#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\releases"
#endif

#define AppName "Word Catcher"
#define AppExeName "WordCatcher.App.exe"

[Setup]
AppId={{D6524EA7-4C0C-4DC2-95D7-93EC59761C60}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=qup1010
AppPublisherURL=https://github.com/qup1010/WordCatcherDesktop
AppSupportURL=https://github.com/qup1010/WordCatcherDesktop/issues
DefaultDirName={localappdata}\Programs\WordCatcherDesktop
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
UsePreviousAppDir=yes
Uninstallable=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\WordCatcher.App\Assets\WordCatcher.ico
OutputDir={#OutputDir}
OutputBaseFilename=WordCatcherDesktop-win-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\WordCatcherDesktop"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletevalue
