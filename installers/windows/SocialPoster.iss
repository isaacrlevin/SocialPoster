; Inno Setup script for the SocialPoster desktop app (self-contained, no .NET install needed).
; Built by CI with: iscc /DAppVersion=1.2.3 /DArch=x64|arm64 /DSourceDir=<publish dir> /DOutputDir=<dir> SocialPoster.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error SourceDir must be defined
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "SocialPoster"
#define AppExe "SocialPoster.MAUI.exe"

[Setup]
; Keep this GUID stable so upgrades replace the previous install.
AppId={{6F0C2B7E-3D55-4B0E-9A41-5C1D7E2A9B10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Isaac Levin
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#OutputDir}
OutputBaseFilename=SocialPoster-windows-{#Arch}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
