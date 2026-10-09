[CmdletBinding()]
param([string]$DotnetPath='', [switch]$Publish, [string]$OutputDirectory='', [switch]$RunCoreTests, [switch]$ForSigning)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$deskRoot=Split-Path -Parent $PSScriptRoot
$releaseRoot=[IO.Path]::GetFullPath((Join-Path $deskRoot 'outputs/hub-r9'))
if (!$DotnetPath) {
  $localSdk=Join-Path $deskRoot 'tools/dotnet/dotnet.exe'
  if (Test-Path -LiteralPath $localSdk) {$DotnetPath=$localSdk}
  else {$DotnetPath=(Get-Command dotnet -ErrorAction Stop).Source}
}
# No Hub execution. Only explicitly requested Core tests may run.
if ($RunCoreTests) {
  & $DotnetPath run --project (Join-Path $PSScriptRoot 'DeskBuddy.Core.Tests/DeskBuddy.Core.Tests.csproj') -c Release
  if ($LASTEXITCODE -ne 0) {throw 'Core tests failed'}
}
$destination=if ($OutputDirectory) {[IO.Path]::GetFullPath($OutputDirectory)} elseif($ForSigning) {Join-Path $releaseRoot 'unsigned-candidate'} else {Join-Path $releaseRoot 'windows'}
if ($Publish) {
  if (!$destination.StartsWith($releaseRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Publish output must be a new directory inside outputs/hub-r9; historical releases are protected.'}
  if ((Test-Path -LiteralPath $destination) -and @(Get-ChildItem -LiteralPath $destination -Force).Count) {throw 'Publish output is not empty. Choose a new R9 directory; no automatic deletion or overwrite.'}
}
& $DotnetPath build (Join-Path $PSScriptRoot 'DeskBuddy.App/DeskBuddy.App.csproj') -c Release
if ($LASTEXITCODE -ne 0) {throw 'App build failed'}
if ($Publish) {
  # Normal local builds retain the app's single-file layout. The optional signing
  # preparation exposes DLLs for review; neither path executes the application.
  $layoutArgs=if($ForSigning){@('-p:PublishSingleFile=false','-p:IncludeNativeLibrariesForSelfExtract=false')}else{@('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true')}
  & $DotnetPath publish (Join-Path $PSScriptRoot 'DeskBuddy.App/DeskBuddy.App.csproj') -c Release -r win-x64 --self-contained true @layoutArgs -o $destination
  if ($LASTEXITCODE -ne 0) {throw 'Publish failed; output is not a completed build.'}
  if($ForSigning){
    & (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $destination -Mode CreateCandidate -ReportPath (Join-Path $releaseRoot 'signing/candidate-inspection.json')
  }
  $app=Join-Path $destination 'DeskBuddy.Hub.exe'
  $signature=(Get-AuthenticodeSignature -LiteralPath $app).Status
  Write-Output "LOCAL BUILD COMPLETE: $destination. Authenticode: $signature. Hub was not started. Signing is optional for a future trusted distribution workflow, not a local build requirement."
}
