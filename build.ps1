param(
    [ValidateSet('win-x64','win-x86','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdkExe = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:LOCALAPPDATA 'ExternalIpWidget.Build\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdkExe)) { throw 'Install .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0' }
Push-Location -LiteralPath $PSScriptRoot
try {
    & $sdkExe build ExternalIpWidget.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $sdkExe run --project tests/ExternalIp.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    $mode = if ($FrameworkDependent) { 'framework-dependent' } else { 'portable' }
    $output = Join-Path $PSScriptRoot "artifacts\$Runtime-$mode"
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    & $sdkExe publish src/ExternalIp.Widget -c Release -r $Runtime --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Output "Executable: $output\ExternalIpWidget.exe"
} finally { Pop-Location }
