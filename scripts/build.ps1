param(
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'src/RazerBookRgb/RazerBookRgb.csproj'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'dist'
}
$releaseDir = [System.IO.Path]::GetFullPath($OutputDirectory)
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$exe = Join-Path $releaseDir 'RazerBookRGB.exe'
foreach ($mode in @('--self-test', '--preview')) {
    $check = Start-Process -FilePath $exe -ArgumentList $mode -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw "$mode failed; see $releaseDir/error.txt." }
}
Get-Content (Join-Path $releaseDir 'self-test-result.txt')
Get-Content (Join-Path $releaseDir 'ui-test-result.txt')
Copy-Item (Join-Path $repoRoot 'README.md') (Join-Path $releaseDir 'README.md') -Force
Get-Item $exe | Select-Object FullName,Length
