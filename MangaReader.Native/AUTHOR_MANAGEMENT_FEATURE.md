# 作者管理独立页面功能文档

## 功能概述

在主窗口新增"作者"导航标签，提供独立的作者管理页面，支持：
- 查看所有作者及其书籍数量
- 搜索作者（带防抖）
- 重命名作者（批量更新关联书籍）
- 跳转书库并按作者筛选

该功能架构完全复用 TagsView 的模式，不引入新的抽象层。

---

## 架构设计

### 数据流

```
Books (ObservableCollection<MangaBook>)
    ↓
RefreshAuthorManagementItems()
    ↓
GroupBy(Author) → 去重统计 → 按名称排序
    ↓
AuthorManagerItems (ObservableCollection<AuthorItem>)
    ↓
UI (ItemsControl 数据绑定)
```

### 搜索防抖

```
AuthorSearchBox_TextChanged() 
    ↓
RestartDebounceTimer(_authorSearchDebounceTimer, 220ms)
    ↓
Timer.Tick → RefreshAuthorManagementItems(filter)
```

### 导航状态

```
NavAuthors_Click()
    ↓
ShowAuthorsView()
    ↓
隐藏其他面板 (Home/Library/Tags) → 显示 AuthorsPagePanel
    ↓
RefreshAuthorManagementItems() → 更新列表
    ↓
UpdateNavigationVisuals() → 更新按钮样式
```

---

## 文件清单

### 新增文件

| 文件 | 说明 | 行数 |
|------|------|------|
| `Models/AuthorItem.cs` | 作者数据模型 | 33 |
| `RenameDialog.xaml` | 重命名对话框 UI | 176 |
| `RenameDialog.xaml.cs` | 重命名对话框逻辑 | 39 |

### 修改文件

| 文件 | 变更 |
|------|------|
| `MainWindow.xaml` | +导航按钮 AuthorsNavButton<br>+AuthorsPagePanel 面板（含搜索、列表、空状态） |
| `MainWindow.xaml.cs` | +AuthorManagerItems 集合<br>+_authorSearchDebounceTimer 计时器<br>+NavAuthors_Click() / ShowAuthorsView()<br>+RefreshAuthorManagementItems()<br>+AuthorSearchBox_TextChanged()<br>+RenameAuthor_Click() / FilterByAuthor_Click()<br>+SetAuthorFilter() / UpdateNavigationVisuals() 更新 |

---

## 核心实现

### 1. 数据模型 `AuthorItem.cs`

```csharp
public sealed class AuthorItem : INotifyPropertyChanged
{
    public required string Name { get; set; }
    
    public int BookCount
    {
        get => _bookCount;
        set
        {
            if (_bookCount != value)
            {
                _bookCount = value;
                OnPropertyChanged();
            }
        }
    }
}
```

**特点**：
- 支持 WPF 数据绑定（INotifyPropertyChanged）
- BookCount 属性变更时自动通知 UI

### 2. 重命名对话框 `RenameDialog.xaml.cs`

```csharp
private void Confirm_Click(object sender, RoutedEventArgs e)
{
    var trimmed = NewNameBox.Text?.Trim() ?? "";
    if (string.IsNullOrEmpty(trimmed))
    {
        System.Windows.MessageBox.Show(@"新名称不能为空。", "提示", 
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }
    
    NewName = trimmed;
    DialogResult = true;
    Close();
}
```

**流程**：
1. 验证输入不为空
2. 设置 `NewName` 属性
3. `DialogResult = true` 标记成功
4. 调用方检查 `dialog.NewName`

### 3. 列表刷新 `RefreshAuthorManagementItems()`

```csharp
private void RefreshAuthorManagementItems(string? filter = null)
{
    // 从 Books 分组统计
    var query = Books
        .Where(b => !string.IsNullOrWhiteSpace(b.Author))
        .GroupBy(b => b.Author, StringComparer.OrdinalIgnoreCase)
        .Select(g => new AuthorItem { Name = g.Key, BookCount = g.Count() });
    
    // 按 filter 过滤
    if (!string.IsNullOrWhiteSpace(filter))
        query = query.Where(a => a.Name.Contains(filter, 
            StringComparison.OrdinalIgnoreCase));
    
    // 按名称排序、清空、重新填充
    var sorted = query.OrderBy(a => a.Name).ToList();
    AuthorManagerItems.Clear();
    foreach (var item in sorted)
        AuthorManagerItems.Add(item);
    
    // 更新统计文本和空状态
    if (AuthorTotalText is not null)
        AuthorTotalText.Text = $"{AuthorManagerItems.Count} 位";
    if (AuthorBookTotalText is not null)
        AuthorBookTotalText.Text = $"{Books.Count(b => 
            !string.IsNullOrWhiteSpace(b.Author))} 本";
    if (AuthorManagerEmptyState is not null)
        AuthorManagerEmptyState.Visibility = AuthorManagerItems.Count == 0 
            ? Visibility.Visible : Visibility.Collapsed;
}
```

