; NAMP installer - Apache + PHP 8.5/8.3 + MariaDB + phpMyAdmin, crash-safe, free by Lvato
#define AppVersion "1.0.0"

[Setup]
AppId={{6E7F3B1A-4C2D-4E8B-9A51-2F0C8D7E6A10}
AppName=NAMP
AppVersion={#AppVersion}
AppVerName=NAMP {#AppVersion}
AppPublisher=Lvato
AppPublisherURL=https://lvato.com
AppSupportURL=https://lvato.com
AppCopyright=Free local server by Lvato
DefaultDirName=C:\NAMP
DisableProgramGroupPage=yes
DefaultGroupName=NAMP
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=NAMP-Setup-{#AppVersion}
SetupIconFile=..\assets\namp.ico
UninstallDisplayIcon={app}\NAMP.exe
UninstallDisplayName=NAMP {#AppVersion} (by Lvato)
Compression=lzma2/max
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern
AppMutex=NAMP-ControlPanel-SingleInstance
CloseApplications=no
; Your databases, websites and backups live in the install folder and are never removed by the uninstaller.
UsePreviousAppDir=yes

[Messages]
WelcomeLabel2=This will install NAMP {#AppVersion} - a free local web server by Lvato.%n%nApache 2.4, PHP 8.5 and 8.3, MariaDB 11.8 LTS and phpMyAdmin are included - nothing else to download.%n%nYour databases are protected against power cuts, local sites get HTTPS automatically, and NAMP runs without administrator rights after this setup.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Dirs]
Name: "{app}\www"
Name: "{app}\logs"
Name: "{app}\backups"
Name: "{app}\tmp"
Name: "{app}\config"

[InstallDelete]
; Older NAMP builds kept a single PHP directly in {app}\php - PHP now lives in php\8.5 and php\8.3
Type: files; Name: "{app}\php\*"
Type: filesandordirs; Name: "{app}\php\ext"
Type: filesandordirs; Name: "{app}\php\dev"
Type: filesandordirs; Name: "{app}\php\extras"
Type: filesandordirs; Name: "{app}\php\lib"
Type: filesandordirs; Name: "{app}\php\sasl2"

[Files]
Source: "..\build\NAMP.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\stage\versions.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\build\stage\apache\*"; DestDir: "{app}\apache"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\stage\php\*"; DestDir: "{app}\php"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\stage\mariadb\*"; DestDir: "{app}\mariadb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\stage\phpmyadmin\*"; DestDir: "{app}\phpmyadmin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\stage\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\NAMP"; Filename: "{app}\NAMP.exe"; WorkingDir: "{app}"
Name: "{autoprograms}\NAMP folder"; Filename: "{app}"
Name: "{autodesktop}\NAMP"; Filename: "{app}\NAMP.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft Visual C++ runtime..."; Flags: waituntilterminated
Filename: "{app}\NAMP.exe"; Parameters: "--install-services"; StatusMsg: "Setting up services, HTTPS and folder permissions..."; Flags: runhidden waituntilterminated
; Start NAMP as the normal (non-admin) user
Filename: "{app}\NAMP.exe"; Description: "Start NAMP now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\NAMP.exe"; Parameters: "--uninstall-cleanup"; RunOnceId: "NampCleanup"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{app}\tmp"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { Upgrade: stop the database cleanly and the helper before files are replaced. }
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop NAMP-Helper', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop NAMP-MariaDB', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(5000);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox('NAMP was removed.' #13#10#13#10 +
      'Your websites, databases and backups were kept in:' #13#10 + ExpandConstant('{app}') + #13#10#13#10 +
      'Delete that folder yourself if you no longer need them.', mbInformation, MB_OK);
end;
