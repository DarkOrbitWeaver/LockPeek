[Setup]
AppName=LockPeek
AppVersion=0.4.34
AppPublisher=DarkOrbitWeaver
AppPublisherURL=https://github.com/DarkOrbitWeaver/LockPeek
AppSupportURL=https://github.com/DarkOrbitWeaver/LockPeek/issues
DefaultDirName={autopf}\LockPeek
DefaultGroupName=LockPeek
UninstallDisplayIcon={app}\LockPeek.exe
OutputDir=..\artifacts\installer
OutputBaseFilename=LockPeek-Setup-v0.4.34
SetupIconFile=..\assets\brand\lockpeek.ico
WizardImageFile=..\assets\brand\installer-side.bmp
WizardSmallImageFile=..\assets\brand\installer-top.bmp
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.22000
PrivilegesRequired=lowest
WizardStyle=modern
CloseApplications=yes

[Files]
Source: "..\artifacts\portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\artifacts\native\LockPeek.Shell.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\LockPeek-sparse.msix"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\context-menu\*.ico"; DestDir: "{app}\assets\context-menu"; Flags: ignoreversion

[Icons]
Name: "{group}\LockPeek"; Filename: "{app}\LockPeek.exe"
Name: "{group}\Uninstall LockPeek"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LockPeek"; Filename: "{app}\LockPeek.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-AppxPackage -Path '{app}\LockPeek-sparse.msix' -ExternalLocation '{app}'"""; StatusMsg: "Registering Explorer context menu..."; Flags: runhidden waituntilterminated
Filename: "{app}\LockPeek.exe"; Description: "Launch LockPeek"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name 'LockPeek.Desktop' | Remove-AppxPackage"""; RunOnceId: "RemoveSparse"; Flags: runhidden waituntilterminated