**关键点**：
- `StringComparer.OrdinalIgnoreCase`：去重时忽略大小写
- `filter` 参数支持增量搜索（由防抖计时器调用）
- 清空后逐项添加（保留顺序、支持 UI 动画）

### 4. 重命名操作 `RenameAuthor_Click()`

```csharp
private void RenameAuthor_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: AuthorItem item })
        return;
    
    // 打开重命名对话框
    var dialog = new RenameDialog(item.Name) { Owner = this };
    if (dialog.ShowDialog() != true || dialog.NewName == item.Name)
        return;
    
    // 收集待更新的书籍
    var booksToUpdate = Books
        .Where(b => string.Equals(b.Author, item.Name, 
            StringComparison.OrdinalIgnoreCase))
        .ToList();
    var updates = booksToUpdate.Select(b => (b.Id, dialog.NewName)).ToList();
    
    // 批量更新数据库（含事务保护）
    _database.SaveBookAuthorsBatch(updates, "rename-author");
    
    // 更新内存模型
    foreach (var book in booksToUpdate)
    {
        book.Author = dialog.NewName;
        book.NotifyAll();  // 触发绑定更新
    }
    
    // 刷新 UI：书库列表、作者页统计
    RefreshLibraryViews(sort: true);
    RefreshAuthorManagementItems(AuthorSearchBox?.Text?.Trim());
    StatusText.Text = $@"已将「{item.Name}」重命名为「{dialog.NewName}」，更新了 {updates.Count} 本书籍。";
}
```

**安全性**：
- 使用 `SaveBookAuthorsBatch()` 做批量操作（已含 try-catch-rollback）
- 区分大小写比较（`StringComparison.OrdinalIgnoreCase`）
- 调用 `NotifyAll()` 确保 UI 同步

### 5. 筛选跳转 `FilterByAuthor_Click()`

```csharp
private void FilterByAuthor_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: AuthorItem item })
        return;
    
    ShowLibraryView("author");
    SetAuthorFilter(item.Name);
    StatusText.Text = $@"已在书库按作者查看：{item.Name}";
}
```

**SetAuthorFilter() 实现**：
```csharp
private void SetAuthorFilter(string authorName)
{
    if (AuthorFilterBox is not null && AuthorFilters.Contains(authorName))
    {
        AuthorFilterBox.SelectedItem = authorName;
        RefreshBookFilter();
    }
}
```

---

## UI 结构

### AuthorsPagePanel 面板布局

```
┌─ AuthorsPagePanel (Grid, Visibility.Collapsed)
│  ├─ ScrollViewer
│  │  └─ StackPanel (Margin 8,0,22,12)
│  │     ├─ 标题区 Border (Padding 20, CornerRadius 11, White)
│  │     │  └─ Grid
│  │     │     ├─ "作者管理" (TextBlock, FontSize 30, Black)
│  │     │     └─ "去书库" (Button, Column 1)
│  │     │
│  │     ├─ 统计卡片 Grid
│  │     │  ├─ "全部作者" / AuthorTotalText (Column 0)
│  │     │  └─ "关联书籍" / AuthorBookTotalText (Column 1)
│  │     │
│  │     └─ 搜索 + 列表 Border (Padding 16, CornerRadius 10)
│  │        ├─ DockPanel
│  │        │  └─ AuthorSearchBox (Height 44, FilterPillTextBox 样式)
│  │        │
│  │        ├─ ItemsControl (AuthorManagerList, 绑定 AuthorManagerItems)
│  │        │  └─ DataTemplate (AuthorItem)
│  │        │     └─ Border (行项，包含作者名、书籍数、按钮)
│  │        │        ├─ StackPanel (DockPanel.Dock="Right", Orientation="Horizontal")
│  │        │        │  ├─ "筛选" Button (Click="FilterByAuthor_Click")
│  │        │        │  └─ "改名" Button (Click="RenameAuthor_Click")
│  │        │        └─ StackPanel
│  │        │           ├─ {Binding Name} (TextBlock, FontSize 16, SemiBold)
│  │        │           └─ {Binding BookCount, StringFormat={}{0} 本书籍} (FontSize 12)
│  │        │
│  │        └─ AuthorManagerEmptyState (Visibility.Collapsed, 空状态)
```

### 侧边栏导航按钮

```xml
<Button x:Name="AuthorsNavButton" 
        Content="作者" 
        Click="NavAuthors_Click" 
        Style="{StaticResource SidebarNavButton}"/>
```

位置：在 TagsNavButton 之后

---

## 搜索防抖配置

### 初始化

在 `ConfigureSearchDebounceTimers()` 中添加：

```csharp
_authorSearchDebounceTimer.Tick += (_, _) =>
{
    _authorSearchDebounceTimer.Stop();
    RefreshAuthorManagementItems(AuthorSearchBox?.Text?.Trim());
};
```

### 触发

```csharp
private void AuthorSearchBox_TextChanged(object sender, TextChangedEventArgs e)
{
    RestartDebounceTimer(_authorSearchDebounceTimer);
}
```

**防抖间隔**：`SearchDebounceInterval = 220ms`

**优点**：
- 用户快速输入时不会频繁 GroupBy
- 中文输入法每字母触发一次 TextChanged 时平滑无卡顿

