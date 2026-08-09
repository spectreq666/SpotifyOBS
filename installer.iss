#define MyAppName "Spectreq OBS Spotify"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "spectreq"
#define MyAppURL "https://github.com"
#define MyAppExeName "Spotify OBS Overlay.exe"

[Setup]
AppId={{D3A5F8E1-2C9B-4D1E-8B7A-6F5E4D3C2B1A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=SpectreqOBS-Spotify-Setup
SetupIconFile=Assets\Logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Автоматически запускать при входе в Windows"; GroupDescription: "Дополнительные настройки:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[UninstallDelete]
Type: filesandordirs; Name: "{app}\*"
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  AppDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('taskkill.exe', '/F /IM "Spotify OBS Overlay.exe"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    AppDir := ExpandConstant('{app}');
    Exec('cmd.exe', '/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q "' + AppDir + '"', '', SW_HIDE, ewNoWait, ResultCode);
  end;
end;
