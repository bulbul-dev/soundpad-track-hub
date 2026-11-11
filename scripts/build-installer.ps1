<#
.SYNOPSIS
  Собирает установщик artifacts\installer\SoundpadTrackHub-Setup-<версия>.exe.

.DESCRIPTION
  1. dotnet publish: один SoundpadTrackHub.exe со встроенным .NET 8 (self-contained),
     чтобы у пользователя ничего не нужно было доустанавливать (кроме WebView2, который есть в Windows 10/11).
  2. Inno Setup (ISCC.exe) упаковывает всё в один Setup.exe с пользовательским соглашением.

  Нужен Inno Setup 6: winget install JRSoftware.InnoSetup

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SoundpadTrackHub\SoundpadTrackHub.csproj'
$publishDir = Join-Path $root 'artifacts\installer-input'
$iss = Join-Path $root 'installer\SoundpadTrackHub.iss'

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Не найден Inno Setup 6. Установите: winget install JRSoftware.InnoSetup' }

[xml]$csproj = Get-Content -LiteralPath $project -Encoding UTF8
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "==> Версия $version"

Write-Host '==> dotnet publish (self-contained, один exe)'
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой.' }
Get-ChildItem -LiteralPath $publishDir -Filter *.xml | Remove-Item -Force

# Inno Setup правильно показывает русский текст соглашения, если файл в UTF-8 с BOM.
$agreement = Join-Path $root 'installer\AGREEMENT.ru.txt'
$text = [IO.File]::ReadAllText($agreement, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($agreement, $text, (New-Object Text.UTF8Encoding($true)))

Write-Host '==> Inno Setup'
& $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup завершился с ошибкой.' }

$setup = Join-Path $root "artifacts\installer\SoundpadTrackHub-Setup-$version.exe"
Write-Host ''
Write-Host "Готово: $setup" -ForegroundColor Green
