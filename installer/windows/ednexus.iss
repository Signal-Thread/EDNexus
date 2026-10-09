; EDNexus Windows installer (Inno Setup 6 — free, open source).
;
; Installs a self-contained build to:
;     C:\Program Files\Signal & Thread\EDNexus\
; User data is NOT stored here: settings, logs and downloaded updates live in
; %LOCALAPPDATA%\EDNexus (see SettingsStore), which stays writable without admin rights. Uninstall
; removes them only if the user opts in (default: keep): settings, logs and the update cache.
;
; Build (version and the published-app dir are supplied on the command line):
;     ISCC.exe /DAppVersion=1.2.3 /DPublishDir=...\publish /Oout ednexus.iss

#define AppName "EDNexus"
#define AppPublisher "Signal & Thread LLC"
#ifndef AppVersion
  #define AppVersion "0.0.1"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif

[Setup]
; Stable AppId — keep constant across versions so upgrades replace in place.
AppId={{7F3B2E14-9C6A-4D5E-B8A1-2F4C6E8A0D31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/Signal-Thread/EDNexus
DefaultDirName={autopf}\Signal & Thread\EDNexus
DefaultGroupName=EDNexus
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\EDNexus.App.exe
SetupIconFile=..\..\assets\icons\ednexus.ico
OutputDir=out
OutputBaseFilename=EDNexus-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Program Files install requires elevation; 64-bit only.
PrivilegesRequired=admin
; Windows 10 1607 (build 14393) is the floor for the .NET 10 runtime the self-contained build ships.
MinVersion=10.0.14393
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\EDNexus"; Filename: "{app}\EDNexus.App.exe"
Name: "{group}\Uninstall EDNexus"; Filename: "{uninstallexe}"
Name: "{autodesktop}\EDNexus"; Filename: "{app}\EDNexus.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EDNexus.App.exe"; Description: "Launch EDNexus"; Flags: nowait postinstall skipifsilent

[Code]
// Offer to remove settings (which can hold an Inara API key), logs and the downloaded-update cache.
// The default button is No (keep), so a reinstall does not lose configuration; silent uninstalls
// always keep. This is a prompt rather than an unconditional [UninstallDelete] on purpose: the
// installer runs elevated, and when a different admin account approved the UAC prompt
// {localappdata} is that admin's profile, not the commander's, so nothing under it is deleted
// without the user seeing (and confirming) the exact path.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent()) then
  begin
    if MsgBox('Also delete your EDNexus settings, logs and downloaded updates?' + #13#10 + #13#10 +
              ExpandConstant('{localappdata}\EDNexus') + #13#10 + #13#10 +
              'Choose No to keep them (recommended if you plan to reinstall).',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\EDNexus'), True, True, True);
  end;
end;
