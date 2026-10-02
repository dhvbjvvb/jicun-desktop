# 即存 for Windows

Android 版「即存」的桌面端：粘贴分享链接 → 调解析接口 → 图片 / 视频 / 音频 / 文案 →
预览 → 下载到自选文件夹。

C# + WinUI 3（Windows App SDK 2.5.1），单工程、无第三方依赖。

## 跑起来

```powershell
cd jicun-desktop
dotnet run
```

工程是 unpackaged + self-contained（WindowsPackageType=None、WindowsAppSDKSelfContained=True），
不需要装 Windows App SDK 运行时，直接出 exe 就能跑：

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\Jicun.exe
```

## 打包

```powershell
.\pack.ps1 -Verify      # win-x64 自包含 + zip，打完启动一次确认不是坏包
```

默认自包含（.NET 运行时 + Windows App SDK 运行时都塞进去），解压就能跑，
代价是 228 MB 目录 / 91 MB zip。加 `-FrameworkDependent` 出 77 MB 的目录，
但目标机器得先装 .NET 10 桌面运行时和 Windows App SDK 运行时。

> ⚠️ `dotnet publish` 不会自动带上 `Jicun.pri`，少了它 exe 双击秒崩
> （退出码 0xC000027B）而且没有任何日志。csproj 里的 `IncludePriInPublish` target
> 负责把它补进发布清单 —— 别删。`pack.ps1 -Verify` 就是拦这个的。

## 更新

启动时后台查一次，设置页也有「检查更新」手动查。检测不看 GitHub API（那接口国内时通时不通，
还有速率限制），读的是仓库里的静态清单 `update/win-x64.json`：版本、更新说明、下载地址、sha256 都写在里面。
拉的时候挨个试 GitHub 镜像 —— ghfast.top → gh-proxy.com → 直连 → jsDelivr，第一个通的就用，这就是「检测走 CDN / 镜像」。

有新版就弹公告窗口：上面是更新说明，底下左边「忽略」右边「更新」。

- 「忽略」只忽略**这一个版本**：用户现在 1.0.0、忽略掉 1.0.1，下次检出 1.0.1 就不再打扰；
  等出了 1.0.2 照样弹。手动点「检查更新」时一律照弹，让他还能改主意。
  （忽略记在 `%LOCALAPPDATA%\Jicun\update-state.json`。）
- 「更新」→ 下载（进度条 + 百分比）→ 对 sha256 → 解压到 `%LOCALAPPDATA%\Jicun\update\staging-<版本>`
  → 用**新版自己的 exe** 起一个 `--apply-update` 进程 → 主程序退出 → 它等窗口退干净再整树覆盖 → 启动新版。
  拿自己当安装器，不多带一个二进制。
- 装在 Program Files 这种没写权限的地方时，设置页会直接说「自动更新用不了」，让人手动下载。
- 镜像站是第三方，所以清单里的 sha256 是**必填**的：校验不过就放弃这次更新，不装。

发版：

```powershell
.\release.ps1 -Version 1.0.1 -Notes "修了解析时闪退；设置页加了检查更新"
.\release.ps1 -Version 1.0.1 -NotesFile notes.md -Publish    # 顺手 gh release create
```

它会改 csproj 里的 `<Version>`、调 `pack.ps1`、算 sha256、写 `update\<运行时>.json`。
**发完记得把 `update\<运行时>.json` 提交推送** —— 客户端就是靠它发现新版的。

客户端读的清单长这样：

```json
{
  "version": "1.0.1",
  "notes": "修了解析时闪退；设置页加了检查更新。",
  "url": "https://github.com/dhvbjvvb/jicun-desktop/releases/download/v1.0.1/Jicun-win-x64.zip",
  "sha256": "（64 位十六进制）",
  "size": 95834112,
  "publishedAt": "2026-10-03"
}
```

## 命令行模式

同一个 exe，带参数就是无界面模式（批处理、排障、"下载器到底能不能下"这种验证用）：

| 命令 | 干什么 |
| --- | --- |
| `Jicun.exe` | 打开界面 |
| `Jicun.exe --download <url> [--name 文件名] [--dir 文件夹] [--kind video\|image\|audio]` | 下载一个文件（`--dir` 优先；没给就按 `--kind` 归到那一档，默认 video） |
| `Jicun.exe --download <url> --audio --title 标题 --artist 歌手 --album 专辑 --cover <封面地址>` | 当音频下，落盘后补 ID3/MP4 标签 |
| `Jicun.exe --parse <链接或整段分享文案>` | 只看解析结果，并打印走的是哪条路 |
| `Jicun.exe --hosts` | 拉一次 /ips.json，看域名热更结果 |
| `Jicun.exe --secrets` | 看上游直连密钥配没配 |
| `Jicun.exe --apply-update --pid 进程号 --from 新目录 --to 安装目录` | 等主程序退出后覆盖安装并重启（更新流程内部用，别手敲） |
| `Jicun.exe --help` | 用法 |

## 结构

| 路径 | 干什么的 |
| --- | --- |
| Jicun.Desktop.csproj | 单工程。net10.0-windows10.0.19041.0 / win-x64。含 IncludePriInPublish |
| pack.ps1 | 自包含 publish + zip；`-Verify` 打完启动一次验包 |
| release.ps1 | 发版：改版本号 → 打包 → 算 sha256 → 写 update/<运行时>.json → 可选 gh release create |
| update/<运行时>.json | 发版清单：版本 / 更新说明 / 下载地址 / sha256。客户端检测的就是它 |
| Program.cs / Cli.cs | 入口分流：有参数走命令行，没参数起界面 |
| MainWindow.xaml(.cs) | 自绘标题栏 + 左侧 NavigationView + 应用图标 |
| Models/MediaModels.cs | MediaItem / VideoVariant / ParseResult，含解析应答的字段映射 |
| Services/ParseService.cs | 两条路：上游聚合直连（先试）+ media-parser（兜底）；判成败、取错误文案 |
| Services/UpstreamMapping.cs | 上游应答 → ParseResult：平台识别、清晰度归一去重、图集清理、文案清洗 |
| Services/MediaProbe.cs | 读 MP4 文件头（moov→trak→tkhd）拿真实宽高，把「原画」变成 3210P 这种真分辨率 |
| Services/Secrets.cs | 上游密钥与基址（默认值在本机私有的 LocalDefaults.cs，不进仓库；环境变量 / %LOCALAPPDATA%\\Jicun\\secrets.json 可覆盖） |
| Services/LocalDefaults.cs.example | 内置默认值（密钥 / 基址 / 兜底域名）的模板：复制成 LocalDefaults.cs 填自己的，那份不进仓库 |
| Services/ApiHosts.cs | 域名热更：拉 /ips.json、信任校验、候选顺序、白名单、本地存档 |
| Services/DownloadService.cs | 队列 + Range 分段并行 + 段级重试 + 文件头嗅探定后缀 + 断点续传 + 音频标签 |
| Services/AudioTags.cs | ID3v2.4 / MP4 ilst 标签写入；按文件头判格式，写临时文件再原子替换 |
| Services/ResumeStore.cs | 断点续传的进度存档 |
| Services/SettingsService.cs | 三个保存位置（视频含实况 / 图片 / 音频，键名 videoFolder / imageFolder / audioFolder）+ 旧配置迁移，存 %LOCALAPPDATA%\\Jicun\\settings.json |
| Services/HistoryService.cs | 解析历史，存 %LOCALAPPDATA%\\Jicun\\history.json，最多 200 条 |
| Services/AppServices.cs | 进程内服务定位器（App 太小，不值得上 DI 容器） |
| Services/UpdateService.cs | 检查更新：拉清单（镜像 / CDN）、下载、sha256 校验、解压、起安装进程 |
| Services/UpdateInstaller.cs | 被 `--apply-update` 调起来：等主程序退出 → 覆盖安装 → 重启新版 |
| Views/ | 解析、下载、历史、设置四个页面 + 预览对话框 |
| Views/UpdateDialog.cs | 更新公告窗口（更新说明 + 进度条），以及检查更新的编排 |
| Converters/ | URL 字符串 → Image.Source（x:Bind 不做隐式转换） |

## 已实现

- 解析：整段分享文案里自动挑链接，Enter 直接解析，粘贴按钮读剪贴板。
  Windows 没有 Android 那条明文限制，所以上游应答里的 `http://` 直链**不做** https 升级
  （与 Android 版刻意不同：那边升级是必须的，这边升级反而多一次失败机会）。
