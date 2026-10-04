<#
    Сборка установщика: один файл .msi со встроенным приложением.

    Приложение публикуется как self-contained single-file — на целевой машине
    .NET ставить не нужно. Для локальных реестров NPM/NuGet нужен Node.js
    (приложение само об этом сообщает).

    Запуск:   pwsh -File Installer/build.ps1
    Версия:   pwsh -File Installer/build.ps1 -Version 1.1.0

    Требуется WiX 5:  dotnet tool install --global wix
                      wix extension add -g WixToolset.UI.wixext
#>
param(
    [string]$Version = '1.0.0',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path $PSScriptRoot -Parent
$publishIn = Join-Path $root 'publish/app'
$outDir    = Join-Path $PSScriptRoot 'out'
$msi       = Join-Path $outDir "PackageDownloader-$Version-x64.msi"

New-Item -ItemType Directory -Force $outDir | Out-Null

if (-not $SkipPublish) {
    Write-Host '[1/3] Публикация приложения (self-contained, один файл)…' -ForegroundColor Cyan
    dotnet publish (Join-Path $root 'PackageDownloader.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:Version=$Version `
        -o $publishIn | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }
}

Write-Host '[2/3] Графика мастера установки…' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'make-art.ps1') | Out-Null

Write-Host '[3/3] Сборка MSI…' -ForegroundColor Cyan
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) { $wix = Join-Path $env:USERPROFILE '.dotnet/tools/wix.exe' } else { $wix = $wix.Source }
if (-not (Test-Path $wix)) { throw 'Не найден wix. Установите: dotnet tool install --global wix' }

& $wix build -arch x64 -ext WixToolset.UI.wixext -culture ru-RU `
    -d Version=$Version `
    -d ProjectDir=$root `
    -d InstallerDir=$PSScriptRoot `
    -d PublishDir=$publishIn `
    -o $msi `
    (Join-Path $PSScriptRoot 'PackageDownloader.wxs')
if ($LASTEXITCODE -ne 0) { throw 'wix build завершился с ошибкой' }

$size = [int]((Get-Item $msi).Length / 1MB)
Write-Host ''
Write-Host "Готово: $msi ($size МБ)" -ForegroundColor Green
