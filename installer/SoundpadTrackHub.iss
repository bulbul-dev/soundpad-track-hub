; Установщик Soundpad Track Hub (Inno Setup 6).
; Собирается скриптом scripts\build-installer.ps1 — он сначала делает dotnet publish.
;
; Что делает установщик:
;  - показывает пользовательское соглашение (installer\AGREEMENT.ru.txt), без согласия установка невозможна;
;  - все файлы программы упакованы и сжаты внутри одного Setup.exe (LZMA2), снаружи их не видно;
;  - ставит программу для текущего пользователя в %LOCALAPPDATA%\Programs\SoundpadTrackHub (без прав администратора);
;  - создаёт ярлыки на рабочем столе и в меню «Пуск»;
;  - включает автозапуск, чтобы Track Hub появлялся вместе с Soundpad;
;  - регистрирует удаление в «Установка и удаление программ».

#ifndef AppVersion
  #define AppVersion "2.0.1"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\installer-input"
#endif

#define AppName "Soundpad Track Hub"
#define AppExe  "SoundpadTrackHub.exe"
#define AppUrl  "https://github.com/gff107292-commits/soundpad-track-hub"

[Setup]
; AppId совпадает с именем, которое ожидает встроенное удаление программы (ключ SoundpadTrackHub_is1).
AppId=SoundpadTrackHub
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Soundpad Track Hub contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppComments=Companion-панель для Soundpad. Открытый исходный код, лицензия MIT.
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} — установщик

DefaultDirName={localappdata}\Programs\SoundpadTrackHub
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

LicenseFile=AGREEMENT.ru.txt
SetupIconFile=..\src\SoundpadTrackHub\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern

; Всё упаковано и сжато внутри одного файла установщика.
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

OutputDir=..\artifacts\installer
OutputBaseFilename=SoundpadTrackHub-Setup-{#AppVersion}

; Установщик попросит закрыть запущенный Track Hub (мьютекс одного экземпляра).
AppMutex=Local\SoundpadTrackHub.SingleInstance
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"
Name: "autostart"; Description: "Запускать вместе с Windows (нужно, чтобы Track Hub сам появлялся при запуске Soundpad)"; GroupDescription: "Автозапуск:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "AGREEMENT.ru.txt"; DestDir: "{app}"; DestName: "Пользовательское соглашение.txt"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
; Имя ярлыка совпадает с тем, что удаляет встроенное удаление программы.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Hitmoz • Soundpad — companion-панель для Soundpad"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Hitmoz • Soundpad — companion-панель для Soundpad"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SoundpadTrackHub"; ValueData: """{app}\{#AppExe}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillTrackHub"

[Code]
// Если раньше программа ставилась через scripts\install.ps1, убираем его старую запись
// в «Установка и удаление программ», чтобы не было двух одинаковых строк.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundpadTrackHub');
end;
