; TuneLab 的 Inno Setup 安装器定义（Windows x64 / arm64 共用，靠 /D 宏区分）。
;
; 产物：TuneLab-Setup-<Arch>-v<Version>.exe
;   · 按用户安装（PrivilegesRequired=lowest）到 {localappdata}\Programs\TuneLab；
;   · 关联 .tlpx（工程）与 .tlx（扩展包），HKCU 下写 ProgID，卸载时清掉；
;   · 开始菜单 + 可选桌面快捷方式；
;   · 应用内自动更新走 /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=<原安装目录>
;     （见 TuneLab/Utils/AppUpdateManager.cs 的 [EP-PACKAGE] 分支）；
;     带 /DIR= 视为更新：装完自动重启 TuneLab。
;
; 编译（CI）：
;   ISCC.exe /DArch=x64   /DVersion=2.1.2 /DSourceDir=... /DOutputDir=... /DIconFile=... TuneLab.iss

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Rid
  #if Arch == "arm64"
    #define Rid "win-arm64"
  #else
    #define Rid "win-x64"
  #endif
#endif
#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "payload"
#endif
#ifndef OutputDir
  #define OutputDir "out"
#endif
#ifndef IconFile
  #define IconFile "TuneLab.ico"
#endif
#ifndef AppId
  #if Arch == "arm64"
    #define AppId "{{5F0B7E52-2A7C-4C0E-9E1B-6C0E1D6A9A44}"
  #else
    #define AppId "{{5F0B7E52-2A7C-4C0E-9E1B-6C0E1D6A9A43}"
  #endif
#endif

[Setup]
AppId={#AppId}
AppName=TuneLab
AppVersion={#Version}
AppPublisher=TuneLab
AppPublisherURL=https://tunelab.app
AppSupportURL=https://github.com/LiuYunPlayer/TuneLab
DefaultDirName={localappdata}\Programs\TuneLab
DefaultGroupName=TuneLab
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=TuneLab-Setup-{#Rid}-v{#Version}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\TuneLab.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}

[Languages]
Name: "zh"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppTitle=TuneLab 安装程序
SetupWindowTitle=TuneLab 安装程序
WelcomeLabel1=欢迎安装 TuneLab
WelcomeLabel2=这会把 TuneLab 安装到你的电脑上。%n%n请关闭其他正在运行的程序，然后继续。
ClickNext=继续 (&N) >
ButtonInstall=安装 (&I)
ButtonFinish=完成 (&F)
FinishedHeadingLabel=TuneLab 安装完成
FinishedLabelNoIcons=TuneLab 已安装完成。
FinishedLabel=TuneLab 已安装完成，可以从开始菜单或桌面快捷方式启动。
SelectDirLabel3=安装程序将把 TuneLab 安装到下面的文件夹。若要选择其他文件夹，点击“浏览”。
SelectStartMenuFolderLabel3=安装程序将在下面的开始菜单文件夹中创建程序快捷方式。
ReadyLabel1=安装程序已准备好，点击“安装”开始。
ConfirmUninstall=确定要卸载 TuneLab 吗？

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TuneLab"; Filename: "{app}\TuneLab.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 TuneLab"; Filename: "{uninstallexe}"
Name: "{autodesktop}\TuneLab"; Filename: "{app}\TuneLab.exe"; WorkingDir: "{app}"; Tasks: desktopicon

; ── 文件关联（HKCU，按用户）──
; .tlpx = 工程文件；.tlx = 扩展包（双击交给 TuneLab，由宿主装扩展）。
[Registry]
Root: HKCU; Subkey: "Software\Classes\.tlpx"; ValueType: string; ValueName: ""; ValueData: "TuneLab.Project"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.tlpx"; ValueType: none; ValueName: "OpenWithProgids"; ValueData: "TuneLab.Project"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\TuneLab.Project"; ValueType: string; ValueName: ""; ValueData: "TuneLab 工程文件"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TuneLab.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\TuneLab.exe,0"
Root: HKCU; Subkey: "Software\Classes\TuneLab.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\TuneLab.exe"" ""%1"""

Root: HKCU; Subkey: "Software\Classes\.tlx"; ValueType: string; ValueName: ""; ValueData: "TuneLab.Extension"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.tlx"; ValueType: none; ValueName: "OpenWithProgids"; ValueData: "TuneLab.Extension"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\TuneLab.Extension"; ValueType: string; ValueName: ""; ValueData: "TuneLab 扩展包"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TuneLab.Extension\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\TuneLab.exe,0"
Root: HKCU; Subkey: "Software\Classes\TuneLab.Extension\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\TuneLab.exe"" ""%1"""

[Run]
; 正常安装：勾选“完成时启动”。
Filename: "{app}\TuneLab.exe"; Description: "启动 TuneLab"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
; 静默更新（应用内更新带 /DIR=）：装完自动重启，用户不必再点一次。
Filename: "{app}\TuneLab.exe"; WorkingDir: "{app}"; Flags: nowait; Check: IsUpdateMode

[Code]
// 应用内更新用 /DIR=<原安装目录> 表示“这是升级安装”：装完要把 TuneLab 拉起来。
function IsUpdateMode: Boolean;
begin
  Result := Pos('/DIR=', Uppercase(GetCmdTail)) > 0;
end;