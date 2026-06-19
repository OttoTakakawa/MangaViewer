# 剪贴板操作卡顿问题 - 技术文档

## 现象

在作品编辑页使用 Ctrl+C / Ctrl+V / Ctrl+X 快捷键时，界面明显卡顿。

## 根因

中文输入法（IME）在组合文字期间会调用 `OpenClipboard` 锁定系统剪贴板。此时应用再调用 `Clipboard.SetText()` 会抛出：

```
COMException (0x800401D0): OpenClipboard 失败 (CLIPBRD_E_CANT_OPEN)
```

当前的临时修复（`MainWindow.xaml.cs` 中的 `SafeSetClipboard` / `SafeGetClipboard`）采用 **同步重试 + Thread.Sleep** 策略：

```csharp
private static void SafeSetClipboard(string text)
{
    for (var i = 0; i < 3; i++)
    {
        try
        {
            System.Windows.Clipboard.SetDataObject(text, true);
            return;
        }
        catch (COMException) when (i < 2)
        {
            Thread.Sleep(30);  // ← 阻塞 UI 线程，导致卡顿
        }
    }
}
```

**问题**：`Thread.Sleep` 阻塞了 UI 线程，最多阻塞 3 × 30ms = 90ms，造成可感知的卡顿。

## 相关代码位置

- `MangaReader.Native/MainWindow.xaml.cs` 第 2753~2790 行：`SafeSetClipboard` / `SafeGetClipboard`
- `MangaReader.Native/MainWindow.xaml.cs` 第 2713~2752 行：`MainWindow_PreviewKeyDown`（调用上述方法）
- `MangaReader.Native/MainWindow.xaml.cs` 第 2626~2639 行：`CopyBookTitle_Click`（也调用 SafeSetClipboard）
- `MangaReader.Native/App.xaml.cs` 第 25~36 行：`App_DispatcherUnhandledException`（未捕获异常会触发 Shutdown）

## 崩溃日志（已解决）

`Data/logs/crash-20260619-*.log` 中三次崩溃均为同一原因：

```
System.Runtime.InteropServices.COMException (0x800401D0): OpenClipboard 失败 (CLIPBRD_E_CANT_OPEN)
   at System.Windows.Clipboard.Flush()
   at MangaReader.Native.MainWindow.MainWindow_PreviewKeyDown
```

## 推荐修复方案

### 方案 A：异步重试（推荐）

将剪贴板操作放到后台线程，避免阻塞 UI：

```csharp
private static async Task SafeSetClipboardAsync(string text)
{
    for (var i = 0; i < 5; i++)
    {
        try
        {
            // 需要在 UI 线程调用 Clipboard API
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.Clipboard.SetDataObject(text, true);
            });
            return;
        }
        catch (COMException)
        {
            await Task.Delay(50);  // 不阻塞 UI 线程
        }
    }
}
```

调用处改为 `async`：

```csharp
case Key.C:
    if (!string.IsNullOrEmpty(box.SelectedText))
        await SafeSetClipboardAsync(box.SelectedText);  // 需要将 handler 改为 async void
    e.Handled = true;
    break;
```

**注意**：`PreviewKeyDown` handler 需改为 `async void`，且 `e.Handled = true` 的时序需要注意。

### 方案 B：Win32 API 直接操作剪贴板

用 P/Invoke 直接调用 `OpenClipboard` / `SetClipboardData` / `CloseClipboard`，绕过 WPF 的 `Clipboard.Flush()`：

```csharp
using System.Runtime.InteropServices;

[DllImport("user32.dll", SetLastError = true)]
private static extern bool OpenClipboard(IntPtr hWndNewOwner);

[DllImport("user32.dll", SetLastError = true)]
private static extern bool CloseClipboard();

[DllImport("user32.dll", SetLastError = true)]
private static extern bool EmptyClipboard();

[DllImport("user32.dll", SetLastError = true)]
private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

[DllImport("kernel32.dll", SetLastError = true)]
private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

[DllImport("kernel32.dll", SetLastError = true)]
private static extern IntPtr GlobalLock(IntPtr hMem);

[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool GlobalUnlock(IntPtr hMem);

private const uint CF_UNICODETEXT = 13;
private const uint GMEM_MOVEABLE = 0x0002;

private static void Win32SetClipboard(string text)
{
    if (!OpenClipboard(IntPtr.Zero))
    {
        // 剪贴板被占用，跳过而非崩溃
        return;
    }
    try
    {
        EmptyClipboard();
        var bytes = (text.Length + 1) * 2;  // UTF-16
        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
        if (hGlobal == IntPtr.Zero) return;
        var ptr = GlobalLock(hGlobal);
        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr + text.Length * 2, 0);  // null terminator
        GlobalUnlock(hGlobal);
        SetClipboardData(CF_UNICODETEXT, hGlobal);
    }
    finally
    {
        CloseClipboard();
    }
}
```

**优点**：完全同步，无阻塞，不会崩溃（`OpenClipboard` 失败直接跳过）。
**缺点**：需要手动管理非托管内存，代码量较大。

### 方案 C：延迟执行

不在 `PreviewKeyDown` 中立即操作剪贴板，而是 post 到 Dispatcher 队列：

```csharp
case Key.C:
    if (!string.IsNullOrEmpty(box.SelectedText))
    {
        var text = box.SelectedText;
        Dispatcher.BeginInvoke(() =>
        {
            try { System.Windows.Clipboard.SetDataObject(text, true); }
            catch (COMException) { /* 静默失败 */ }
        }, DispatcherPriority.Input);
    }
    e.Handled = true;
    break;
```

**优点**：简单，不阻塞按键处理。
**缺点**：剪贴板写入是异步的，如果用户快速连续按键可能丢失。

## 测试要点

1. 使用中文输入法（微软拼音、搜狗等）在编辑页输入文字
2. 输入过程中（输入法候选框显示时）按 Ctrl+X / Ctrl+C / Ctrl+V
3. 确认不卡顿、不闪退
4. 确认剪贴板内容正确（Ctrl+V 能粘贴出正确内容）
5. 测试快速连续按 Ctrl+C 多次
6. 测试 SummaryBox（多行 AcceptsReturn）和普通 TextBox 都正常
