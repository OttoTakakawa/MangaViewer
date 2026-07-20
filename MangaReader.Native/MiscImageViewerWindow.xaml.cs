using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MangaReader.Native.Models;
using MangaReader.Native.Services;

namespace MangaReader.Native;

// 杂图单图浏览窗口。
// 独立于漫画 ReaderWindow，避免 MangaBook 强耦合侵入。
// 持有当前瀑布流筛选结果作为播放列表，左右键 / 按钮切换。
// 评分、评语、tag 编辑、收藏、反向重命名均通过 LibraryDatabase 持久化。
public partial class MiscImageViewerWindow : Window
{
    private readonly LibraryDatabase _database;
    private readonly IReadOnlyList<MiscImage> _playlist;
    private int _currentIndex;
    private bool _suppressRatingChanged;
    private readonly DispatcherTimer _commentSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _pendingComment = "";

    public MiscImageViewerWindow(LibraryDatabase database, IReadOnlyList<MiscImage> playlist, int startIndex)
    {
        _database = database;
        _playlist = playlist;
        _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, _playlist.Count - 1));

        InitializeComponent();
        PreviewKeyDown += Window_PreviewKeyDown;
        _commentSaveTimer.Tick += CommentSaveTimer_Tick;

        SizeChanged += (_, _) => ApplyImageLayout();
        Loaded += (_, _) =>
        {
            ApplyImageLayout();
            ShowCurrent();
        };
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                GoTo(_currentIndex - 1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                GoTo(_currentIndex + 1);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void ShowCurrent()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        var image = _playlist[_currentIndex];
        LoadImage(image);
        UpdateChrome(image);
        UpdateCommentBox(image);
        UpdateRatingSlider(image);
        MarkOpened(image);
    }

    private void LoadImage(MiscImage image)
    {
        BitmapSource? bitmap = null;
        try
        {
            if (File.Exists(image.FilePath))
            {
                bitmap = ImageLoader.LoadBitmap(image.FilePath, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLogger.Warn("misc-viewer", $"加载图片失败：{image.FilePath}。{ex.Message}");
        }

        MainImage.Source = bitmap;
        ApplyImageLayout();
    }

    private void ApplyImageLayout()
    {
        if (MainImage.Source is BitmapSource src)
        {
            var ratio = src.PixelWidth / (double)Math.Max(1, src.PixelHeight);
            var maxW = ActualWidth - 48;
            var maxH = ActualHeight - 200;
            var w = Math.Min(maxW, maxH * ratio);
            var h = w / ratio;
            MainImage.Width = w;
            MainImage.Height = h;
            System.Windows.Controls.Canvas.SetLeft(MainImage, (ActualWidth - w) / 2);
            System.Windows.Controls.Canvas.SetTop(MainImage, (ActualHeight - h) / 2);
        }
    }

    private void UpdateChrome(MiscImage image)
    {
        FileNameText.Text = image.FileName;
        PositionText.Text = $"{_currentIndex + 1} / {_playlist.Count}";
        CounterText.Text = PositionText.Text;
    }

    private void UpdateCommentBox(MiscImage image)
    {
        _suppressCommentSave = true;
        CommentBox.Text = image.Comment;
        CommentOverlay.Visibility = image.HasComment ? Visibility.Visible : Visibility.Collapsed;
        _suppressCommentSave = false;
    }

    private bool _suppressCommentSave;

    private void UpdateRatingSlider(MiscImage image)
    {
        _suppressRatingChanged = true;
        RatingSlider.Value = image.Rating;
        RatingText.Text = image.Rating.ToString("0.#");
        _suppressRatingChanged = false;
    }

    private static void MarkOpened(MiscImage image)
    {
        // 调用方在持有最新引用前已经更新过，这里只刷新内存。
        image.LastOpenedAt = DateTimeOffset.Now.ToString("O");
    }

    private void GoTo(int index)
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        var clamped = (index % _playlist.Count + _playlist.Count) % _playlist.Count;
        if (clamped == _currentIndex)
        {
            return;
        }

        FlushComment();

        _currentIndex = clamped;
        ShowCurrent();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => GoTo(_currentIndex - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => GoTo(_currentIndex + 1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RatingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressRatingChanged || _playlist.Count == 0)
        {
            return;
        }

        var value = Math.Round(e.NewValue * 2, MidpointRounding.AwayFromZero) / 2;
        var image = _playlist[_currentIndex];
        image.Rating = value;
        RatingText.Text = value.ToString("0.#");
        try
        {
            _database.UpdateMiscImageRating(image.Id, value);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-viewer", $"保存评分失败：{image.Id}。{ex.Message}");
        }
    }

    private void EditComment_Click(object sender, RoutedEventArgs e)
    {
        CommentOverlay.Visibility = CommentOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (CommentOverlay.Visibility == Visibility.Visible)
        {
            CommentBox.Focus();
        }
    }

    private void CommentBox_LostFocus(object sender, RoutedEventArgs e)
    {
        FlushComment();
    }

    private void CommentBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressCommentSave || _playlist.Count == 0)
        {
            return;
        }

        _pendingComment = CommentBox.Text ?? "";
        _commentSaveTimer.Stop();
        _commentSaveTimer.Start();
    }

    private void CommentSaveTimer_Tick(object? sender, EventArgs e)
    {
        _commentSaveTimer.Stop();
        if (_playlist.Count == 0)
        {
            return;
        }

        var image = _playlist[_currentIndex];
        var text = _pendingComment;
        image.Comment = text;
        try
        {
            _database.UpdateMiscImageComment(image.Id, text);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-viewer", $"保存评语失败：{image.Id}。{ex.Message}");
        }
    }

    private void FlushComment()
    {
        if (_commentSaveTimer.IsEnabled)
        {
            _commentSaveTimer.Stop();
            CommentSaveTimer_Tick(null, EventArgs.Empty);
        }
    }

    private void EditTags_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        var image = _playlist[_currentIndex];
        var dialog = new TagNameDialog(image.Tags, "编辑 Tag（逗号分隔）")
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            var newTags = MiscTagService.FormatTags(MiscTagService.ParseTags(dialog.TagName));
            image.Tags = newTags;
            try
            {
                _database.UpdateMiscImageTags(image.Id, newTags);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("misc-viewer", $"保存 Tag 失败：{image.Id}。{ex.Message}");
            }
        }
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        var image = _playlist[_currentIndex];
        image.IsFavorite = !image.IsFavorite;
        try
        {
            _database.UpdateMiscImageFavorite(image.Id, image.IsFavorite);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-viewer", $"保存收藏失败：{image.Id}。{ex.Message}");
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        var image = _playlist[_currentIndex];
        if (!File.Exists(image.FilePath))
        {
            System.Windows.MessageBox.Show("找不到原文件，无法重命名。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new MiscRenameDialog(image.FilePath) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newFileName = dialog.NewFileName;
        var directory = Path.GetDirectoryName(image.FilePath) ?? "";
        var newPath = Path.Combine(directory, newFileName);

        if (File.Exists(newPath))
        {
            System.Windows.MessageBox.Show($"目标文件已存在：{newFileName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            File.Move(image.FilePath, newPath);
            _database.RenameMiscImageFile(image.Id, newPath, newFileName);
            image.FilePath = newPath;
            image.FileName = newFileName;
            UpdateChrome(image);
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-rename", ex, $"反向重命名失败：{image.FilePath} → {newPath}");
            System.Windows.MessageBox.Show($"反向重命名失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