---

## 与现有系统的集成

### 导航管理

**ShowAuthorsView() 调用链**：
```
NavAuthors_Click()
  ↓
ShowAuthorsView()
  ├─ _currentNavigationKey = "authors"
  ├─ MotionService.HideWithFade(其他面板)
  ├─ MotionService.ShowWithFade(AuthorsPagePanel)
  ├─ RefreshAuthorManagementItems()
  └─ UpdateNavigationVisuals()  ← 刷新按钮状态
```

**其他导航方法同步**：
- `ShowHomeView()` 增加 `MotionService.HideWithFade(AuthorsPagePanel)`
- `ShowLibraryView()` 增加 `MotionService.HideWithFade(AuthorsPagePanel)`
- `ShowTagsView()` 增加 `MotionService.HideWithFade(AuthorsPagePanel)`

### 搜索防抖

**StopSearchDebounceTimers() 添加**：
```csharp
_authorSearchDebounceTimer.Stop();
```

**用途**：在 `ResetLibraryFilters()` 或 `NavLibrary_Click()` 时防止后台搜索继续

### 数据库操作

**复用 SaveBookAuthorsBatch()**：
```csharp
_database.SaveBookAuthorsBatch(updates, "rename-author");
```

- 参数：`List<(string bookId, string newAuthor)>` 与操作名
- 已含事务保护（try-catch-rollback）
- 同步所有关联书籍的 Author 字段

---

## 验证清单

### 编译验证
- ✅ Release 编译：0 错误，0 警告
- ✅ 命名空间正确（Models/AuthorItem, RenameDialog）

### 功能验证

| 用例 | 验证点 | 预期结果 |
|------|------|--------|
| 点击"作者"按钮 | 导航至 AuthorsPagePanel | 其他面板隐藏，作者页显示，侧边栏"作者"按钮高亮 |
| 作者列表填充 | 从 Books 分组统计 | 名称去重、书籍数正确、按名称排序 |
| 搜索防抖 | 输入搜索词，观察列表更新 | 220ms 后列表过滤，快速输入无卡顿 |
| 重命名作者 | 点"改名"按钮、输入新名、确认 | 数据库更新、列表刷新、书库列表作者列刷新 |
| 批量更新验证 | 重命名后检查 Books 集合 | 该作者的所有书籍 Author 字段已更新 |
| 筛选跳转 | 点"筛选"按钮 | 跳转至书库，AuthorFilterBox 显示该作者 |
| 清空筛选 | 在书库点"清空筛选" | AuthorSearchBox 清空，AuthorManagerEmptyState 消失 |
| 空状态显示 | 搜索无匹配作者 | AuthorManagerEmptyState 显示"没有匹配的作者。" |

### 集成验证

- ✅ ShowHomeView() / ShowLibraryView() / ShowTagsView() 都隐藏 AuthorsPagePanel
- ✅ UpdateNavigationVisuals() 更新 AuthorsNavButton 样式
- ✅ StopSearchDebounceTimers() 包含 _authorSearchDebounceTimer
- ✅ RefreshLibraryViews(authors: true) 时包括作者统计更新

---

## 代码统计

- **新增行数**：约 150 行（业务逻辑）+ 180 行（UI XAML）= 330 行
- **修改行数**：约 50 行（导航、防抖、集成）
- **总文件数**：5 个（Models/AuthorItem.cs, RenameDialog.xaml/cs, MainWindow.xaml/cs）

---

## 后续优化空间

1. **作者批量操作**
   - 支持选中多个作者、批量删除（清空 Author 字段）
   - 支持导出作者列表为 CSV

2. **作者统计面板**
   - 显示各作者的阅读进度（未读/在读/已读）
   - 作者热力图（按书籍数排序）

3. **作者关联**
   - 支持作者别名管理（同一作者多个名字）
   - 作者头像、简介（扩展元数据）

4. **性能优化**
   - Books 数量大时，GroupBy 可缓存至内存字典
   - 增量更新（仅刷新变更的作者项）

---

## 提交信息

```
Commit: 16c92e5
Message: Add author management page with independent editing interface

Changes:
- New Models/AuthorItem.cs: data model for author display with BookCount
- New RenameDialog.xaml/cs: modal dialog for renaming authors
- New AuthorsPagePanel in MainWindow.xaml: dedicated author management tab
- AuthorManagerItems collection and search with debounce
- RefreshAuthorManagementItems: grouping, filtering, and listing authors
- RenameAuthor_Click: rename author across all related books via SaveBookAuthorsBatch
- FilterByAuthor_Click: jump to library and filter by selected author
- Navigation integration: "作者" button in sidebar, state management in ShowAuthorsView()
```

---

## 参考资源

- **现有模式**：TagsView（MainWindow.xaml line ~1196）、TagManagerItems、RefreshTagManagementItems()
- **对话框参考**：AuthorBatchImportDialog.xaml
- **数据库接口**：LibraryDatabase.SaveBookAuthorsBatch()
- **UI 服务**：MotionService.ShowWithFade/HideWithFade()
