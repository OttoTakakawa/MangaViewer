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

## 发布测试版

```powershell
dotnet publish .\MangaReader.Native\MangaReader.Native.csproj -c Release -r win-x64 --self-contained true -o .\_release\MangaReader-Test
```

`_release/` 已加入 `.gitignore`，不会上传发布产物。
