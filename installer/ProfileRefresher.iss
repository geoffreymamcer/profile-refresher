; Installer for Browser Profile Refresher (Inno Setup 6.3 or newer).
; Built by GitHub Actions; to build by hand, publish the app into ..\publish\app first (see build.yml).
;
; Installs for the current Windows user only, so it never asks for administrator rights.
; Settings and chat logs live in %AppData% and Documents, so updating or uninstalling keeps them.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6C0B6C1E-5B7A-4E0B-9F4E-2D2B7C1F5A90}
AppName=Browser Profile Refresher
AppVersion={#AppVersion}
AppVerName=Browser Profile Refresher {#AppVersion}
AppPublisher=Jeymeyzer. XYZ Chatter (Team Mel)
AppCopyright=© 2026 Jeymeyzer. XYZ Chatter (Team Mel)
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Browser Profile Refresher Setup
DefaultDirName={localappdata}\Programs\ProfileRefresher
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=ProfileRefresher-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Browser Profile Refresher
UninstallDisplayIcon={app}\ProfileRefresher.exe
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[InstallDelete]
; On update, remove extension files that no longer exist in the new version.
Type: filesandordirs; Name: "{app}\extension"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Browser Profile Refresher"; Filename: "{app}\ProfileRefresher.exe"
Name: "{autoprograms}\Browser Profile Refresher extension folder"; Filename: "{app}\extension"
Name: "{autodesktop}\Browser Profile Refresher"; Filename: "{app}\ProfileRefresher.exe"; Tasks: desktopicon

[Run]
; After an update started from the app (/RELAUNCH=1), start the app again.
Filename: "{app}\ProfileRefresher.exe"; Flags: nowait; Check: Relaunch
Filename: "{app}\ProfileRefresher.exe"; Description: "Start Browser Profile Refresher"; Flags: nowait postinstall skipifsilent
Filename: "{app}\extension"; Description: "Open the extension folder (for Load unpacked in Chrome or Edge)"; Flags: postinstall shellexec skipifsilent unchecked

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
