; Inno Setup script for the Valheim World Keeper helper.
; Built by CI (see .github/workflows/release.yml); the version is passed in with /DMyAppVersion.

#define MyAppName "Valheim World Keeper"
#define MyAppExeName "ValheimWorldKeeper.exe"
#define MyAppPublisher "Kristian"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
; Keep this GUID stable across versions so upgrades replace the same install.
AppId={{B7E4C2A1-9F3D-4E6B-8A2C-1D5F7E9A0C34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Per-user install — no admin prompt, friendlier for non-technical friends.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ValheimWorldKeeper
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=ValheimWorldKeeper-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
; Offered to the user, ticked by default (omit "unchecked" flag = pre-checked).
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
