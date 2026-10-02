<#
  把桌面版打成可以直接发给别人的 zip。

  默认自包含：.NET 运行时 + Windows App SDK 运行时都塞进去，目标机器什么都不用装。
  代价是解压后 160 MB 出头（已经裁掉 Windows App SDK 里这套用不上的 AI / 语义搜索 / 小组件
  负载，见下面 -SkipTrim）。加 -FrameworkDependent 出小包（几 MB），
  但目标机器得先装 .NET 10 桌面运行时和 Windows App SDK 运行时。

  用法：
    .\pack.ps1                      # win-x64 自包含 + zip
    .\pack.ps1 -SkipZip             # 只出目录，不压
    .\pack.ps1 -FrameworkDependent  # 小包
    .\pack.ps1 -Runtime win-arm64   # 给 Windows on ARM
    .\pack.ps1 -Verify              # 打完启动一次，确认不是坏包
    .\pack.ps1 -SkipTrim            # 不裁剪（带上那套用不上的 AI 负载，多 60 MB 左右）
    .\pack.ps1 -Installer            # 再出一个 Inno Setup 安装器（要先装 Inno Setup 6）
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")] [string] $Configuration = "Release",
    [ValidateSet("win-x64", "win-arm64")] [string] $Runtime = "win-x64",
    [switch] $FrameworkDependent,
    [switch] $SkipZip,
    [switch] $SkipTrim,
    [switch] $Verify,
    [switch] $Installer
)

$ErrorActionPreference = "Stop"

$root  = $PSScriptRoot
$proj  = Join-Path $root "Jicun.Desktop.csproj"
$stage = Join-Path $root "dist\$Runtime"
$zip   = Join-Path $root "dist\Jicun-$Runtime.zip"

if (-not (Test-Path $proj)) { throw "找不到工程文件：$proj" }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# 这两个开关在 csproj 里默认都是 true，这里用命令行全局属性覆盖
$sc  = if ($FrameworkDependent) { "false" } else { "true" }
$sdk = if ($FrameworkDependent) { "false" } else { "true" }

Write-Host "publish $Configuration / $Runtime (self-contained=$sc, windowsAppSdkSelfContained=$sdk) ..." -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r $Runtime --self-contained $sc -p:WindowsAppSDKSelfContained=$sdk -o $stage -v m -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

$exe = Join-Path $stage "Jicun.exe"
if (-not (Test-Path $exe)) { throw "publish 结束但没看到 Jicun.exe，检查上面的输出" }

# 调试符号和 xml 文档不进包
Get-ChildItem $stage -Recurse -File -Include *.pdb, *.xml | Remove-Item -Force

