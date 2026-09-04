#ifndef AppVersion
  #error AppVersion must be supplied by scripts/build-windows-installer.ps1
#endif
#ifndef ReleaseTag
  #error ReleaseTag must be supplied by scripts/build-windows-installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by scripts/build-windows-installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by scripts/build-windows-installer.ps1
#endif
#ifndef BrandingIcon
  #error BrandingIcon must be supplied by scripts/build-windows-installer.ps1
#endif

#define AppName "ServerDesk"
#define AppExeName "ServerDesk.App.exe"
#define AppPublisher "ServerDesk"
#define AppUrl "https://github.com/vianhofico/ServerDesk"

[Setup]
AppId={{8680D74F-3283-4694-A5F3-1D89AD638F5E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#ReleaseTag}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\ServerDesk
DefaultGroupName=ServerDesk
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ServerDesk-{#ReleaseTag}-win-x64-setup
SetupIconFile={#BrandingIcon}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Windows installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "vietnamese"; MessagesFile: "compiler:Languages\Vietnamese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ServerDesk"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{autodesktop}\ServerDesk"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
