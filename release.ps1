<#
  发一个版：改版本号 → 打包 → 算 sha256 → 写 update/<运行时>.json →（可选）发 GitHub Release。

  客户端就是靠仓库里的 update/<运行时>.json 发现新版的，所以发完之后记得提交推送。


  **更新说明只有一个来源：这个版本的 GitHub Release 正文**（清单里不放说明）。所以：
    - -NotesFile 支持 Markdown，弹窗会渲染（# 标题、**加粗**、==高亮==、- 列表、居中…见 README「更新」一节）
    - 光写清单不算完：必须 -Publish（或在网页上建 Release），否则客户端拿不到说明，只能显示「没有写更新说明」
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

# 2. 打包（Release / 自包含 / 出 zip，打完启动一次确认不是坏包）
if (-not $SkipPack) {
    & (Join-Path $root "pack.ps1") -Configuration Release -Runtime $Runtime -Verify
    if ($LASTEXITCODE -ne 0) { throw "pack.ps1 失败（exit $LASTEXITCODE）" }
}
if (-not (Test-Path $zip)) { throw "没看到 $zip，先跑一次打包" }

# 3. 算 sha256 —— 镜像站是第三方，这是唯一的安全边界，客户端校验不过就不装
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
Write-Host "sha256 $hash" -ForegroundColor Cyan

# 4. 写清单。下载地址写 GitHub Release 的附件；客户端会自动套镜像，并且必须对得上 sha256
$manifest = [ordered]@{
    version     = $clean
    url         = "https://github.com/$repo/releases/download/v$clean/Jicun-$Runtime.zip"
    sha256      = $hash
    size        = $size
    publishedAt = (Get-Date -Format "yyyy-MM-dd")
}
$manifestDir = Join-Path $root "update"
New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null
$manifestPath = Join-Path $manifestDir "$Runtime.json"
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), $utf8)
Write-Host "清单已写：$manifestPath" -ForegroundColor Green

# 5. 发 GitHub Release：**更新说明只有这一个来源**（清单里不放说明），下载附件也走它
if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "没装 gh（GitHub CLI），用不了 -Publish；也可以自己到网页把 $zip 传上去"
    }
    gh release create "v$clean" $zip --repo $repo --title "即存 for Windows $clean" --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create 失败（exit $LASTEXITCODE）" }
    Write-Host "Release 已发布：https://github.com/$repo/releases/tag/v$clean" -ForegroundColor Green
}
else {
    Write-Warning "没有 -Publish：GitHub 上就没有 v$clean 这个 Release，客户端拿不到更新说明（弹窗会显示「没有写更新说明」）。下载附件也得靠它。"
}

Write-Host ""
Write-Host "别忘了把 update\$Runtime.json 和改过的 csproj 提交推送 —— 客户端就是靠它发现新版的。" -ForegroundColor Yellow
