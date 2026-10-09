[CmdletBinding()]
param(
  [string]$Directory='',
  [ValidateSet('CreateCandidate','Candidate','TrustedSigned')][string]$Mode='Candidate',
  [string[]]$ExpectedPublisherThumbprint=@(),
  [string]$SignToolPath='',
  [string]$ReportPath='',
  [switch]$LibraryOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Get-ReleaseHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Test-ReleasePe([string]$Path) {
  $stream=[IO.File]::OpenRead($Path)
  try {return ($stream.Length -ge 2 -and $stream.ReadByte() -eq 77 -and $stream.ReadByte() -eq 90)} finally {$stream.Dispose()}
}
function Get-ReleasePath([string]$Root,[string]$Relative) {
  if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)' -or $Relative.Contains(':')) {throw "Unsafe manifest path: $Relative"}
  $full=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
  if (!$full.StartsWith($Root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Manifest path escapes package'}
  return $full
}
function Get-ReleaseFiles([string]$Root) {
  $rootItem=Get-Item -LiteralPath $Root -Force
  if (!$rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Package must be an ordinary directory, not a link'}
  $queue=New-Object 'System.Collections.Generic.Queue[string]'; $queue.Enqueue($Root)
  while ($queue.Count) {
    foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
      if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Reparse point is not allowed: $($item.Name)"}
      if ($item.PSIsContainer) {$queue.Enqueue($item.FullName)} else {$item}
    }
  }
}
function Get-PeFingerprint([string]$Path,[long]$OriginalLength=0) {
  $b=[IO.File]::ReadAllBytes($Path)
  if ($b.Length -lt 256 -or $b[0] -ne 77 -or $b[1] -ne 90) {throw 'Not a valid PE candidate'}
  $pe=[BitConverter]::ToInt32($b,60)
  if ($pe -lt 64 -or $pe+152 -gt $b.Length -or [BitConverter]::ToUInt32($b,$pe) -ne 17744) {throw 'Invalid PE header'}
  $opt=$pe+24; $magic=[BitConverter]::ToUInt16($b,$opt)
  if ($magic -eq 523) {$sec=$opt+112+32} elseif ($magic -eq 267) {$sec=$opt+96+32} else {throw 'Unsupported PE optional header'}
  if ($sec+8 -gt $b.Length) {throw 'Truncated PE security directory'}
  $certAt=[BitConverter]::ToUInt32($b,$sec); $certSize=[BitConverter]::ToUInt32($b,$sec+4)
  if (!$OriginalLength) {$OriginalLength=$b.Length}
  if ($OriginalLength -gt $b.Length -or $OriginalLength -lt $sec+8) {throw 'PE body length changed'}
  if ($b.Length -gt $OriginalLength) {
    if ($certAt -lt $OriginalLength -or $certAt -gt $OriginalLength+7 -or [long]$certAt+$certSize -ne $b.Length -or !$certSize) {throw 'Signing changed PE payload or added an unexpected overlay'}
    for ($n=$OriginalLength;$n -lt $certAt;$n++) {if ($b[$n] -ne 0) {throw 'Nonzero data was appended before certificate'}}
  }
  $normalized=New-Object byte[] $OriginalLength; [Array]::Copy($b,$normalized,$OriginalLength)
  [Array]::Clear($normalized,$opt+64,4); [Array]::Clear($normalized,$sec,8)
  $sha=[Security.Cryptography.SHA256]::Create()
  try {return ([BitConverter]::ToString($sha.ComputeHash($normalized))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
}
function Test-ReleaseSignatureEvidence($Evidence,[string[]]$AllowedThumbprints,[bool]$TimestampRequired=$true) {
  # Pure predicate for data-only tests. Production evidence comes from Authenticode and SignTool.
  if ($Evidence.Status -ne 'Valid' -or !$Evidence.Thumbprint) {return $false}
  if ($TimestampRequired -and !$Evidence.TimestampThumbprint) {return $false}
  return @($AllowedThumbprints | Where-Object {$_ -eq $Evidence.Thumbprint}).Count -gt 0
}
function Get-ReleaseSignature([string]$Path) {
  $sig=Get-AuthenticodeSignature -LiteralPath $Path
  $embedded=$null
  try {$embedded=New-Object Security.Cryptography.X509Certificates.X509Certificate2 ([Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($Path))} catch {}
  # Windows prefers catalog evidence when both signatures exist. Record the embedded
  # signer separately; SignTool /pa without catalog options verifies that signature.
  [pscustomobject]@{Status=$sig.Status.ToString();Thumbprint=if($embedded){$embedded.Thumbprint}else{''};Subject=if($embedded){$embedded.Subject}else{''};Issuer=if($embedded){$embedded.Issuer}else{''};TimestampThumbprint=if($sig.TimeStamperCertificate){$sig.TimeStamperCertificate.Thumbprint}else{''};SignatureType=$sig.SignatureType.ToString();WindowsReportedThumbprint=if($sig.SignerCertificate){$sig.SignerCertificate.Thumbprint}else{''}}
  if($embedded){$embedded.Dispose()}
}
if ($LibraryOnly) {return}
if (!$Directory) {throw 'Directory is required'}
$root=[IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$metadata=@('candidate-manifest.json','release-manifest.json','SHA256SUMS.txt','RELEASE.READY.json')
$manifestName=if($Mode -eq 'TrustedSigned'){'release-manifest.json'}else{'candidate-manifest.json'}
if ($ReportPath) {
  $ReportPath=[IO.Path]::GetFullPath($ReportPath)
  if ($ReportPath.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Report must be outside the package'}
}
$actual=@(Get-ReleaseFiles $root | Where-Object {$_.FullName.Substring($root.Length+1).Replace('\','/') -notin $metadata} | Sort-Object FullName)
foreach($f in $actual){
  if($f.Extension -in @('.ps1','.psm1','.bat','.cmd','.vbs','.js','.msi','.msix','.appx','.sys')){throw 'This app-only pipeline does not accept scripts/installers/drivers; they require a separate signing review'}
  if($f.Extension -in @('.exe','.dll') -and !(Test-ReleasePe $f.FullName)){throw 'Executable extension contains non-PE data'}
}
$entries=@(); $errors=New-Object 'System.Collections.Generic.List[string]'
if ($Mode -eq 'CreateCandidate') {
  if (Test-Path -LiteralPath (Join-Path $root $manifestName)) {throw 'Candidate manifest already exists; it must not be silently regenerated'}
  foreach($f in $actual) {
    $relative=$f.FullName.Substring($root.Length+1).Replace('\','/')
    $isPE=Test-ReleasePe $f.FullName
    $sig=if($isPE){Get-ReleaseSignature $f.FullName}else{$null}
    $fingerprint=if($isPE -and $sig.Status -eq 'NotSigned'){Get-PeFingerprint $f.FullName}else{''}
    $entries+=[pscustomobject]@{path=$relative;bytes=$f.Length;sha256=Get-ReleaseHash $f.FullName;kind=if($isPE){'PE'}else{'data'};signature=$sig;unsignedFingerprint=$fingerprint}
  }
  $candidate=[ordered]@{schema='desk-buddy-signing-candidate/1';createdUtc=[DateTime]::UtcNow.ToString('o');layout='self-contained-folder';appExecuted=$false;files=$entries}
  if (@($entries | Where-Object {$_.path -eq 'DeskBuddy.Hub.exe'}).Count -ne 1 -or @($entries | Where-Object {$_.path -eq 'DeskBuddy.Hub.dll'}).Count -ne 1) {throw 'Folder candidate must contain Hub.exe and Hub.dll; single-file release needs a separate pre-bundle signing pipeline'}
  $candidate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root $manifestName) -Encoding utf8
} else {
  $manifest=Get-Content -LiteralPath (Join-Path $root $manifestName) -Raw | ConvertFrom-Json
  $expectedSchema=if($Mode -eq 'TrustedSigned'){'desk-buddy-signed-release/1'}else{'desk-buddy-signing-candidate/1'}
  if ($manifest.schema -ne $expectedSchema -or $manifest.layout -ne 'self-contained-folder') {throw 'Unsupported or hidden-payload release layout'}
  $entries=@($manifest.files)
  $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach($entry in $entries) {
    if (!$seen.Add($entry.path)) {throw 'Duplicate manifest path'}
    $f=Get-ReleasePath $root $entry.path
    if (!(Test-Path -LiteralPath $f -PathType Leaf)) {$errors.Add("Missing: $($entry.path)");continue}
    if ((Get-ReleaseHash $f) -ne $entry.sha256 -or (Get-Item -LiteralPath $f).Length -ne $entry.bytes) {$errors.Add("Hash/size mismatch: $($entry.path)")}
  }
  foreach($file in $actual) {if (!$seen.Contains($file.FullName.Substring($root.Length+1).Replace('\','/'))) {$errors.Add("Unlisted file: $($file.Name)")}}
  if (!$seen.Contains('DeskBuddy.Hub.exe') -or !$seen.Contains('DeskBuddy.Hub.dll')) {$errors.Add('Missing inspectable Hub executable/assembly')}
}
$checks=@()
if ($Mode -eq 'TrustedSigned') {
  if (!$ExpectedPublisherThumbprint.Count) {throw 'An explicitly approved publisher certificate thumbprint is required'}
  if (!$SignToolPath) {$command=Get-Command signtool.exe -ErrorAction SilentlyContinue; if($command){$SignToolPath=$command.Source}}
  if (!$SignToolPath -or !(Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {throw 'Windows SDK SignTool is required for trusted-release verification; no installation is performed automatically'}
  foreach($entry in $entries) {
    $path=Get-ReleasePath $root $entry.path
    if (!(Test-Path -LiteralPath $path)) {continue}
    if (!(Test-ReleasePe $path)) {continue}
    $sig=Get-ReleaseSignature $path
    if(!$sig.Thumbprint){$errors.Add("Embedded signature required for portable release: $($entry.path)")}
    $allowed=if($entry.signing -eq 'preserved-dependency'){@($entry.originalSignerThumbprint)}else{@($ExpectedPublisherThumbprint)}
    if ($entry.path -match '^DeskBuddy\.(Hub\.exe|Hub\.dll|Core\.dll)$') {$allowed=@($ExpectedPublisherThumbprint)}
    # Catalog timestamp choice can differ; SignTool /tw /all below is authoritative
    # for the required embedded timestamp and treats warnings as failures.
    if (!(Test-ReleaseSignatureEvidence $sig $allowed $false)) {$errors.Add("Untrusted or wrong embedded publisher: $($entry.path)")}
    $toolLog=@(& $SignToolPath verify /pa /all /tw /v $path 2>&1 | ForEach-Object {"$_"});$code=$LASTEXITCODE
    if ($code -ne 0) {$errors.Add("SignTool verification/warning exit ${code}: $($entry.path)")}
    $checks+=[pscustomobject]@{path=$entry.path;signature=$sig;signToolExit=$code;details=$toolLog}
  }
}
$result=[pscustomobject]@{schema='desk-buddy-release-verification/1';mode=$Mode;passed=($errors.Count -eq 0);checkedUtc=[DateTime]::UtcNow.ToString('o');files=$entries.Count;errors=@($errors);signatureChecks=$checks;appExecuted=$false;smartAppControlAcceptance='NOT_TESTED; Authenticode validity is not a policy execution guarantee'}
if($ReportPath){[IO.Directory]::CreateDirectory((Split-Path -Parent $ReportPath))|Out-Null;$result|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $ReportPath -Encoding utf8}
if($errors.Count){throw ('Release verification failed: '+($errors -join '; '))}
$result
