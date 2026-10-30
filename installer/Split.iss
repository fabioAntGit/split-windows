; Split's installer. Built by make-installer.ps1, which passes (ISCC /D...):
;   AppVersion  the version from Directory.Build.props
;   Arch        x64 or arm64
;   SourceDir   the published files for that architecture
;   OutputDir   where Split-<version>-<arch>-Setup.exe goes
; The setup tests may also pass MutexName, to stay clear of a Split that is running.
#ifndef MutexName
  #define MutexName "Split"
#endif
#if Arch == "arm64"
  #define Architectures "arm64"
#else
  #define Architectures "x64os"
#endif

[Setup]
; Never change: it is how a later setup finds this one to update it.
AppId={{6B1D3E0C-5A2F-4C7E-9D84-2F0E5B7A91C3}
AppName=Split
AppVersion={#AppVersion}
AppPublisher=Split
DefaultDirName={localappdata}\Programs\Split
DefaultGroupName=Split
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Split.exe
UninstallDisplayName=Split
SetupIconFile=..\src\Split.App\Split.ico
OutputDir={#OutputDir}
OutputBaseFilename=Split-{#AppVersion}-{#Arch}-Setup
; Per user: nothing is written outside the user's profile, so no administrator is asked for.
PrivilegesRequired=lowest
ArchitecturesAllowed={#Architectures}
ArchitecturesInstallIn64BitMode={#Architectures}
; Windows 10 22H2.
MinVersion=10.0.19045
; The running Split holds the mutex it takes at start (src/Split.Windows/SingleInstance.cs); setup and uninstall wait for it to quit.
AppMutex={#MutexName}
CloseApplications=yes
ChangesEnvironment=yes
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.OpenWithSplit=Open with Split
portuguese.OpenWithSplit=Abrir com o Split
french.OpenWithSplit=Ouvrir avec Split
spanish.OpenWithSplit=Abrir con Split
english.AddToPath=Add splitapp to PATH (the command line tool)
portuguese.AddToPath=Adicionar o splitapp ao PATH (a ferramenta de linha de comandos)
french.AddToPath=Ajouter splitapp au PATH (l’outil en ligne de commande)
spanish.AddToPath=Añadir splitapp al PATH (la herramienta de línea de comandos)

[Tasks]
Name: "addtopath"; Description: "{cm:AddToPath}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Split"; Filename: "{app}\Split.exe"
Name: "{autodesktop}\Split"; Filename: "{app}\Split.exe"; Tasks: desktopicon
; Send to › Split hands every selected file to one start.
Name: "{usersendto}\Split"; Filename: "{app}\Split.exe"

[Registry]
; "Open with Split" for files and folders. The shell starts the command for the selection (Player: up to 15 items, one start each);
; Split gathers those starts into one wheel. On Windows 11 the entry is under "Show more options".
Root: HKCU; Subkey: "Software\Classes\*\shell\Split"; ValueType: string; ValueData: "{cm:OpenWithSplit}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Split"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Split.exe"
Root: HKCU; Subkey: "Software\Classes\*\shell\Split"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKCU; Subkey: "Software\Classes\*\shell\Split\command"; ValueType: string; ValueData: """{app}\Split.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Split"; ValueType: string; ValueData: "{cm:OpenWithSplit}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Split"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Split.exe"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Split"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Split\command"; ValueType: string; ValueData: """{app}\Split.exe"" ""%1"""
; Launch at login is Split's own setting (Settings › General); this only cleans its value up. Nothing is created here.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Split"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\Split.exe"; Description: "{cm:LaunchProgram,Split}"; Flags: nowait postinstall skipifsilent

; The settings and the activity log in %APPDATA%\Split belong to the user and stay after an uninstall.
[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  EnvironmentKey = 'Environment';

// The user's own PATH: no administrator. The entry is the install folder, where splitapp.exe is.
procedure ChangePath(Add: Boolean);
var
  Paths, Folder: string;
  At: Integer;
begin
  Folder := ExpandConstant('{app}');
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Paths) then Paths := '';
  At := Pos(';' + Uppercase(Folder) + ';', ';' + Uppercase(Paths) + ';');
  if Add then
  begin
    if At > 0 then Exit;
    if (Paths <> '') and (Paths[Length(Paths)] <> ';') then Paths := Paths + ';';
    Paths := Paths + Folder;
  end
  else
  begin
    if At = 0 then Exit;
    Delete(Paths, At, Length(Folder) + 1);
    if (Paths <> '') and (Paths[Length(Paths)] = ';') then Delete(Paths, Length(Paths), 1);
  end;
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then ChangePath(True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then ChangePath(False);
end;