- 结果摊平成卡片：视频（多清晰度各一张、实况图单独一张）、图片、音频；封面等于视频封面时不重复列图片。
- 清晰度标签显示**真实分辨率**而不是上游的「原画 / 高清 / 原画 34.7Mbps」：
  应答里带了 width/height 就直接算短边（抖音根节点 7680x3210 → `3210P`）；
  只给了 `quality:"original"` 或 `"原画"` 又没宽高的（快手），就去读视频文件头里的真实宽高
  （快手那条标着原画的其实和它 720p 档同为 1600x704 → `704P`）。
  探测只挑标签里没数字的档，最多 3 个、整体 6 秒，失败就保留原标签 —— 它只是锦上添花，绝不能让解析失败。
- 预览：图片 / 视频 / 音频三种，都走 ContentDialog；视频音频用 MediaPlayerElement
  （原生 HLS + 自带传输控件），图片可缩放。
- 下载：3 路并发，单文件大于 8MB 走 4 段 Range 并行，每段最多重试 2 次，
  落盘后按文件头修正后缀，重名自动加序号；进程重启后能接着下。
- 音频下载完自动写 ID3 / MP4 标签（标题、作者、专辑、封面）。
  写标签要整份文件读进内存再原子替换，可能失败 —— 那时文件已经下好了，所以写不进去只吞掉，不判下载失败。
