; ============================================================
;  MonitorAlign 安装脚本 (Inno Setup 6)
;  编译： ISCC.exe setup.iss
;  产物： dist-setup\MonitorAlign-Setup-<版本>.exe
;
;  v2.0.0 起打包的是 **WinUI 3 版**（源码在 ..\MonitorAlignWinUI，产物 ..\dist-winui）。
;  v1.6.0 及以前是 WPF 版（..\MonitorAlign，产物 ..\dist）。
;
;  ⚠ AppId 与安装目录**刻意保持不变**：WinUI 3 版是接 v1.6.0 的棒，
;    用户那边应当是「升级」而不是「多装了一个程序」。换了 AppId 就等于换了个产品，
;    旧版会永远留在控制面板里下不掉。
;
;  ⚠ 版本号唯一来源是 ..\MonitorAlignWinUI\MonitorAlignWinUI.csproj 的 <Version>，
;    改版本时两处必须同步（设置页「关于」显示的就是 csproj 那个）。
; ============================================================

#define MyAppName        "MonitorAlign 显示器对齐工具"
#define MyAppShort       "MonitorAlign"
#define MyAppVersion     "2.0.2"
#define MyAppPublisher   "UTwelve"
#define MyAppExeName     "MonitorAlign.exe"

[Setup]
; AppId 一经发布不要修改，否则会被识别成另一个程序
AppId={{7C4E1A93-2B5F-4D18-9E63-A8F27D5016BC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}

; 绿色小工具：装到用户目录，全程不需要管理员权限
DefaultDirName={localappdata}\Programs\{#MyAppShort}
DefaultGroupName={#MyAppShort}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\dist-setup
OutputBaseFilename={#MyAppShort}-Setup-{#MyAppVersion}
SetupIconFile=..\MonitorAlignWinUI\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
; ---------- 关键向导文案中文化（Inno 官方未附简体中文语言包） ----------
SetupAppTitle=安装 - {#MyAppName}
SetupWindowTitle=安装 - {#MyAppName}
UninstallAppTitle=卸载 - {#MyAppName}
UninstallAppFullTitle=卸载 {#MyAppName}
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=即将在你的电脑上安装 [name/ver]。%n%n这是一个绿色小工具：只读取和修改 Windows 的显示器排列设置，不写驱动、不联网。%n%n边缘泛光开启时程序会驻留托盘以保持效果（关掉泛光后关窗即退出），开机自启默认关闭、可在设置页随时开关。%n%n点击“下一步”继续。
WizardSelectDir=选择安装位置
SelectDirDesc=要把 [name] 安装到哪里？
SelectDirLabel3=安装程序会把 [name] 安装到下面的文件夹。
SelectDirBrowseLabel=点击“下一步”继续；想换个位置请点击“浏览”。
DiskSpaceMBLabel=至少需要 [mb] MB 的可用磁盘空间。
WizardSelectTasks=选择附加任务
SelectTasksDesc=还需要执行哪些附加任务？
WizardSelectProgramGroup=选择开始菜单文件夹
SelectStartMenuFolderDesc=要在开始菜单的哪个位置放快捷方式？
SelectStartMenuFolderLabel3=安装程序会在下面的开始菜单文件夹中创建快捷方式。
WizardReady=准备安装
ReadyLabel1=安装程序已准备好，即将开始安装 [name]。
ReadyLabel2a=点击“安装”开始安装；想检查或修改设置请点击“上一步”。
ReadyLabel2b=点击“安装”开始安装。
WizardInstalling=正在安装
InstallingLabel=请稍候，安装程序正在安装 [name]。
FinishedHeadingLabel=完成 [name] 安装向导
FinishedLabelNoIcons=[name] 已安装完成。
FinishedLabel=[name] 已安装完成，可以点击下面的图标直接运行。
ClickFinish=点击“完成”结束安装向导。
ButtonNext=下一步(&N) >
ButtonBack=< 上一步(&B)
ButtonInstall=安装(&I)
ButtonFinish=完成(&F)
ButtonCancel=取消
ButtonBrowse=浏览(&R)...
ConfirmUninstall=确定要完全卸载 [name] 及其所有组件吗？
UninstalledAll=[name] 已成功卸载。
UninstallStatusLabel=正在从你的电脑上卸载 [name]，请稍候。
SetupAlreadyRunning=安装程序已在运行。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
; WinUI 3 版是**目录版**（228MB / 528 个文件）—— Windows App SDK 自包含，
; 目标机不需要装任何运行时。exe 只有 300KB，真正干活的是同目录那堆 dll，
; 所以必须整目录带子目录一起装（别只挑 exe）。
Source: "..\dist-winui\*"; DestDir: "{app}"; Excludes: "MonitorAlign.settings.json,MonitorAlign.settings.json.tmp,MonitorAlign-info.txt,MonitorAlign-geomcheck.txt,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README-使用说明.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\使用说明"; Filename: "{app}\README-使用说明.md"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 清理绿色配置与自检产物，避免卸载后残留
Type: files; Name: "{app}\MonitorAlign-info.txt"
Type: files; Name: "{app}\MonitorAlign-geomcheck.txt"
Type: files; Name: "{app}\MonitorAlign-diag.txt"
Type: files; Name: "{app}\MonitorAlign.settings.json"
Type: dirifempty; Name: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "MonitorAlign"; Flags: uninsdeletevalue dontcreatekey
