#ifndef SourceDir
  #error SourceDir must point to the published PakScape application.
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif

[Setup]
AppId={{B94AEE2C-EE7A-4097-B126-0909F13A2F71}
AppName=PakScape
AppVersion={#AppVersion}
AppPublisher=PakScape
AppPublisherURL=https://github.com/timbergeron/PakScape
DefaultDirName={localappdata}\Programs\PakScape
DefaultGroupName=PakScape
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=PakScape-{#AppVersion}-win-x64-setup
SetupIconFile=..\PakStudio.App\Assets\PakScape.ico
UninstallDisplayIcon={app}\PakScape.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PakScape"; Filename: "{app}\PakScape.exe"
Name: "{autodesktop}\PakScape"; Filename: "{app}\PakScape.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\.pak\OpenWithProgids"; ValueType: string; ValueName: "PakScape.pak"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\PakScape.pak"; ValueType: string; ValueName: ""; ValueData: "Quake PAK archive"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\PakScape.pak\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\PakScape.File.ico"""
Root: HKCU; Subkey: "Software\Classes\PakScape.pak\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PakScape.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.pk3\OpenWithProgids"; ValueType: string; ValueName: "PakScape.pk3"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\PakScape.pk3"; ValueType: string; ValueName: ""; ValueData: "Quake PK3 archive"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\PakScape.pk3\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\PakScape.File.ico"""
Root: HKCU; Subkey: "Software\Classes\PakScape.pk3\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PakScape.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.kpf\OpenWithProgids"; ValueType: string; ValueName: "PakScape.kpf"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\PakScape.kpf"; ValueType: string; ValueName: ""; ValueData: "Quake KPF archive"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\PakScape.kpf\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\PakScape.File.ico"""
Root: HKCU; Subkey: "Software\Classes\PakScape.kpf\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PakScape.exe"" ""%1"""
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "PakScape"; ValueData: "Software\PakScape\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\PakScape\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "PakScape"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\PakScape\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pak"; ValueData: "PakScape.pak"
Root: HKCU; Subkey: "Software\PakScape\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pk3"; ValueData: "PakScape.pk3"
Root: HKCU; Subkey: "Software\PakScape\Capabilities\FileAssociations"; ValueType: string; ValueName: ".kpf"; ValueData: "PakScape.kpf"

[Run]
Filename: "{app}\PakScape.exe"; Description: "Launch PakScape"; Flags: nowait postinstall skipifsilent
