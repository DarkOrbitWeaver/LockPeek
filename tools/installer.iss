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

[Files]
Source: "..\artifacts\portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\LockPeek"; Filename: "{app}\LockPeek.exe"
Name: "{group}\Uninstall LockPeek"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LockPeek"; Filename: "{app}\LockPeek.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\LockPeek.exe"; Description: "Launch LockPeek"; Flags: nowait postinstall skipifsilent
