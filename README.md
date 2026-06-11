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

```powershell
dotnet build .\MangaReader.Native\MangaReader.Native.csproj
dotnet run --project .\MangaReader.Native\MangaReader.Native.csproj
```

## 发布测试版

```powershell
dotnet publish .\MangaReader.Native\MangaReader.Native.csproj -c Release -r win-x64 --self-contained true -o .\_release\MangaReader-Test
```

`_release/` 已加入 `.gitignore`，不会上传发布产物。
