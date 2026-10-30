param([switch]$Settings)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdkExe = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:LOCALAPPDATA 'ExternalIpWidget.Build\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdkExe)) { throw 'Install .NET 10 Desktop Runtime or SDK: https://dotnet.microsoft.com/download/dotnet/10.0' }
$appDll = Join-Path $PSScriptRoot 'artifacts\dotnet-desktop\ExternalIpWidget.dll'
if (-not (Test-Path -LiteralPath $appDll)) { $appDll = Join-Path $PSScriptRoot 'ExternalIpWidget.dll' }
if (-not (Test-Path -LiteralPath $appDll)) { $appDll = Join-Path $PSScriptRoot 'src\ExternalIp.Widget\bin\Release\net10.0-windows\ExternalIpWidget.dll' }
if (-not (Test-Path -LiteralPath $appDll)) { throw 'Build the application or extract the dotnet-desktop release first.' }
if ($Settings) { & $sdkExe $appDll --settings } else { & $sdkExe $appDll }
