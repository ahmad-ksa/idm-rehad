; Rehad Download Manager installer (Inno Setup 6)
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6F1C2E7A-3B9D-4C58-9E21-5A7D0B8C4F13}
AppName=Rehad Download Manager
AppVersion={#AppVersion}
AppPublisher=Rehad
DefaultDirName={localappdata}\Programs\Rehad
DefaultGroupName=Rehad Download Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\out
OutputBaseFilename=Rehad-Setup-{#AppVersion}
SetupIconFile=..\src\Rehad\Assets\app.ico
UninstallDisplayIcon={app}\Rehad.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\out\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Rehad Download Manager"; Filename: "{app}\Rehad.exe"
Name: "{group}\Uninstall Rehad"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Rehad Download Manager"; Filename: "{app}\Rehad.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Rehad.exe"; Description: "{cm:LaunchProgram,Rehad Download Manager}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /f /im Rehad.exe"; Flags: runhidden; RunOnceId: "KillRehad"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.rehad.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Chromium\NativeMessagingHosts\com.rehad.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.rehad.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.rehad.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Vivaldi\NativeMessagingHosts\com.rehad.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Mozilla\NativeMessagingHosts\com.rehad.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Rehad"; Flags: uninsdeletevalue dontcreatekey
