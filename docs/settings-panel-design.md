# 全局设置面板 设计文档

## 需求

新建 `SettingsDialog`，将散落在侧边栏、隐藏 UI、阅读器顶栏的配置项统一收纳，提供一致的管理入口。

## 现有设置项清单

### 存储机制

| 存储 | 位置 | 用途 |
|------|------|------|
| `app_settings` 表（KV） | `LibraryDatabase.cs:96-100` | 全局偏好 |
| `shortcuts` 表（KV） | `LibraryDatabase.cs:90-94` | 快捷键 + 阅读器偏好（命名债） |
| `MangaReader_DataLocation.txt` | `AppStorage.cs:5` | 数据根目录 |

### 已有设置项

| Key | 表 | 用途 | 当前 UI 位置 |
|-----|----|------|---------------|
| `app.privacy_mode` | app_settings | 隐私模式开关 | 侧边栏按钮 |
| `tag.custom_colors` | app_settings | 自定义标签颜色 | 无独立 UI |
| `app.delete_source_password` | app_settings | 删除源文件密码 | 无 UI（默认 0309） |
| `app.catalog_delete_source_enabled` | app_settings | 目录中删除源文件开关 | 无 UI（默认开启） |
| `reader.next` | shortcuts | 下一页快捷键 | 隐藏的 UI（Visibility=Collapsed） |
| `reader.previous` | shortcuts | 上一页快捷键 | 隐藏的 UI |
| `reader.wheelmode` | shortcuts | 滚轮模式 | ReaderWindow 顶栏 |
| `reader.doublepage.gap` | shortcuts | 双页间距 | ReaderWindow 顶栏 |
| `reader.qualitymode` | shortcuts | 渲染质量 | ReaderWindow 顶栏 |
| `MangaReader_DataLocation.txt` | 文件 | 数据根目录 | 侧边栏按钮 |

### 新增设置项（随书签功能引入）

| Key | 表 | 用途 | 默认值 |
|-----|----|------|--------|
| `app.catalog_delete_source_enabled` | app_settings | 目录中删除源文件开关 | `"1"` |

## 对话框模式参考

项目已有 7 个对话框，统一模式：
- XAML：`WindowStyle="None"` + `AllowsTransparency="True"` + `AppDialogShadowBorder` + 三行 Grid（标题/内容/按钮）
- C#：构造函数注入依赖（`AppStorage`、`LibraryDatabase`），通过公开属性回传结果
- 样式：`AppDialogButton`、`AppDialogGhostButton`、`AppDialogDangerButton`、`AppDialogTitleText`、`AppDialogLabelText`、`AppDialogTextBox`
- 调用：`dialog.ShowDialog() == true` 后读取属性

参考文件：`DataSafetyDialog.xaml(.cs)`、`RenameDialog.xaml(.cs)`

## 设置面板结构

### 布局：左侧子导航 + 右侧内容

```
┌──────────────────────────────────────────────┐
│  设置                                    [×]  │
├──────────┬───────────────────────────────────┤
│ 通用     │                                   │
│ 阅读     │       （当前分区内容）              │
│ 数据     │                                   │
│ 标签     │                                   │
│ 危险     │                                   │
├──────────┴───────────────────────────────────┤
│                              [取消]  [保存]   │
└──────────────────────────────────────────────┘
```

### 分区 1：通用

| 设置项 | 控件 | 存储 Key | 说明 |
|--------|------|----------|------|
| 隐私模式 | ToggleButton | `app.privacy_mode` | 即时生效 |
| 删除源文件密码 | TextBox + 修改按钮 | `app.delete_source_password` | 点击修改时需先输入旧密码 |
| 目录中显示删除源文件 | CheckBox | `app.catalog_delete_source_enabled` | 控制详情页目录右键菜单是否显示删除源文件 |

### 分区 2：阅读

| 设置项 | 控件 | 存储 Key | 说明 |
|--------|------|----------|------|
| 下一页快捷键 | TextBox | `reader.next` | 复用现有 `ParseKeys` |
| 上一页快捷键 | TextBox | `reader.previous` | |
| 默认滚轮模式 | ComboBox | `reader.wheelmode` | 翻页/缩放/滚动 |
| 默认渲染质量 | ComboBox | `reader.qualitymode` | 质量/性能 |
| 默认双页间距 | Slider | `reader.doublepage.gap` | 0-80 |

