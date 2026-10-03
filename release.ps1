<#
  发一个版：改版本号 → 打包（安装器 + 绿色包）→ 发 GitHub Release（安装器 + zip + 说明一起传）。

  更新的载荷是**安装器 exe**（客户端下它、静默跑它）；zip 是顺手传上去给人手动下的。
  发版机器得装 Inno Setup —— 安装器是 pack.ps1 -Installer 现编出来的。

  客户端只认 GitHub 上最新那个 Release：版本号 = tag、说明 = 正文、sha256 = 附件的 digest，
  所以**必须 -Publish**（或自己到网页建 Release），也不用再提交什么清单文件。


  **更新说明只有这一个来源：这个版本的 GitHub Release 正文**。所以：
    - -NotesFile 支持 Markdown，弹窗会渲染（# 标题、**加粗**、==高亮==、- 列表、居中…见 README「更新」一节）
    - 必须 -Publish（或在网页上建 Release），否则客户端连「有新版本」都不知道
    - 发完之后改 Release 正文，用户下次检查就能看到，不用重发一版
  用法：
    .\release.ps1 -Version 1.0.1 -Notes "修了解析时闪退；设置页加了检查更新"
    .\release.ps1 -Version 1.0.1 -NotesFile notes.md
    .\release.ps1 -Version 1.0.1 -Notes "..." -Publish      # 顺手 gh release create（要先 gh auth login）
    .\release.ps1 -Version 1.0.1 -Notes "..." -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Notes = "",
    [string] $NotesFile = "",
    [ValidateSet("win-x64", "win-arm64")] [string] $Runtime = "win-x64",
    [switch] $Publish,
    [switch] $SkipPack
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$proj = Join-Path $root "Jicun.Desktop.csproj"
$zip  = Join-Path $root "dist\Jicun-$Runtime.zip"
$repo = "dhvbjvvb/jicun-desktop"
$utf8 = New-Object System.Text.UTF8Encoding $false   # 不带 BOM，客户端 JSON 解析器不认 BOM

if (-not (Test-Path $proj)) { throw "找不到工程文件：$proj" }

# 版本号得是 1.2.3 这种，客户端按它比大小。带 v 前缀也认
$clean = $Version.Trim().TrimStart("v", "V")
if ($clean -notmatch '^\d+\.\d+\.\d+$') { throw "版本号得写成 1.2.3 这样：$Version" }

# 安装器就是更新的载荷；zip 是顺手传的绿色包。文件名规则要和 pack.ps1 的 SetupSuffix 保持一致
$setupSuffix = if ($Runtime -eq "win-x64") { "" } else { "-$Runtime" }
$setup = Join-Path $root "dist\Jicun-Setup-$clean$setupSuffix.exe"

if ($NotesFile) {
    if (-not (Test-Path $NotesFile)) { throw "找不到更新说明文件：$NotesFile" }
    $Notes = Get-Content $NotesFile -Raw
}
if ([string]::IsNullOrWhiteSpace($Notes)) { throw "总得写点更新说明（-Notes 或 -NotesFile）" }

# 1. 改 csproj 里的 <Version> —— 客户端读的就是它
$text = Get-Content $proj -Raw
if ($text -notmatch '<Version>[^<]*</Version>') { throw "csproj 里没有 <Version> 那一行，先手工加一个" }
$text = $text -replace '<Version>[^<]*</Version>', "<Version>$clean</Version>"
[System.IO.File]::WriteAllText($proj, $text, $utf8)
Write-Host "版本号已改成 $clean" -ForegroundColor Cyan

# 2. 打包：安装器（更新的载荷）+ 绿色包 zip。打完启动一次确认不是坏包。
#    安装器是 Inno Setup 现场编的，机器上没装的话 pack.ps1 会在这里直接报出来
if (-not $SkipPack) {
    & (Join-Path $root "pack.ps1") -Configuration Release -Runtime $Runtime -Verify -Installer
    if ($LASTEXITCODE -ne 0) { throw "pack.ps1 失败（exit $LASTEXITCODE）" }
}
if (-not (Test-Path $setup)) { throw "没看到安装器 $setup，先跑一次打包（要装 Inno Setup）" }
if (-not (Test-Path $zip)) { throw "没看到绿色包 $zip，先跑一次打包" }

# 3. 发 GitHub Release：**版本、说明、安装包全在它身上**（客户端只认它），两个附件一起传
if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "没装 gh（GitHub CLI），用不了 -Publish；也可以自己到网页把 $setup 和 $zip 传上去"
    }
    gh release create "v$clean" $setup $zip --repo $repo --title "即存 for Windows $clean" --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create 失败（exit $LASTEXITCODE）" }
    Write-Host "Release 已发布：https://github.com/$repo/releases/tag/v$clean" -ForegroundColor Green
}
else {
    Write-Warning "没有 -Publish：GitHub 上就没有 v$clean 这个 Release，客户端什么都看不到（检测、说明、安装包全靠它）。"
}

Write-Host ""
Write-Host "别忘了把 csproj 的版本号提交推送（仓库里的版本号得跟着走）。" -ForegroundColor Yellow
