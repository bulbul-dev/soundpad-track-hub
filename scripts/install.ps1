<#
.SYNOPSIS
  Сборка и установка Soundpad Track Hub для текущего пользователя (без прав администратора).

.DESCRIPTION
  1. dotnet publish (Release, win-x64, framework-dependent: нужен .NET 8 Desktop Runtime).
  2. Копирование в %LOCALAPPDATA%\Programs\SoundpadTrackHub.
  3. Ярлыки «Soundpad Track Hub» в меню «Пуск» и (по желанию) на рабочем столе.
  4. Запись в «Установка и удаление программ» (HKCU) — удаление запускает SoundpadTrackHub.exe --uninstall.
  5. По желанию — автозапуск Windows (HKCU\...\Run).

  Soundpad, его файлы и его реестр не затрагиваются.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Desktop -Autostart
#>
param(
    [switch]$Desktop,
    [switch]$Autostart,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SoundpadTrackHub\SoundpadTrackHub.csproj'
$publish = Join-Path $root 'artifacts\publish'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\SoundpadTrackHub'
$exe = Join-Path $dest 'SoundpadTrackHub.exe'

if (Get-Process -Name SoundpadTrackHub -ErrorAction SilentlyContinue) {
    Write-Host 'Soundpad Track Hub запущен. Закройте его (значок в области уведомлений -> «Выход») и запустите установку снова.' -ForegroundColor Yellow
    exit 1
}

Write-Host '==> Сборка одного файла SoundpadTrackHub.exe (dotnet publish)...'
$sc = if ($SelfContained) { 'true' } else { 'false' }
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
# Один exe: все библиотеки (WebView2, NAudio, Serilog) упакованы внутрь,
# нативный WebView2Loader.dll распаковывается автоматически при запуске.
dotnet publish $project -c Release -r win-x64 --self-contained $sc -o $publish `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:GenerateDocumentationFile=false
if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
Get-ChildItem -LiteralPath $publish -Filter *.xml | Remove-Item -Force

if (Test-Path -LiteralPath (Join-Path $dest 'Soundpad.exe')) {
    throw "В папке $dest найден Soundpad.exe — установка отменена, чтобы не затронуть файлы Soundpad."
}

Write-Host "==> Установка в $dest"
if (Test-Path -LiteralPath $dest) {
    # Удаляем только старую версию Track Hub (папка принадлежит Track Hub).
    Get-ChildItem -LiteralPath $dest -Force | Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $dest -Recurse -Force

Write-Host '==> Ярлыки'
$shell = New-Object -ComObject WScript.Shell
$folders = @([Environment]::GetFolderPath('Programs'))
if ($Desktop) { $folders += [Environment]::GetFolderPath('DesktopDirectory') }
foreach ($folder in $folders) {
    $lnk = $shell.CreateShortcut((Join-Path $folder 'Soundpad Track Hub.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $dest
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Hitmoz • Soundpad — companion-панель для Soundpad'
    $lnk.Save()
}

Write-Host '==> Запись «Установка и удаление программ» (HKCU)'
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundpadTrackHub'
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name DisplayName -Value 'Soundpad Track Hub'
Set-ItemProperty -Path $key -Name DisplayVersion -Value $version
Set-ItemProperty -Path $key -Name Publisher -Value 'Soundpad Track Hub'
Set-ItemProperty -Path $key -Name DisplayIcon -Value $exe
Set-ItemProperty -Path $key -Name InstallLocation -Value $dest
Set-ItemProperty -Path $key -Name UninstallString -Value "`"$exe`" --uninstall"
Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord

if ($Autostart) {
    Write-Host '==> Автозапуск Windows (HKCU\...\Run)'
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name SoundpadTrackHub -Value "`"$exe`" --autostart"
}

Write-Host ''
Write-Host "Готово. Запуск: $exe" -ForegroundColor Green
