# MangaViewer

Windows 本地漫画管理与图片查看器。

## 当前定位

- WPF / .NET 8 原生桌面应用。
- SQLite 本地数据库。
- 面向本地文件夹漫画库管理。
- 支持作者、标签、封面、阅读状态、收藏、阅读进度与批量导入。
- 数据默认保存在程序目录旁的 `MangaReader_Data`，该目录不会提交到 Git。

## 项目结构

- `MangaReader.Native/`：当前主线 WPF 应用源码。
- `MangaReader.Updater/`：自动更新器，负责在主程序退出后替换发布目录文件。
- `漫画阅读器开发文档.md`：开发记录、架构约束与后续路线。

## 本地运行

要求：

- Windows
- .NET 8 SDK

```powershell
dotnet build .\MangaReader.Native\MangaReader.Native.csproj
dotnet run --project .\MangaReader.Native\MangaReader.Native.csproj
```

## 本地数据

本项目不上传任何本地漫画数据。

- `MangaReader_Data/`：运行时数据库、缩略图缓存、备份目录，已加入 `.gitignore`。
- `_release/`：本地发布产物，已加入 `.gitignore`。
- `bin/`、`obj/`：编译缓存，已加入 `.gitignore`。
- `*.db`、`*.db-wal`、`*.db-shm`：SQLite 数据文件，已加入 `.gitignore`。

如果需要迁移自己的书库数据，请手动复制本机的 `MangaReader_Data`，不要提交到 Git。

## 备份与恢复

软件侧边栏的 `立即备份` 会把当前 `app.db` 复制到 `MangaReader_Data/backups/`，`打开备份` 只是打开这个目录，方便查看和复制备份文件。

恢复备份时先关闭软件，再把目标备份文件复制到当前数据目录并重命名为 `app.db`。覆盖前建议把当前 `app.db` 另存一份，避免误恢复。

## 发布测试版

```powershell
dotnet publish .\MangaReader.Native\MangaReader.Native.csproj -c Release -r win-x64 --self-contained true -o .\_release\0.3.xx
```

发布目录会自动包含 `Updater\MangaReader.Updater.exe`。

主程序侧边栏的 `检查更新` 采用本地优先策略：

- 如果本机存在 `_release/更高版本/MangaReader.Native.exe`，直接使用该发布目录更新当前运行目录。
- 如果本机存在 `_release/` 或 `updates/` 下的更高版本 zip，直接使用该 zip 更新。
- 如果你已经手动 `git pull`，并且源码里的 `MangaReader.Native.csproj` 版本号高于当前运行版本，软件会先本地执行 `dotnet publish` 生成更新目录，再用 `MangaReader.Updater.exe` 替换当前运行目录。
- 只有本地没有可用更新时，才会读取 GitHub 最新 Release，并下载其中的 `MangaReader-win-x64-v*.zip` 资产。

因此开发机上的推荐流程是：先 `git pull`，再打开旧版 exe 点 `检查更新`。这条路径不依赖 GitHub Release 下载，但需要本机安装 .NET 8 SDK。

正式发布建议推送版本 Tag：

```powershell
git tag v0.3.xx
git push origin v0.3.xx
```

GitHub Actions 会自动构建 Windows x64 zip 并上传到对应 Release。

`_release/` 已加入 `.gitignore`，不会上传发布产物。
