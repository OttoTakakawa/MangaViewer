# 编辑模式 — 删除源文件 设计文档

## 需求

在编辑模式→危险操作区，新增"删除源文件"按钮，永久删除漫画的源文件夹。

## 现有代码位置

### 危险操作区 XAML

文件: `MangaReader.Native/MainWindow.xaml` 第 2477-2490 行

```xml
<TextBlock Text="危险操作" Style="{StaticResource DetailSectionTitleText}" Margin="0,0,0,8"/>
<StackPanel Orientation="Horizontal">
    <Button x:Name="PrivacyCoverButtonEdit" Content="保持隐私封面"
            Click="ToggleBookPrivacyCover_Click"
            Style="{StaticResource DetailFileActionButton}"/>
    <Button x:Name="HideBookButtonEdit" Content="隐藏作品"
            Click="HideBook_Click"
            Style="{StaticResource DetailFileActionButton}"/>
    <Button Content="删除库记录"
            Click="DeleteBook_Click"
            Style="{StaticResource DetailDangerInlineButton}"/>
</StackPanel>
```

### 删除库记录事件处理

文件: `MangaReader.Native/MainWindow.xaml.cs` 第 1100-1125 行

```csharp
private async void DeleteBook_Click(object sender, RoutedEventArgs e)
{
    if (_currentBook is null) return;
    var book = _currentBook;
    var result = MessageBox.Show(
        $"确定从书库中删除《{book.Title}》的记录吗？...",
        "删除库记录", MessageBoxButton.YesNo, MessageBoxImage.Warning);
    if (result != MessageBoxResult.Yes) return;
    await Task.Run(() => _database.DeleteBook(book));
    _allBooks.Remove(book);
    // ... 刷新界面 ...
}
```

### 数据库方法

文件: `MangaReader.Native/Services/LibraryDatabase.cs` 第 555-563 行

```csharp
public void DeleteBook(MangaBook book)
{
    BackupDatabase("before-delete-book", force: true);
    // DELETE FROM books WHERE id = $id;
}
```

### 源文件夹路径

文件: `MangaReader.Native/Models/MangaBook.cs` 第 55 行

```csharp
public string FolderPath { get; set; } = "";
```

### 可用样式

- `DetailDangerInlineButton` — 红色危险按钮（36px 高，18px padding），已有"删除库记录"使用

## 交互设计

### 确认流程（双重确认）

```
用户点击"删除源文件"
  │
  ├── 对话框 1: "确定永久删除《书名》的源文件夹及其所有内容吗？此操作不可恢复！"
  │      ├── [取消] → 结束
  │      └── [确定]
  │
  ├── 对话框 2: 输入书名匹配（防误操作）
  │      ├── 为空/不匹配 → 提示错误，留在对话框
  │      └── 输入正确 → 执行删除
  │
  └── 后台删除（await Task.Run）
         ├── Directory.Delete(FolderPath, recursive: true)
         ├── _database.DeleteBook(book)  // 同步删除库记录
         ├── 刷新界面
         └── 文件夹不存在 → 静默只删库记录
```

### 与"删除库记录"的区别

| | 删除库记录 | 删除源文件 |
|---|---|---|
| 数据库记录 | 删除 | 删除 |
| 源文件夹 | 保留 | 永久删除 |
| 确认方式 | 一次 MessageBox | 双重确认（确认 + 书名匹配） |
| 可恢复性 | 有备份 | 不可恢复 |
| 按钮样式 | DetailDangerInlineButton | DetailDangerInlineButton |

## 实现步骤

### 1. XAML — 新增按钮

在 `删除库记录` 按钮后添加：

```xml
<Button x:Name="DeleteSourceButton"
        Content="删除源文件"
        Click="DeleteSourceFiles_Click"
        Style="{StaticResource DetailDangerInlineButton}"
        Visibility="Collapsed"/>
```

- `x:Name` 用于 `SetEditMode()` 中控制可见性
- 通过 `DeleteSourceButton.Visibility` 在编辑模式切换时控制

### 2. C# — SetEditMode 添加可见性控制

在 `SetEditMode(bool enabled)` 方法（约第 2700 行附近）中，将 `DeleteSourceButton` 加入可见性控制。参考现有模式：

```csharp
// 已有的模式：编辑按钮显示/隐藏
EditModeButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
DeleteSourceButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
```

### 3. C# — DeleteSourceFiles_Click 方法

写入 `MainWindow.xaml.cs`，建议放在 `DeleteBook_Click` 附近：

```csharp
private async void DeleteSourceFiles_Click(object sender, RoutedEventArgs e)
{
    if (_currentBook is null) return;
    var book = _currentBook;

    // 第一重确认
    var first = System.Windows.MessageBox.Show(
        $"确定永久删除《{book.Title}》的源文件夹及其所有内容吗？\n\n" +
        $"路径：{book.FolderPath}\n\n" +
        "此操作不可恢复！",
        "删除源文件",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning);

    if (first != MessageBoxResult.Yes) return;

    // 第二重确认：输入书名
    var input = Microsoft.VisualBasic.Interaction.InputBox(
        $"请输入书名以确认删除（需完全匹配）：\n{book.Title}",
        "最后确认（必须匹配书名）",
        "");

    if (string.IsNullOrEmpty(input) || input != book.Title)
    {
        StatusText.Text = "书名不匹配，已取消。";
        return;
    }

    // 执行删除
    await DeleteSourceFilesAsync();
}

private async Task DeleteSourceFilesAsync()
{
    var book = _currentBook!;
    var folderPath = book.FolderPath;
    var title = book.Title;

    try
    {
        await Task.Run(() =>
        {
            // 删库记录
            _database.DeleteBook(book);

            // 删源文件
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
            }
        });

        // UI 更新
        _allBooks.Remove(book);
        Books.Remove(book);
        _currentBook = null;
        BooksList.SelectedItem = null;
        Dispatcher.Invoke(() => SetDetailVisible(false));
        RefreshLibraryViews(tagManager: false, authors: true);
        RefreshHomeShelves();
        StatusText.Text = $"已删除《{title}》的源文件夹及库记录。";
    }
    catch (Exception ex)
    {
        StatusText.Text = $"删除失败：{ex.Message}";
        AppLogger.Error("file", ex, $"删除源文件夹失败：{folderPath}");
    }
}
```

### 4. 需要添加的 using

```csharp
using Microsoft.VisualBasic; // InputBox
```

`Microsoft.VisualBasic` 是 .NET 标准库，无需额外 NuGet 包。

### 5. 特殊情况处理

| 情况 | 行为 |
|------|------|
| 文件夹不存在（已被手动删除） | `Directory.Exists` 为 false，只删库记录 |
| 文件夹被占用 | `Directory.Delete` 抛异常 → catch 中提示，不删库记录 |
| 权限不足 | 同上，抛异常 |
| 用户在两次确认间取消 | 安全退出，无副作用 |

## 可选增强

- **回收站模式**：用 `Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folderPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)` 替代 `Directory.Delete`。优点：可恢复；缺点：大文件夹可能失败或极慢，不推荐。

## 版本号

建议升到 `0.7.0`（新功能）而非补丁号。
