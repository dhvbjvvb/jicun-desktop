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
;   * 卸载会连本地数据一起删掉（%LOCALAPPDATA%\Jicun：设置 / 历史 / 下载记录 / 缓存），
;     但不动用户自己下载的视频 / 图片 / 音频；卸载前若程序在跑，先弹窗让用户一键关掉它
;     （[Code] 的 InitializeUninstall），不然文件被占用会删不干净。
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
; 静默更新时要覆盖正在运行的自己：让安装器走 Restart Manager 去关掉占着文件的进程
; （客户端那边也传了 /CLOSEAPPLICATIONS）。
CloseApplications=yes
; 但别让它自己重启应用 —— 重开交给上面 [Run] 那条，两边都做会开出两个窗口。
RestartApplications=no
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
; 不带 skipifsilent：静默更新时也要把新版拉起来（skipifsilent 会在 /SILENT 下跳过它，
; 用户就得自己去开始菜单点一下）。正常手动安装时这就是「完成后启动」那个勾。
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall

[UninstallDelete]
; 卸载不留数据（用户明确要求）：设置 / 历史 / 下载记录 / 缓存整目录清掉。
; 只删我们自己那份数据（%LOCALAPPDATA%\Jicun）—— 用户下载到视频/图片/音乐目录里的文件一律不动。
Type: filesandordirs; Name: "{localappdata}\Jicun"

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
; 最后那段是给接收方看的：安装包没签名，别人从网上拿到会先吃一次 SmartScreen 警告，
; 这里就地解释一句，省得人家以为是病毒（不指望所有人先问我们）
WelcomeLabel2=这个向导会把 [name/ver] 装到你的电脑上。%n%n装好后桌面会有快捷方式（下一页可以取消）；设置、历史和下载记录都放在你自己的用户目录里，卸载时会一并删掉（你自己下载的视频、图片、音频不受影响）。%n%n如果刚才 Windows 弹了蓝色的「Windows 已保护你的电脑」，点「更多信息」→「仍要运行」就行：这个安装包没有代码签名证书，不是病毒。%n%n继续之前，建议先关掉正在运行的 即存。
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

; 卸载器自己也有一套文案，键名跟安装那套不同 —— 不覆盖的话卸载界面是英文的
; （实测：卸载确认页显示 "Are you sure you want to completely remove 即存 and all of its components?"）
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 %1
UninstallAppRunningError=%1 正在运行。%n%n请先把它全部关掉，再点「确定」继续；点「取消」退出。
ConfirmUninstall=确定要完全删除 %1 及其所有组件吗？
UninstallStatusLabel=正在从你的电脑上删除 %1，请稍等。
UninstalledAll=%1 已从你的电脑上完全删除。
UninstalledMost=%1 卸载完成。%n%n有少数文件没能删掉，可以手动清理。
UninstalledAndNeedsRestart=要完成 %1 的卸载，需要重启电脑。%n%n现在重启吗？

[Code]
const
  AppExeName = '{#AppExe}';

{ ---------- 卸载：先把正在运行的程序关掉，再把数据一起删掉 ---------- }

function IsAppRunning(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'),
    '/c tasklist /FI "IMAGENAME eq ' + AppExeName + '" /NH | find /I "' + AppExeName + '" > nul',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure WaitASecond();
var
  Code: Integer;
begin
  { Inno 的 Pascal Script 里没有 Sleep，用 ping 顶一下 }
  Exec(ExpandConstant('{cmd}'), '/c ping -n 2 127.0.0.1 > nul', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CloseJicun();
var
  Code: Integer;
  Tries: Integer;
begin
  { taskkill 不带 /F 就是发 WM_CLOSE：先请它自己退，能走完它自己的退出流程 }
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM ' + AppExeName + ' > nul 2>&1',
       '', SW_HIDE, ewWaitUntilTerminated, Code);
  Tries := 5;
  while (Tries > 0) and IsAppRunning() do
  begin
    WaitASecond();
    Tries := Tries - 1;
  end;
  { 还不退就强杀 —— 数据反正是要删的，留着进程只会让文件删不掉 }
  if IsAppRunning() then
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM ' + AppExeName + ' /T /F > nul 2>&1',
         '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function InitializeUninstall(): Boolean;
var
  Choice: Integer;
begin
  Result := True;

  if IsAppRunning() then
  begin
    { 静默卸载（/SILENT，客户端自己更新时用不着）没人点按钮，直接关掉它 }
    if UninstallSilent() then
      CloseJicun()
    else
    begin
      { 数组字面量不能另起一行：.iss 的解析器会把行首的 [ 当成节标签 }
      Choice := TaskDialogMsgBox('即存 正在运行',
        '卸载前得先把它关掉，不然程序文件被它占着，会删不干净。',
        mbConfirmation, MB_YESNO, ['现在关掉它并继续卸载', '先不卸载'], 1);
      if Choice = IDYES then
        CloseJicun()
      else
        Result := False;   { 用户想自己回去关，那这次先不卸 }
    end;
  end;

  { 数据一起删：先说清楚删什么、不碰什么，别让人事后才知道 }
  if Result and (not UninstallSilent()) then
    Result := TaskDialogMsgBox('卸载会删掉本地数据',
      '设置、观看历史、下载记录和缓存都会一起删掉，删了恢复不了。' + #13#10 +
      '你自己下载的视频 / 图片 / 音频不受影响。',
      mbConfirmation, MB_YESNO, ['继续卸载', '先不卸载'], 1) = IDYES;
end;

procedure InitializeWizard();
begin
  { 协议页默认就选「我接受」：省掉用户一次点击；想拒绝还是能点另一项 }
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;