# 裁掉 Windows App SDK 里这套用不上的负载：AI / ML / 语义搜索 / 小组件 / 工作负载会话。
# 它们是被自包含清单一起塞进来的，实测启动时一个都没加载（包内只有 Microsoft.Windows.SDK.NET.dll
# 这个投影程序集是启动时真的要用的）。裁完之后启动、解析、下载、--selftest 都照常。
# 语言资源也留 zh-CN / en-us 两份就够：界面文案是我们自己写的，框架那点内置字符串英文兜得住。
if (-not $SkipTrim) {
    $trim = @(
        'onnxruntime.dll', 'DirectML.dll', 'NPUDetect.dll', 'PerceptiveStreaming.dll', 'Microsoft.ML.OnnxRuntime.dll',
        'Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll',
        'Microsoft.Windows.AI*', 'Microsoft.Windows.Search.*', 'Microsoft.Windows.Widgets.*',
        'Microsoft.Windows.Workloads*', 'Microsoft.Windows.Vision*', 'Microsoft.Windows.SemanticSearch*',
        'Microsoft.Windows.Internal.*', 'Microsoft.Windows.ImageCreationInternal.winmd',
        'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Core.Projection.dll', 'WebView2Loader.dll',
        'workloads.*.json'
    )

    $killed = Get-ChildItem $stage -Recurse -File |
        Where-Object { $name = $_.Name; @($trim | Where-Object { $name -like $_ }).Count -gt 0 }
    $killedMB = [math]::Round((($killed | Measure-Object Length -Sum).Sum) / 1MB, 1)
    $killed | Remove-Item -Force

    $langs = Get-ChildItem $stage -Directory |
        Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $_.Name -notin @('zh-CN', 'en-us') }
    $langsMB = [math]::Round((($langs | Get-ChildItem -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
    $langs | Remove-Item -Recurse -Force

    Write-Host ("  裁掉 " + $killed.Count + " 个用不上的文件（" + $killedMB + " MB）+ " +
                $langs.Count + " 个多余语言目录（" + $langsMB + " MB）") -ForegroundColor DarkGray
}

# WinUI 的 .pri 不在发布清单里，漏了会做出一个双击就静默崩的包（退出码 0xC000027B）。
# csproj 里的 IncludePriInPublish 负责补上；这里再启动一次，坏包当场拦下，而不是等用户来报。
if ($Verify) {
    Write-Host "启动一次，确认不是坏包 ..." -ForegroundColor Cyan
    $proc = Start-Process -FilePath $exe -PassThru
    for ($i = 0; $i -lt 24; $i++) {
        Start-Sleep -Milliseconds 500
        if ($proc.HasExited) { break }
    }
    if ($proc.HasExited) {
        throw ("打出来的包起不来（退出码 " + $proc.ExitCode + "）。最可能的原因：Jicun.pri 没进包，见 csproj 的 IncludePriInPublish。")
    }
    Write-Host "  起来了，正常" -ForegroundColor Green
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

if (-not $SkipZip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("打包完成：" + $zip + " (" + [math]::Round((Get-Item $zip).Length / 1MB, 1) + " MB)") -ForegroundColor Green
}

# 可选：用 Inno Setup 出一个安装器（installer.iss）。它打的是上面那个目录，不是 zip。
if ($Installer) {
    $cands = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    if (${env:ProgramFiles(x86)}) { $cands += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe") }
    $iscc = $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "没找到 Inno Setup 的 ISCC.exe。装一个：winget install JRSoftware.InnoSetup" }

    # 版本号取 csproj 的 <Version> —— 跟客户端拿来比大小的是同一个
    $version = ([regex]'<Version>([^<]+)</Version>').Match((Get-Content $proj -Raw)).Groups[1].Value
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "csproj 里的 <Version> 得写成 1.2.3 这样：$version" }

    # 打哪个运行时目录、给哪种机器装，都传给 ISCC：写死 win-x64 的话，-Runtime win-arm64 -Installer
    # 会拿上一次 x64 的目录编出安装器，还会同名覆盖掉 x64 那份。
    $setupSuffix = if ($Runtime -eq "win-x64") { "" } else { "-$Runtime" }
    $arch = if ($Runtime -eq "win-arm64") { "arm64" } else { "x64compatible" }

    Write-Host "编译安装器（Inno Setup，版本 $version / $Runtime）..." -ForegroundColor Cyan
    & $iscc (Join-Path $root "installer.iss") "/DAppVersion=$version" "/DStageDir=dist\$Runtime" "/DArch=$arch" "/DSetupSuffix=$setupSuffix"
    if ($LASTEXITCODE -ne 0) { throw "ISCC 失败（exit $LASTEXITCODE）" }

    $setup = Join-Path $root "dist\Jicun-Setup-$version$setupSuffix.exe"
    if (-not (Test-Path $setup)) { throw "ISCC 说成功，但没看到 $setup" }
    Write-Host ("安装器：" + $setup + " (" + [math]::Round((Get-Item $setup).Length / 1MB, 1) + " MB)") -ForegroundColor Green
}

$size = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host ("输出目录：" + $stage + " (" + $size + " MB)") -ForegroundColor Green
Write-Host ("可执行文件：" + $exe)
