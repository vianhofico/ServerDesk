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
; Use Default.isl as a complete fallback and override the core setup flow below.
; This keeps Vietnamese support independent of whether a particular Inno package
; happens to bundle the community Vietnamese.isl translation.
Name: "vietnamese"; MessagesFile: "compiler:Default.isl"

[LangOptions]
vietnamese.LanguageName=Tiếng Việt
vietnamese.LanguageID=$042A
vietnamese.LanguageCodePage=0

[Messages]
vietnamese.SetupAppTitle=Cài đặt
vietnamese.SetupWindowTitle=Cài đặt - %1
vietnamese.UninstallAppTitle=Gỡ cài đặt
vietnamese.UninstallAppFullTitle=Gỡ cài đặt - %1
vietnamese.InformationTitle=Thông tin
vietnamese.ConfirmTitle=Xác nhận
vietnamese.ErrorTitle=Lỗi
vietnamese.SetupLdrStartupMessage=Chương trình này sẽ cài đặt %1. Bạn có muốn tiếp tục không?
vietnamese.ExitSetupTitle=Thoát cài đặt
vietnamese.ExitSetupMessage=Cài đặt chưa hoàn thành. Nếu thoát bây giờ, ServerDesk sẽ chưa được cài đặt.%n%nBạn có thể chạy lại trình cài đặt sau.%n%nThoát ngay?
vietnamese.ButtonBack=< &Trước
vietnamese.ButtonNext=T&iếp >
vietnamese.ButtonInstall=&Cài đặt
vietnamese.ButtonCancel=Hủy
vietnamese.ButtonFinish=&Hoàn thành
vietnamese.ButtonBrowse=&Duyệt...
vietnamese.SelectLanguageTitle=Chọn ngôn ngữ cài đặt
vietnamese.SelectLanguageLabel=Chọn ngôn ngữ sử dụng trong quá trình cài đặt:
vietnamese.ClickNext=Nhấn Tiếp để tiếp tục, hoặc Hủy để thoát trình cài đặt.
vietnamese.WizardSelectDir=Chọn vị trí cài đặt
vietnamese.SelectDirDesc=[name] sẽ được cài ở đâu?
vietnamese.SelectDirLabel3=[name] sẽ được cài vào thư mục sau:
vietnamese.WizardSelectTasks=Chọn tác vụ bổ sung
vietnamese.SelectTasksDesc=Chọn các tác vụ bổ sung cần thực hiện.
vietnamese.SelectTasksLabel2=Chọn các tác vụ bổ sung mà trình cài đặt sẽ thực hiện khi cài [name], rồi nhấn Tiếp.
vietnamese.WizardReady=Sẵn sàng cài đặt
vietnamese.ReadyLabel1=Trình cài đặt đã sẵn sàng để cài [name] trên máy tính của bạn.
vietnamese.ReadyLabel2a=Nhấn Cài đặt để tiếp tục, hoặc nhấn Trước để xem lại các tùy chọn.
vietnamese.ReadyLabel2b=Nhấn Cài đặt để tiếp tục.
vietnamese.ReadyMemoDir=Vị trí cài đặt:
vietnamese.ReadyMemoTasks=Tác vụ bổ sung:

[CustomMessages]
vietnamese.CreateDesktopIcon=Tạo lối tắt ngoài &Desktop
vietnamese.AdditionalIcons=Lối tắt bổ sung:
vietnamese.LaunchProgram=Chạy %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ServerDesk"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{autodesktop}\ServerDesk"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
