# MangaViewer

Windows 本地漫画管理与阅读器。文件夹优先、离线优先、紧凑克制。

## 这是什么

把一堆散在硬盘里的漫画文件夹整理成一个可检索、可阅读、可管理的本地书库。不上云、不抓元数据、不解压压缩包——它只和你磁盘里现成的图片文件夹打交道。

## 核心特性

**书库管理**

- 自动扫描文件夹生成书库，按目录结构推断作者
- 标签体系：分组、颜色、互斥、用户自定义 + 内置预设；支持标记颜色循环切换
- 评分（0~5，0.5 步长）、收藏、阅读状态、阅读进度
- 多维筛选：作者、标签、状态、隐藏作品；多种排序（标题 / 评分 / 页数 / 容量 / 阅读次数 / 录入 / 出品时间）；一键"只看隐藏作品"
- 批量管理：Shift 范围加选 / Ctrl 强制减选 / 空白拖动框选 / 批量改标签、封面样式、去前缀
- 瀑布流右键菜单：快速切换封面样式、隐藏作品、隐私封面、打开文件夹
- 瀑布流虚拟化，几千本流畅

**阅读器**

- 单页 / 双页、适宽 / 适高 / 原图、翻页 / 滚动
- 单字母快捷键：`W` 全屏、`E` 适高、`Q` 适宽、`S` 单双页、`Z` 滚轮模式、`D` 切 HUD、`C` 翻转方向、`A` 背景循环、`X` 退出
- 鼠标：左键按点击位置翻页、右键长按临时放大、中键切 HUD
- 读完自动跳下一本，遵循当前书库筛选 + 排序

**数据安全**

- 元数据编辑前自动备份（节流防抖）
- 评分等用户付出心智的字段走"半托管"路径，扫描器不会覆盖
- 数据库目录与发布产物均不入仓

## 快速开始

要求 Windows + .NET 8 SDK。

```powershell
dotnet build .\MangaReader.Native\MangaReader.Native.csproj
dotnet run --project .\MangaReader.Native\MangaReader.Native.csproj
```

首次启动后在侧边栏点 **添加书库**，选择一个根目录即可。

## 数据与备份

运行时数据全部在 `MangaReader_Data/` 下（与主程序同级），**不会上传 Git**：

- `app.db`：SQLite 数据库
- `backups/`：手动 / 自动备份
- 缩略图与封面缓存

侧边栏 **立即备份** 把当前 `app.db` 复制到 `backups/`，**打开备份** 打开目录方便管理。

恢复备份时**先关软件**，把目标备份复制为 `app.db` 覆盖，建议先把当前 `app.db` 另存一份。

迁移书库到新机：复制整个 `MangaReader_Data/` 即可。

## 发布与更新

**本地打包**：

```powershell
.\pack.ps1 -Mode standalone     # 自包含 ~60MB，无需 .NET 8 Runtime
.\pack.ps1 -Mode runtime-dep    # 依赖 Runtime 的轻量包
```

脚本会自动从 `icon.png` 生成 `AppIcon.ico` 并打包到 `_release/`。

**侧边栏检查更新** 走本地优先：

1. `_release/` 下的更高版本目录或 zip
2. 源码版本更高时自动 `dotnet publish`
3. 最后才访问 GitHub Release

**正式发版**：

```powershell
git tag v0.7.1.0
git push origin v0.7.1.0
```

GitHub Actions 监听 `v*` tag，自动构建 `MangaReader-win-x64-v*.zip` 并上传 Release。

## 项目结构

| 路径 | 说明 |
|---|---|
| `MangaReader.Native/` | WPF 主程序源码 |
| `MangaReader.Updater/` | 独立更新器，主程序退出后替换文件 |
| `漫画阅读器开发文档.md` | 版本记录与设计取舍 |
| `AI_TECHNICAL_HANDOFF.md` | 改动约束与未来路线（面向接手开发者 / AI） |

仓库根目录还残留早期 Vite / Avalonia 试验代码（`src/`、`dist/`、`MangaReader.Avalonia/`），**不在活跃维护范围**。

## 不做的方向

为避免误投入：

- 云同步、在线漫画源、OPDS — 离线优先是硬约束
- 压缩包（zip / cbz / rar / cbr）— 文件夹优先
- 抓取远程元数据 — 用户本地决定
- 移动端 / Web 端 — 项目定位 Windows 桌面
- AI 自动打 tag / 图像增强 — 与"管理 + 阅读"主线偏离

详见 `AI_TECHNICAL_HANDOFF.md` 第 14 节。

## 许可

仅个人使用。
