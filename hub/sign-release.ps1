[CmdletBinding()]
param(
  [ValidateSet('PrepareExternal','CompleteExternal','Certificate')][string]$Mode='PrepareExternal',
  [string]$CandidateDirectory='', [string]$StagingDirectory='', [string]$OutputDirectory='',
  [string]$CertificateThumbprint='', [ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStore='CurrentUser',
  [string[]]$ExpectedPublisherThumbprint=@(), [string]$SignToolPath='',
  [string]$TimestampUrl='http://timestamp.digicert.com'
)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$cfg=[pscustomobject]@{Mode=$Mode;Candidate=$CandidateDirectory;Stage=$StagingDirectory;Output=$OutputDirectory;Thumb=$CertificateThumbprint;Store=$CertificateStore;Expected=@($ExpectedPublisherThumbprint);Tool=$SignToolPath;Timestamp=$TimestampUrl}
$verify=Join-Path $PSScriptRoot 'verify-release.ps1'
. $verify -LibraryOnly
$releaseRoot=[IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'outputs/hub-r6'))
function Resolve-R5Location([string]$Value,[string]$Default) {
  $p=if($Value){[IO.Path]::GetFullPath($Value).TrimEnd('\')}else{Join-Path $releaseRoot $Default}
  if (!$p.StartsWith($releaseRoot+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'All signing paths must be inside outputs/hub-r6; old releases are protected'}
  $ancestor=$p
  while($ancestor -and !(Test-Path -LiteralPath $ancestor)) {$ancestor=Split-Path -Parent $ancestor}
  while($ancestor) {if((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Signing paths cannot pass through reparse points'};$ancestor=Split-Path -Parent $ancestor}
  return $p
}
$candidateRoot=Resolve-R5Location $cfg.Candidate 'unsigned-candidate'
$stageRoot=Resolve-R5Location $cfg.Stage 'signing-stage'
$outputRoot=Resolve-R5Location $cfg.Output 'signed-release'
foreach($a in @($candidateRoot,$stageRoot,$outputRoot)) {foreach($b in @($candidateRoot,$stageRoot,$outputRoot)) {if($a -ne $b -and $a.StartsWith($b+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate, stage, and release directories cannot nest'}}}
if (@($candidateRoot,$stageRoot,$outputRoot | Select-Object -Unique).Count -ne 3) {throw 'Candidate, stage, and release must be different directories'}
& $verify -Directory $candidateRoot -Mode Candidate | Out-Null
$candidateManifest=Join-Path $candidateRoot 'candidate-manifest.json'
$candidate=Get-Content -LiteralPath $candidateManifest -Raw | ConvertFrom-Json
$candidateHash=Get-ReleaseHash $candidateManifest
if ($cfg.Mode -eq 'Certificate') {
  $cfg.Thumb=($cfg.Thumb -replace '\s','').ToUpperInvariant()
  if($cfg.Thumb -notmatch '^[A-F0-9]{40}$') {throw 'BLOCKED: provide an existing approved code-signing certificate thumbprint. No certificate is created or imported.'}
  $cert=Get-Item -LiteralPath ("Cert:\{0}\My\{1}" -f $cfg.Store,$cfg.Thumb) -ErrorAction Stop
  if(!$cert.HasPrivateKey -or $cert.NotBefore -gt (Get-Date) -or $cert.NotAfter -le (Get-Date)) {throw 'Certificate has no accessible private key or is outside its validity period'}
  if($cert.Subject -eq $cert.Issuer) {throw 'Self-signed leaf certificates are not accepted for this release pipeline'}
  if(!@($cert.EnhancedKeyUsageList | Where-Object {$_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3'}).Count) {throw 'Certificate lacks the Code Signing EKU'}
  $chain=New-Object Security.Cryptography.X509Certificates.X509Chain
  try {$chain.ChainPolicy.RevocationMode='Online';$chain.ChainPolicy.UrlRetrievalTimeout=[TimeSpan]::FromSeconds(15);if(!$chain.Build($cert)){throw 'Signing certificate trust/revocation chain could not be verified'}} finally {$chain.Dispose()}
  $cfg.Expected=@($cfg.Thumb)
  $uri=$null;if(![Uri]::TryCreate($cfg.Timestamp,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -notin @('http','https')){throw 'TimestampUrl must be an explicit HTTP(S) RFC3161 service'}
}
if($cfg.Mode -ne 'PrepareExternal') {
  if(!$cfg.Expected.Count -or @($cfg.Expected | Where-Object {$_ -notmatch '^[A-Fa-f0-9]{40}$'}).Count) {throw 'BLOCKED: explicitly approved publisher thumbprint(s) are required; no automatic signer guessing'}
  if(!$cfg.Tool){$tool=Get-Command signtool.exe -ErrorAction SilentlyContinue;if($tool){$cfg.Tool=$tool.Source}}
  if(!$cfg.Tool -or !(Test-Path -LiteralPath $cfg.Tool -PathType Leaf)){throw 'BLOCKED: specify Windows SDK SignTool. This script does not install tools.'}
}
$needsSigning=@($candidate.files | Where-Object {$_.kind -eq 'PE' -and $_.signature.Status -eq 'NotSigned'})
foreach($file in $candidate.files) {
  if($file.kind -eq 'PE' -and !$file.signature.PSObject.Properties['WindowsReportedThumbprint']){throw 'Candidate uses an older signer inventory. Rebuild a fresh -ForSigning candidate before signing; existing snapshots remain unchanged.'}
  if($file.kind -eq 'PE' -and $file.signature.Status -notin @('Valid','NotSigned')){throw "Invalid pre-existing signature: $($file.path). Do not overwrite a damaged signature."}
  if($file.kind -eq 'PE' -and $file.signature.Status -eq 'Valid' -and !$file.signature.Thumbprint){throw "No embedded signer; catalog-only dependency needs a separate portability review: $($file.path)"}
}
if($cfg.Mode -ne 'CompleteExternal') {
  if(Test-Path -LiteralPath $stageRoot){throw 'Signing stage already exists; choose a new stage directory. Nothing is deleted.'}
  [IO.Directory]::CreateDirectory($stageRoot)|Out-Null
  foreach($file in $candidate.files){$dest=Get-ReleasePath $stageRoot $file.path;[IO.Directory]::CreateDirectory((Split-Path -Parent $dest))|Out-Null;Copy-Item -LiteralPath (Get-ReleasePath $candidateRoot $file.path) -Destination $dest}
  $request=[ordered]@{schema='desk-buddy-external-signing-request/1';candidateManifestSha256=$candidateHash;files=@($needsSigning|Select-Object path,sha256,bytes,unsignedFingerprint);requirements=@('Sign only listed unsigned PE files with an approved publisher identity','SHA256 file digest plus RFC3161 SHA256 timestamp','Do not alter executable payload or existing signed dependencies','Do not add files, secrets, PFX files, scripts, or launch the app')}
  $requestPath=$stageRoot+'.signing-request.json';$request|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $requestPath -Encoding utf8
  if($cfg.Mode -eq 'PrepareExternal'){Write-Output "PREPARED, NOT SIGNED OR RELEASED: $stageRoot ; request: $requestPath";return}
} else {
  $request=Get-Content -LiteralPath ($stageRoot+'.signing-request.json') -Raw | ConvertFrom-Json
  if($request.schema -ne 'desk-buddy-external-signing-request/1' -or $request.candidateManifestSha256 -ne $candidateHash){throw 'External stage does not match the immutable candidate'}
}
if(Test-Path -LiteralPath $outputRoot){throw 'Signed release destination already exists; choose a new destination'}
# Reject extra payloads before calling any signer, then pin source bytes.
$expected=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($file in $candidate.files){[void]$expected.Add($file.path)}
foreach($f in Get-ReleaseFiles $stageRoot){$rel=$f.FullName.Substring($stageRoot.Length+1).Replace('\','/');if(!$expected.Contains($rel)){throw "Unexpected staged file: $rel"}}
if($cfg.Mode -eq 'Certificate') {
  foreach($file in $candidate.files){if((Get-ReleaseHash (Get-ReleasePath $stageRoot $file.path)) -ne $file.sha256){throw 'Candidate changed during staging'}}
  foreach($file in $needsSigning) {
    $args=@('sign','/sha1',$cfg.Thumb,'/s','My','/fd','SHA256','/tr',$cfg.Timestamp,'/td','SHA256')
    if($cfg.Store -eq 'LocalMachine'){$args+='/sm'}
    $args+=Get-ReleasePath $stageRoot $file.path
    & $cfg.Tool @args
    if($LASTEXITCODE -ne 0){throw "Signing failed or returned a warning: $($file.path). No release was created."}
  }
}
$signedFiles=@()
foreach($file in $candidate.files) {
  $path=Get-ReleasePath $stageRoot $file.path
  $signing=if($file.kind -ne 'PE'){'data'}elseif($file.signature.Status -eq 'Valid'){'preserved-dependency'}else{'publisher-signed'}
  if($signing -eq 'publisher-signed') {
    if((Get-PeFingerprint $path $file.bytes) -ne $file.unsignedFingerprint){throw "Executable payload changed beyond Authenticode fields: $($file.path)"}
  } elseif((Get-ReleaseHash $path) -ne $file.sha256){throw "Data or pre-signed dependency was modified: $($file.path)"}
  $signedFiles+=[pscustomobject]@{path=$file.path;bytes=(Get-Item -LiteralPath $path).Length;sha256=Get-ReleaseHash $path;kind=$file.kind;signing=$signing;originalSha256=$file.sha256;originalSignerThumbprint=if($file.signature){$file.signature.Thumbprint}else{''}}
}
$manifest=[ordered]@{schema='desk-buddy-signed-release/1';layout='self-contained-folder';createdUtc=[DateTime]::UtcNow.ToString('o');candidateManifestSha256=$candidateHash;files=$signedFiles;appExecuted=$false}
$manifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $stageRoot 'release-manifest.json') -Encoding utf8
$report=Join-Path $releaseRoot 'signing/signed-stage-verification.json'
& $verify -Directory $stageRoot -Mode TrustedSigned -ExpectedPublisherThumbprint $cfg.Expected -SignToolPath $cfg.Tool -ReportPath $report | Out-Null
# Promotion occurs only after every signature and hash check passes. No binary is executed.
[IO.Directory]::CreateDirectory($outputRoot)|Out-Null
foreach($file in @($signedFiles.path)+@('release-manifest.json')){$dest=Get-ReleasePath $outputRoot $file;[IO.Directory]::CreateDirectory((Split-Path -Parent $dest))|Out-Null;Copy-Item -LiteralPath (Get-ReleasePath $stageRoot $file) -Destination $dest}
& $verify -Directory $outputRoot -Mode TrustedSigned -ExpectedPublisherThumbprint $cfg.Expected -SignToolPath $cfg.Tool -ReportPath (Join-Path $releaseRoot 'signing/release-verification.json') | Out-Null
$sum=@(Get-ReleaseFiles $outputRoot|Sort-Object FullName|ForEach-Object {(Get-ReleaseHash $_.FullName)+'  '+$_.FullName.Substring($outputRoot.Length+1).Replace('\','/')})
$sum|Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding utf8
@{status='AUTHENTICODE_VERIFIED_POLICY_ACCEPTANCE_PENDING';manifestSha256=Get-ReleaseHash (Join-Path $outputRoot 'release-manifest.json');sha256ListHash=Get-ReleaseHash (Join-Path $outputRoot 'SHA256SUMS.txt');appExecuted=$false;note='Not a Smart App Control execution guarantee. Normal launch requires separate authorized validation.'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outputRoot 'RELEASE.READY.json') -Encoding utf8
Write-Output "AUTHENTICODE VERIFIED: $outputRoot ; policy execution acceptance remains pending. Hub was not started."
