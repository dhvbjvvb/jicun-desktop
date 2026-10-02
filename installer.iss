; 即存 的安装器（Inno Setup 6，用 ISCC 编译）。
;
; 几个刻意的选择：
;   * PrivilegesRequired=lowest —— 按用户装到 %LOCALAPPDATA%\Programs，不弹 UAC。
;     装在自己目录里还有个好处：程序对自己目录有写权限，应用内的自动更新才用得上
;     （装进 Program Files 会变成「自动更新用不了」）。
;   * 欢迎页保留并改成中文说明（下一页就是协议）。
;   * 协议显示仓库里的 LICENSE（MIT），并把「我接受」**默认选中** —— 用户不用多点一下，
;     想拒绝仍然可以点另一项（见文件末尾 [Code]）。
;   * 桌面快捷方式默认勾上（!see [Tasks] 的 checkedonce），用户可以取消。
;   * 卸载不动用户数据：设置 / 历史 / 下载记录都在 %LOCALAPPDATA%\Jicun，不归安装器管。
;
; 用法（一般不用手敲，pack.ps1 -Installer 会调）：
;   ISCC.exe installer.iss /DAppVersion=1.0.1 /DStageDir=dist\win-x64

#define AppName "即存"
#define AppExe "Jicun.exe"
#define AppPublisher "DIOT"
#define AppUrl "https://github.com/dhvbjvvb/jicun-desktop"
; 打哪个运行时目录、给哪种机器装，由 pack.ps1 用 /DStageDir /DArch /DSetupSuffix 传进来。
; 写死的话 .\pack.ps1 -Runtime win-arm64 -Installer 会拿上一次 x64 的目录编出安装器，
; 而且会把 x64 的安装器同名覆盖掉。手敲 ISCC 时这三个默认值就是 win-x64 那套。
#ifndef StageDir
  #define StageDir "dist\win-x64"
#endif
#ifndef Arch
  #define Arch "x64compatible"
#endif
#ifndef SetupSuffix
  #define SetupSuffix ""
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; AppId 是升级用的身份，换掉它就会变成「装了两份」
AppId={{8F3A1C1E-6B0A-4B7E-9E1F-2A5C7D9E4B31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
DefaultDirName={autopf}\Jicun
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=yes
LicenseFile=LICENSE
PrivilegesRequired=lowest
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
OutputDir=dist
OutputBaseFilename=Jicun-Setup-{#AppVersion}{#SetupSuffix}
SetupIconFile=Assets\jicun.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Tasks]
; checkedonce = 默认勾上，但用户取消过一次之后就不再自动勾（下次升级不会偷偷加回来）
Name: "desktopicon"; Description: "在桌面创建快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Messages]
; 官方发行包里没有简体中文语言文件（ChineseSimplified.isl 不随 Inno 发布），
; 所以在这里把用户会走到的那几页直接覆盖成中文。键名必须和 Default.isl 里的一致，
; 写错 ISCC 会在编译期报出来（所以不怕漏字，只怕漏键）。
SetupAppTitle=安装
SetupWindowTitle=安装 即存
ExitSetupTitle=退出安装
ExitSetupMessage=安装还没完成，现在退出就不会装上了。%n%n可以以后再运行安装程序。%n%n要退出吗？
ButtonBack=< 上一步
ButtonNext=下一步 >
ButtonInstall=安装
ButtonCancel=取消
ButtonFinish=完成
ButtonBrowse=浏览…
ButtonOK=确定
ButtonYes=是
ButtonNo=否
; SelectDir 页那个「浏览」用的是另一把键（ButtonBrowse 只管浏览对话框里的按钮）
ButtonWizardBrowse=浏览…
ClickNext=点「下一步」继续，点「取消」退出安装。
WelcomeLabel1=欢迎安装 即存
; 注意别自己再写一遍应用名：[name/ver] 会展开成「即存 1.0.0」
WelcomeLabel2=这个向导会把 [name/ver] 装到你的电脑上。%n%n装好后桌面会有快捷方式（下一页可以取消）；设置、历史和下载记录都放在你自己的用户目录里，卸载时不会被删。%n%n继续之前，建议先关掉正在运行的 即存。
WizardLicense=许可协议
LicenseLabel=请先读一下下面这些重要信息。
LicenseLabel3=继续安装就表示你接受这份协议；不接受就点「取消」。
LicenseAccepted=我接受这份协议
LicenseNotAccepted=我不接受
WizardSelectDir=选择安装位置
SelectDirDesc=装到哪儿？
SelectDirLabel3=安装程序会把 即存 装进下面这个文件夹。
SelectDirBrowseLabel=点「下一步」继续；想换地方就点「浏览」。
DiskSpaceMBLabel=至少需要 [mb] MB 空闲磁盘空间。
DiskSpaceGBLabel=至少需要 [gb] GB 空闲磁盘空间。
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=安装至少需要 %1 KB，选中的盘只剩 %2 KB。%n%n还是要继续吗？
WizardSelectTasks=选择附加任务
SelectTasksDesc=还要做哪些事？
SelectTasksLabel2=勾选你想让安装程序顺手做的事，然后点「下一步」。
WizardReady=准备安装
ReadyLabel1=准备好了，可以开始安装 即存。
ReadyLabel2a=点「安装」开始；想改什么就点「上一步」。
ReadyLabel2b=点「安装」开始安装。
WizardPreparing=正在准备
PreparingDesc=正在准备安装 即存。
WizardInstalling=正在安装
InstallingLabel=正在安装，请稍等。
FinishedLabel=即存 装好了。可以从桌面快捷方式或开始菜单启动。
FinishedLabelNoIcons=即存 装好了。

[Code]
procedure InitializeWizard();
begin
  { 协议页默认就选「我接受」：省掉用户一次点击；想拒绝还是能点另一项 }
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;
