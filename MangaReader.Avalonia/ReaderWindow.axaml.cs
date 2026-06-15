using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using MangaReader.Core.Models;

namespace MangaReader.Avalonia;

public sealed partial class ReaderWindow : Window
{
    private readonly MangaBook _book;
    private int _pageIndex;

    public ReaderWindow(MangaBook book)
    {
        InitializeComponent();
        _book = book;
        _pageIndex = Math.Clamp(book.LastReadPageIndex, 0, Math.Max(book.Pages.Count - 1, 0));
        Title = book.Title;
        TitleText.Text = book.Title;
        RenderPage();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Previous_Click(object? sender, RoutedEventArgs e)
    {
        Navigate(-1);
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        Navigate(1);
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        OpenPath(_book.FolderPath);
    }

    private void ReaderWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.A:
            case Key.Left:
                Navigate(-1);
                e.Handled = true;
                break;
            case Key.D:
            case Key.Right:
            case Key.Space:
                Navigate(1);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Navigate(int delta)
    {
        if (_book.Pages.Count == 0)
        {
            return;
        }

        _pageIndex = Math.Clamp(_pageIndex + delta, 0, _book.Pages.Count - 1);
        _book.LastReadPageIndex = _pageIndex;
        RenderPage();
    }

    private void RenderPage()
    {
        PageImage.Source = null;

        if (_book.Pages.Count == 0)
        {
            PlaceholderText.IsVisible = true;
            PageText.Text = "0 / 0";
            return;
        }

        var path = _book.Pages[_pageIndex];
        PageText.Text = $"{_pageIndex + 1} / {_book.Pages.Count}";
        PlaceholderText.IsVisible = false;

        try
        {
            using var stream = File.OpenRead(path);
            PageImage.Source = new Bitmap(stream);
        }
        catch
        {
            PlaceholderText.IsVisible = true;
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", path);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            Process.Start("xdg-open", path);
        }
        catch
        {
        }
    }
}
