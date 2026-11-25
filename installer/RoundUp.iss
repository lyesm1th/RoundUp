; RoundUp Windows installer, per-user and without admin rights.
; This installer installs the self-updating launcher; the main RoundUp application
; is installed from verified GitHub Releases on first launch.

#define AppName "RoundUp"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{D6B6873A-9634-4B04-89A1-6175960BC437}
AppName={#AppName}
AppVerName={#AppName} {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=RoundUp
AppPublisherURL=https://github.com/lyesm1th/RoundUp
AppSupportURL=https://github.com/lyesm1th/RoundUp/issues
DefaultDirName={localappdata}\RoundUp
DefaultGroupName=RoundUp
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist\setup
OutputBaseFilename=RoundUpSetup-win-x64
SetupIconFile=..\assets\RoundUp.ico
UninstallDisplayIcon={app}\RoundUpLauncher.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
DirExistsWarning=no
CloseApplications=yes

[Files]
Source: "..\dist\launcher\RoundUpLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion

; Project data and backups are independent of the program update cycle.
; They survive both updates and uninstall unless the user deletes them manually.
[Dirs]
Name: "{app}\app"
Name: "{app}\projects"
Name: "{app}\photos"
Name: "{app}\imports"
Name: "{app}\updates"
Name: "{app}\backups"
Name: "{app}\backups\app"
Name: "{app}\backups\projects"
Name: "{app}\logs"

[Icons]
Name: "{autoprograms}\RoundUp"; Filename: "{app}\RoundUpLauncher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\RoundUpLauncher.exe"
Name: "{autodesktop}\RoundUp"; Filename: "{app}\RoundUpLauncher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\RoundUpLauncher.exe"

[Run]
Filename: "{app}\RoundUpLauncher.exe"; Description: "Запустити RoundUp"; Flags: nowait postinstall skipifsilent