- 域名热更：启动后台拉一次 /ips.json，换域名、取平台白名单、存本地。
- 保存位置分三档、各自可自选：**视频（含实况图，落盘是 mp4）/ 图片 / 音频**。
  默认分别是 `Videos\即存`、`Pictures\即存`、`Music\即存`（Windows 对应库下加「即存」子目录）；
  设置页每档一个「选择...」和「打开」，下载页每张卡显示自己会存到哪。
  选目录走 WinRT FolderPicker + HWND 互操作（非打包应用必须这步）。
  旧版只有单一下载目录的配置会自动铺满三档，不会让老用户的文件突然换地方。
- 历史点击「重新解析」直接带着链接回解析页，卡片左边是封面图（老记录没存封面就显示占位图标）。
- 同一个链接重复解析只留最新一条（旧的整条换掉，不会堆出一串一模一样的记录）；
  老配置文件里已经堆下的重复项会在启动读历史时自动合并一次。
- 下载页和历史页都能清理记录：点「选择」进勾选模式（按钮变「完成」，每行出现勾选框），
  逐条勾或点「全选」，再点「删除」。**只删记录，磁盘上已经下好的文件一个都不动**；
  「全选」是个开关，已经全选时再点一次就变成取消全选。
- 取消下载后状态是「已取消」，不会卡在「下载中」，也不会出现「已完成 25.9 MB / 27.5 MB」这种矛盾数字。
- 无界面命令行模式，见上。

## 还没做

- FLAC 标签写入。AudioTags 只识别 FLAC 不写（Android 版也只写 mp3/mp4）。
- **真实上游已在 4 个平台实测通过**：抖音 / 快手 / 微信视频号 / 豆包各拿真实分享链接跑通，
  路由分别是 `upstream:douyin` / `upstream:kuaishou` / `upstream:wechatchannels` / `upstream:doubao`。
  本地 mock 的回归用例仍在，详见 [UPSTREAM.md](UPSTREAM.md)。
- MSIX 打包与签名。现在是绿色包，解压即用（自动更新已经做了，见上面「更新」）。
- 上游应答里 `.m3u8` 的清晰度会被丢掉（下载器不做 HLS 分片拼接），与 Android 版一致。

## 配置

| 东西 | 在哪 |
| --- | --- |
| 三个保存位置 | 设置页；%LOCALAPPDATA%\\Jicun\\settings.json |
| 解析历史 | %LOCALAPPDATA%\\Jicun\\history.json（最多 200 条） |
| 域名热更存档 | %LOCALAPPDATA%\\Jicun\\server-config.json |
| 忽略的版本 | %LOCALAPPDATA%\Jicun\update-state.json |
| 更新包缓存 / 解压目录 | %LOCALAPPDATA%\Jicun\update\（装完自动清；`--apply-update` 的日志是 update.log） |
| 上游密钥 | 默认值在本机私有的 LocalDefaults.cs（不进仓库，仓库里只有空占位）；覆盖：环境变量 `JICUN_UPSTREAM_KEY` / `JICUN_UPSTREAM_BASE`，或 %LOCALAPPDATA%\\Jicun\\secrets.json |

默认值放在 `Services\LocalDefaults.cs`（本机私有，仓库里没有这个文件，只有值全空的
LocalDefaults.Fallback.cs），环境变量和 secrets.json 仍然优先，换 key 不必重新构建。
用 `Jicun.exe --secrets` 看当前用的是哪一档、来源是什么。
默认值编进二进制后能被反编译读出 —— 真要做全量分发，应改成让客户端拿短期 token 走自建代理。

## 解析应答契约

- 兜底那条路：`GET https://<兜底域名>/parse?url=<UrlEncode(shareUrl)>`，20 秒超时。域名在 LocalDefaults.cs / server-config.json 里配。
- 上游那条路：`<upstreamBase>/api/dyjx | /api/ksjx | /api/wxsph | /api/doubao`，
  12 秒超时，带 `X-API-Key`。每个平台一条独立接口，拿错平台会回 422。

成功看 `succ == true`，或 `code / retcode / status` 为 0 或 200；
三个都没有时，只要带了 `data` 对象也算成功（上游应答就是包在 `data` 里的）。
失败文案优先取 `retdesc / error / message / msg`。例如无效链接会回：

```json
{"retcode":400,"retdesc":"该内容可能为私密/日常作品或已被作者删除","succ":false,"data":null}
```

⚠️ 上游解析失败用的是 **HTTP 400 + retdesc**，不是 200 + succ:false —— 不能拿状态码当结论。
数据体在 `data`（或 `result`）。中文必须按 UTF-8 解，别依赖 Content-Type 里的 charset，
缺了会按 latin-1 解成乱码。
