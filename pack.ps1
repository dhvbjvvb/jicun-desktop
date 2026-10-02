<#
  把桌面版打成可以直接发给别人的 zip。

  默认自包含：.NET 运行时 + Windows App SDK 运行时都塞进去，目标机器什么都不用装。
  代价是解压后 200 MB 出头。加 -FrameworkDependent 出小包（几 MB），
  但目标机器得先装 .NET 10 桌面运行时和 Windows App SDK 运行时。

  用法：
    .\pack.ps1                      # win-x64 自包含 + zip
    .\pack.ps1 -SkipZip             # 只出目录，不压
    .\pack.ps1 -FrameworkDependent  # 小包
    .\pack.ps1 -Runtime win-arm64   # 给 Windows on ARM
    .\pack.ps1 -Verify              # 打完启动一次，确认不是坏包
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")] [string] $Configuration = "Release",
    [ValidateSet("win-x64", "win-arm64")] [string] $Runtime = "win-x64",
    [switch] $FrameworkDependent,
    [switch] $SkipZip,
    [switch] $Verify
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

$size = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host ("输出目录：" + $stage + " (" + $size + " MB)") -ForegroundColor Green
Write-Host ("可执行文件：" + $exe)
