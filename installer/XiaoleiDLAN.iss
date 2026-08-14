; Xiaolei DLAN 安装脚本（Inno Setup 7）
; 源文件目录：publish\install（dotnet publish 多文件自包含输出 + mpv）

#define MyAppName "Xiaolei DLAN"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Xiaolei"
#define MyAppExeName "ScreenCastReceiver.exe"
#define SourceDir "..\publish\install"

[Setup]
AppId={{B7E4C9E1-3A5C-4F6D-9E8A-2D1F0A5B7C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\publish
OutputBaseFilename=XiaoleiDLAN-Setup-{#MyAppVersion}
SetupIconFile=favicon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
; 不自动写日志文件，程序内部为纯内存日志
DisableDirPage=no
CreateAppDir=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 全部文件递归复制，排除调试符号 (.pdb) 减小体积
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