> 注意：阅读器顶栏保留即时调节入口，设置面板只管"默认值"。

### 分区 3：数据

| 设置项 | 控件 | 说明 |
|--------|------|------|
| 数据根目录 | 只读 TextBox + "更改"按钮 | 调用 `AppStorage.SaveCustomRoot`，提示重启生效 |
| 打开备份文件夹 | Button | 复用现有逻辑 |
| 手动备份 | Button | 调用 `CreateManualBackupAsync` |
| 恢复备份 | Button | 打开 `DataSafetyDialog` |
| 检查更新 | Button | 复用现有 `CheckUpdate_Click` |

### 分区 4：标签

| 设置项 | 控件 | 存储 Key | 说明 |
|--------|------|----------|------|
| 自定义标签颜色 | 颜色列表编辑器 | `tag.custom_colors` | `;` 分隔的 `#RRGGBB` |

### 分区 5：危险

| 设置项 | 控件 | 说明 |
|--------|------|------|
| 清除所有书签 | Button | 需密码确认，调用 `RemoveAllBookmarks` |
| 重置所有设置为默认 | Button | 需确认 |

## UI 入口

### 侧边栏改动

在侧边栏"工具"组（`MainWindow.xaml:1110-1127`）新增"设置"按钮：

```xml
<Button Content="设置" Click="Settings_Click" Style="{StaticResource SidebarActionButton}"/>
```

### 迁移计划

迁移后从侧边栏移除：
- "指定数据" → 已收纳到设置→数据
- "隐私模式" → 已收纳到设置→通用
- "备份数据" → 已收纳到设置→数据

保留：
- "检查更新"（调试组）
- "展开日志"（调试组）

### 清理隐藏的快捷键 UI

`MainWindow.xaml:2519-2531` 的 `NextShortcutBox`/`PrevShortcutBox` 被 `Visibility="Collapsed"` 隐藏但 `LoadShortcuts()` 仍引用。迁移后删除这些控件，`LoadShortcuts` 改为从设置面板的实例读取或直接从数据库加载到内存字段。

## 实现步骤

### 1. 新建 SettingsDialog

```
SettingsDialog.xaml
SettingsDialog.xaml.cs
```

复制 `DataSafetyDialog` 骨架，改为左侧导航 + 右侧内容布局。

### 2. 构造函数注入

```csharp
public SettingsDialog(AppStorage storage, LibraryDatabase database)
{
    InitializeComponent();
    _storage = storage;
    _database = database;
    // 加载当前设置值到控件
}
```

### 3. 保存逻辑

点击"保存"时：
- 遍历所有分区，收集变更的设置项
- 批量调用 `_database.SaveSetting` / `SaveShortcut`
- 隐私模式即时生效（调用 `TogglePrivacyMode` 逻辑）
- 数据根目录变更提示重启
- `DialogResult = true; Close();`

### 4. MainWindow 入口

```csharp
private void Settings_Click(object sender, RoutedEventArgs e)
{
    var dialog = new SettingsDialog(_storage, _database) { Owner = this };
    if (dialog.ShowDialog() == true)
    {
        // 应用隐私模式等即时生效项
        ApplyPrivacyMode();
        // 重新加载快捷键
        LoadShortcuts();
    }
}
```

## 风险点

| 风险 | 方案 |
|------|------|
| `shortcuts` 表语义混淆 | 设置面板内部统一走 `SaveSetting/LoadSetting`，新增设置项不再写入 `shortcuts` 表 |
| 阅读器偏好的热应用 | 设置面板只管默认值，阅读器顶栏保留即时调节 |
| 数据根目录需重启 | 保留现有重启提示和 `RestartCurrentProcess` 逻辑 |
| 隐藏的快捷键 UI 残留 | 迁移后删除 `NextShortcutBox`/`PrevShortcutBox`，`LoadShortcuts` 改为直接读 DB |
| 侧边栏按钮迁移后空位 | 侧边栏"工具"组只剩"设置"一个按钮，可考虑合并到导航组 |

## 版本号

建议升到 `0.7.0`（新功能 + UI 重构）。
