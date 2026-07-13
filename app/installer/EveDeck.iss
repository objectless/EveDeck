; Inno Setup script for EveDeck (free, open-source installer compiler -- jrsoftware.org/isinfo.php).
; Produces a normal Windows installer (Setup.exe) as an alternative to the portable zip release.
;
; Per-user install (no admin/UAC prompt), matching how tools like VS Code/Discord install by
; default -- appropriate for a single-user gamer machine, not something IT-managed.
;
; Build locally:
;   dotnet publish ..\src\EveDeck\EveDeck.csproj -c Release --self-contained -r win-x64 -o ..\publish
;   iscc EveDeck.iss /DMyAppVersion=1.6.0
;
; CI passes /DMyAppVersion from the release tag (see .github/workflows/release.yml).

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
; Overridden in CI, which publishes to a repo-root publish-ci\ instead of app\publish\.
#ifndef MySourceDir
  #define MySourceDir "..\publish"
#endif

#define MyAppName "EveDeck"
#define MyAppPublisher "EveDeck"
#define MyAppURL "https://evedeck.space"
#define MyAppExeName "EveDeck.exe"

[Setup]
; Fixed GUID -- identifies this app across versions so upgrades replace in place instead of
; installing side-by-side. Do not regenerate this.
AppId={{9F3E1C2A-7B6D-4E1A-9C3F-2D6A8B4E5F10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=EveDeck-Setup-v{#MyAppVersion}
SetupIconFile=..\src\EveDeck\Assets\evedeck.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\..\LICENSE
; The in-app silent-update flow (Services/UpdateApplyService.cs) re-runs this installer with
; /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS -- spelled out explicitly here rather than relying on
; Inno's own defaults, since that flow depends on both being enabled.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; No skipifsilent: the in-app silent-update flow (Services/UpdateApplyService.cs) depends on this
; relaunching EveDeck after a /SILENT install -- confirmed via local testing that silent installs
; otherwise finish with the app closed and nothing bringing it back (/RESTARTAPPLICATIONS alone
; did not reliably relaunch it). Safe for interactive installs too: postinstall already means
; "launch after finishing", this only removes the silent-mode exception to that.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall

[UninstallDelete]
; EveDeck writes settings.json/logs to %LOCALAPPDATA%\EveDeck, not the install dir -- left in
; place on uninstall intentionally (matches user expectation that settings survive a reinstall).
