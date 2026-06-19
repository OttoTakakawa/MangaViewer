using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WPoint = System.Windows.Point;
using WRect = System.Windows.Rect;
using WSize = System.Windows.Size;

namespace MangaReader.Native.Controls;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(214d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(344d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(18d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty OverscanRowsProperty =
        DependencyProperty.Register(
            nameof(OverscanRows),
            typeof(int),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private WSize _extent;
    private WSize _viewport;
    private WPoint _offset;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public int OverscanRows
    {
        get => (int)GetValue(OverscanRowsProperty);
        set => SetValue(OverscanRowsProperty, value);
    }

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public ScrollViewer? ScrollOwner { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    protected override WSize MeasureOverride(WSize availableSize)
    {
        var itemCount = GetItemCount();
        var columns = GetColumnCount(availableSize.Width);
        var slotWidth = ItemWidth + HorizontalSpacing;
        var slotHeight = ItemHeight + VerticalSpacing;
        var rowCount = itemCount == 0 ? 0 : (int)Math.Ceiling((double)itemCount / columns);

        _viewport = new WSize(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        _extent = new WSize(
            Math.Max(_viewport.Width, columns * slotWidth),
            Math.Max(_viewport.Height, rowCount * slotHeight));

        CoerceOffsets();
        ScrollOwner?.InvalidateScrollInfo();

        if (itemCount == 0)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
            return availableSize;
        }

        var overscan = Math.Max(0, OverscanRows);
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(_offset.Y / slotHeight) - overscan);
        var lastVisibleRow = Math.Min(rowCount - 1, (int)Math.Ceiling((_offset.Y + _viewport.Height) / slotHeight) + overscan);
        var firstIndex = Math.Min(itemCount - 1, firstVisibleRow * columns);
        var lastIndex = Math.Min(itemCount - 1, ((lastVisibleRow + 1) * columns) - 1);

        RealizeItems(firstIndex, lastIndex);
        CleanUpItems(firstIndex, lastIndex);

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new WSize(ItemWidth, ItemHeight));
        }

        return availableSize;
    }

    protected override WSize ArrangeOverride(WSize finalSize)
    {
        var columns = GetColumnCount(finalSize.Width);
        var slotWidth = ItemWidth + HorizontalSpacing;
        var slotHeight = ItemHeight + VerticalSpacing;
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return finalSize;
        }

        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0)
            {
                continue;
            }

            var row = itemIndex / columns;
            var column = itemIndex % columns;
            var x = column * slotWidth - _offset.X;
            var y = row * slotHeight - _offset.Y;
            child.Arrange(new WRect(new WPoint(x, y), new WSize(ItemWidth, ItemHeight)));
        }

        return finalSize;
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 48);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 48);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 96);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 96);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 48);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 48);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);
    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - 96);
    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + 96);

    public void SetHorizontalOffset(double offset)
    {
        _offset.X = Math.Clamp(offset, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        _offset.Y = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public WRect MakeVisible(Visual visual, WRect rectangle)
    {
        if (visual is not UIElement element)
        {
            return WRect.Empty;
        }

        var childIndex = InternalChildren.IndexOf(element);
        if (childIndex < 0)
        {
            return WRect.Empty;
        }

        var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (itemIndex < 0)
        {
            return WRect.Empty;
        }

        var columns = GetColumnCount(ViewportWidth);
        var row = itemIndex / columns;
        var y = row * (ItemHeight + VerticalSpacing);
        if (y < VerticalOffset)
        {
            SetVerticalOffset(y);
        }
        else if (y + ItemHeight > VerticalOffset + ViewportHeight)
        {
            SetVerticalOffset(y + ItemHeight - ViewportHeight);
        }

        return new WRect(0, y, ItemWidth, ItemHeight);
    }

    private void RealizeItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator is null || firstIndex < 0 || lastIndex < firstIndex)
        {
            return;
        }

        var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;
        childIndex = Math.Max(0, childIndex);

        using var context = generator.StartAt(startPosition, GeneratorDirection.Forward, true);
        for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
        {
            if (generator.GenerateNext(out var newlyRealized) is not UIElement child)
            {
                continue;
            }

            if (!newlyRealized)
            {
                continue;
            }

            if (childIndex >= InternalChildren.Count)
            {
                AddInternalChild(child);
            }
            else
            {
                InsertInternalChild(childIndex, child);
            }

            generator.PrepareItemContainer(child);
        }
    }

    private void CleanUpItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return;
        }

        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var generatorPosition = new GeneratorPosition(childIndex, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(generatorPosition);
            if (itemIndex < 0)
            {
                RemoveInternalChildRange(childIndex, 1);
                continue;
            }

            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                continue;
            }

            RemoveInternalChildRange(childIndex, 1);
            try
            {
                generator.Remove(generatorPosition, 1);
            }
            catch
            {
            }
        }
    }

    private int GetColumnCount(double availableWidth)
    {
        var width = double.IsInfinity(availableWidth) || availableWidth <= 0 ? ItemWidth : availableWidth;
        var itemSlotWidth = ItemWidth + HorizontalSpacing;
        if (itemSlotWidth <= 0)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Floor((width + HorizontalSpacing) / itemSlotWidth));
    }

    private int GetItemCount()
    {
        return ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
    }

    private void CoerceOffsets()
    {
        _offset.X = Math.Clamp(_offset.X, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        _offset.Y = Math.Clamp(_offset.Y, 0, Math.Max(0, ExtentHeight - ViewportHeight));
    }
}
