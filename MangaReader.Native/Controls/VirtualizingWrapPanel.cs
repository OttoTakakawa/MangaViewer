using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MangaReader.Native.Services;
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

    // PinterestMode=true 时启用非均匀高度瀑布流：每列固定宽度 = ItemWidth，
    // 每个 item 高度按其 AspectRatioPath 属性计算（h = ItemWidth / aspect），
    // 按「最短列累加」放置。aspect 被 clamp 到 [AspectMin, AspectMax] 防极端值。
    public static readonly DependencyProperty PinterestModeProperty =
        DependencyProperty.Register(
            nameof(PinterestMode),
            typeof(bool),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty AspectRatioPathProperty =
        DependencyProperty.Register(
            nameof(AspectRatioPath),
            typeof(string),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata("AspectRatio", FrameworkPropertyMetadataOptions.AffectsMeasure));

    // aspect clamp 范围：3:1 横图到 1:3 竖图，超出范围回退到 1.0（避免破坏布局）
    private const double AspectMin = 1.0 / 3.0;
    private const double AspectMax = 3.0;
    private const double AspectFallback = 1.0;

    private WSize _extent;
    private WSize _viewport;
    private WPoint _offset;

    // Pinterest 模式缓存
    private List<WRect>? _pinterestLayout;
    private int _pinterestCachedItemCount = -1;
    private double _pinterestCachedItemWidth = double.NaN;
    private double _pinterestCachedAvailableWidth = double.NaN;
    private int _pinterestCachedColumns = -1;

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

    public bool PinterestMode
    {
        get => (bool)GetValue(PinterestModeProperty);
        set => SetValue(PinterestModeProperty, value);
    }

    public string AspectRatioPath
    {
        get => (string)GetValue(AspectRatioPathProperty);
        set => SetValue(AspectRatioPathProperty, value);
    }

    // 强制失效 Pinterest 布局缓存。外部数据变化（如 PixelWidth/PixelHeight 回填）后调用。
    public void InvalidateLayout()
    {
        _pinterestLayout = null;
        _pinterestCachedItemCount = -1;
        _pinterestCachedItemWidth = double.NaN;
        _pinterestCachedAvailableWidth = double.NaN;
        _pinterestCachedColumns = -1;
        InvalidateMeasure();
    }

    // 返回与 [viewportTop, viewportBottom] 相交的 item 索引列表（精确，基于 _pinterestLayout）。
    // 用于缩略图加载器精确知道当前哪些 item 可见，避免估算偏差导致优先级错误。
    // overscan 像素扩展上下边界，让即将可见的图也优先加载。
    public List<int> GetVisibleIndices(double viewportTop, double viewportBottom, double overscanPixels = 0)
    {
        var result = new List<int>();
        if (_pinterestLayout is null || _pinterestLayout.Count == 0)
        {
            return result;
        }

        var top = viewportTop - overscanPixels;
        var bottom = viewportBottom + overscanPixels;

        for (var i = 0; i < _pinterestLayout.Count; i++)
        {
            var rect = _pinterestLayout[i];
            if (rect.Bottom < top) continue;
            if (rect.Y > bottom) continue;
            result.Add(i);
        }
        return result;
    }

    // 判断 PinterestMode 是否已生效（_pinterestLayout 非空）
    public bool HasPinterestLayout => _pinterestLayout is { Count: > 0 };

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
        return PinterestMode
            ? PinterestMeasureOverride(availableSize)
            : UniformMeasureOverride(availableSize);
    }

    protected override WSize ArrangeOverride(WSize finalSize)
    {
        return PinterestMode
            ? PinterestArrangeOverride(finalSize)
            : UniformArrangeOverride(finalSize);
    }

    // ===== Pinterest 非均匀瀑布流 =====

    private WSize PinterestMeasureOverride(WSize availableSize)
    {
        var itemCount = GetItemCount();
        var columns = GetColumnCount(availableSize.Width);

        // 诊断日志：确认 PinterestMode 真的生效
        if (_pinterestLayout is null)
        {
            AppLogger.Info("misc-panel", $"PinterestMeasureOverride: itemCount={itemCount}, columns={columns}, ItemWidth={ItemWidth}, availableWidth={availableSize.Width}");
        }

        _viewport = new WSize(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        // 缓存命中判断：itemCount / ItemWidth / availableWidth / columns 任一变化则重算布局
        var needRebuild = _pinterestLayout is null
            || itemCount != _pinterestCachedItemCount
            || ItemWidth != _pinterestCachedItemWidth
            || availableSize.Width != _pinterestCachedAvailableWidth
            || columns != _pinterestCachedColumns;

        if (needRebuild)
        {
            _pinterestLayout = BuildPinterestLayout(itemCount, columns);
            _pinterestCachedItemCount = itemCount;
            _pinterestCachedItemWidth = ItemWidth;
            _pinterestCachedAvailableWidth = availableSize.Width;
            _pinterestCachedColumns = columns;
        }

        var slotWidth = ItemWidth + HorizontalSpacing;
        _extent = new WSize(
            Math.Max(_viewport.Width, columns * slotWidth),
            Math.Max(_viewport.Height, ComputePinterestExtentHeight(_pinterestLayout)));

        CoerceOffsets();
        ScrollOwner?.InvalidateScrollInfo();

        if (itemCount == 0)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
            return availableSize;
        }

        var overscan = Math.Max(0, OverscanRows);
        var overscanHeight = ItemWidth * 3 * overscan; // 估算 overscan 距离（极端 aspect 下界）
        var top = Math.Max(0, _offset.Y - overscanHeight);
        var bottom = _offset.Y + _viewport.Height + overscanHeight;

        var (firstIndex, lastIndex) = FindVisibleRange(_pinterestLayout!, top, bottom, itemCount);

        RealizeItems(firstIndex, lastIndex);
        CleanUpItems(firstIndex, lastIndex);

        // 给每个已实现 child Measure 它在 Pinterest 布局中的 slot 尺寸
        var generator = ItemContainerGenerator;
        foreach (UIElement child in InternalChildren)
        {
            if (generator is null)
            {
                child.Measure(new WSize(ItemWidth, ItemWidth));
                continue;
            }
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(InternalChildren.IndexOf(child), 0));
            if (itemIndex < 0 || itemIndex >= _pinterestLayout!.Count)
            {
                child.Measure(new WSize(ItemWidth, ItemWidth));
                continue;
            }
            var rect = _pinterestLayout[itemIndex];
            child.Measure(new WSize(rect.Width, rect.Height));
        }

        return availableSize;
    }

    private List<WRect> BuildPinterestLayout(int itemCount, int columns)
    {
        var layout = new List<WRect>(itemCount);
        if (itemCount == 0 || columns <= 0)
        {
            return layout;
        }

        var aspects = ReadAspects(itemCount);
        var columnHeights = new double[columns];
        var slotWidth = ItemWidth + HorizontalSpacing;

        for (var i = 0; i < itemCount; i++)
        {
            var aspect = aspects[i];
            if (aspect < AspectMin || aspect > AspectMax)
            {
                aspect = AspectFallback;
            }
            var height = ItemWidth / aspect;

            var minCol = 0;
            for (var c = 1; c < columns; c++)
            {
                if (columnHeights[c] < columnHeights[minCol])
                {
                    minCol = c;
                }
            }

            var x = minCol * slotWidth;
            var y = columnHeights[minCol];
            layout.Add(new WRect(x, y, ItemWidth, height));
            columnHeights[minCol] = y + height + VerticalSpacing;
        }

        return layout;
    }

    private List<double> ReadAspects(int itemCount)
    {
        var aspects = new List<double>(itemCount);
        var path = AspectRatioPath;
        var owner = ItemsControl.GetItemsOwner(this);
        var items = owner?.Items;

        if (items is null || items.Count == 0 || string.IsNullOrEmpty(path))
        {
            for (var i = 0; i < itemCount; i++)
            {
                aspects.Add(AspectFallback);
            }
            return aspects;
        }

        // 从第一个 item 获取 PropertyInfo（假设集合内类型一致）
        System.Reflection.PropertyInfo? property = null;
        for (var i = 0; i < items.Count && property is null; i++)
        {
            property = items[i]?.GetType().GetProperty(path);
        }

        for (var i = 0; i < itemCount; i++)
        {
            var item = i < items.Count ? items[i] : null;
            var value = item is not null && property is not null
                ? property.GetValue(item) as double?
                : null;
            aspects.Add(value ?? AspectFallback);
        }
        return aspects;
    }

    private static double ComputePinterestExtentHeight(List<WRect>? layout)
    {
        if (layout is null || layout.Count == 0)
        {
            return 0;
        }
        var max = 0.0;
        for (var i = 0; i < layout.Count; i++)
        {
            var bottom = layout[i].Bottom;
            if (bottom > max)
            {
                max = bottom;
            }
        }
        return max + 10; // 末尾留少量余量
    }

    private static (int FirstIndex, int LastIndex) FindVisibleRange(List<WRect> layout, double top, double bottom, int itemCount)
    {
        if (layout.Count == 0)
        {
            return (0, -1);
        }

        // layout 按 item 顺序排列，但 y 并不严格单调（因为是「最短列」放置），
        // 所以只能线性扫描。itemCount 通常数千级，O(N) 可接受。
        var first = -1;
        var last = -1;
        for (var i = 0; i < layout.Count && i < itemCount; i++)
        {
            var rect = layout[i];
            if (rect.Bottom < top)
            {
                continue;
            }
            if (rect.Y > bottom)
            {
                // 不能直接 break，因为「最短列」布局中 y 不单调
                // 但大多数情况下末尾 y 都很大，留个快速路径：若当前 y > bottom 且所有列都到过 bottom 之后，可结束
                continue;
            }
            if (first < 0)
            {
                first = i;
            }
            if (i > last)
            {
                last = i;
            }
        }

        // 第二轮：把 y > bottom 但被前面阻塞的项也包进来（确保 lastIndex 之后不再有大块可见区）
        // 已通过线性扫描覆盖。

        if (first < 0)
        {
            return (0, -1);
        }
        if (last < 0)
        {
            last = first;
        }
        // 扩展 lastIndex 包含 y 略大于 bottom 的 item（overscan 边界）
        while (last + 1 < itemCount && layout[last + 1].Y <= bottom + 1)
        {
            last++;
        }
        return (first, last);
    }

    private WSize PinterestArrangeOverride(WSize finalSize)
    {
        if (_pinterestLayout is null || _pinterestLayout.Count == 0)
        {
            return finalSize;
        }

        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return finalSize;
        }

        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0 || itemIndex >= _pinterestLayout.Count)
            {
                continue;
            }
            var rect = _pinterestLayout[itemIndex];
            var adjusted = new WRect(
                rect.X - _offset.X,
                rect.Y - _offset.Y,
                rect.Width,
                rect.Height);
            child.Arrange(adjusted);
        }

        return finalSize;
    }

    private int PinterestMakeVisible(int itemIndex)
    {
        if (_pinterestLayout is null || itemIndex < 0 || itemIndex >= _pinterestLayout.Count)
        {
            return 0;
        }
        var rect = _pinterestLayout[itemIndex];
        var y = rect.Y;
        if (y < VerticalOffset)
        {
            SetVerticalOffset(y);
        }
        else if (y + rect.Height > VerticalOffset + ViewportHeight)
        {
            SetVerticalOffset(y + rect.Height - ViewportHeight);
        }
        return itemIndex;
    }

    // ===== 原均匀网格实现（PinterestMode=false 时使用）=====

    private WSize UniformMeasureOverride(WSize availableSize)
    {
        // 诊断日志：如果 PinterestMode=true 但仍走了 Uniform 路径，说明属性没生效
        if (PinterestMode && _pinterestLayout is null)
        {
            AppLogger.Warn("misc-panel", $"UniformMeasureOverride called but PinterestMode=true! itemCount={GetItemCount()}");
        }
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

    private WSize UniformArrangeOverride(WSize finalSize)
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

    public void LineUp() => SetVerticalOffset(VerticalOffset - Math.Max(48, ViewportHeight / 8));
    public void LineDown() => SetVerticalOffset(VerticalOffset + Math.Max(48, ViewportHeight / 8));
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

        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return WRect.Empty;
        }

        var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (itemIndex < 0)
        {
            return WRect.Empty;
        }

        if (PinterestMode)
        {
            PinterestMakeVisible(itemIndex);
            if (_pinterestLayout is not null && itemIndex < _pinterestLayout.Count)
            {
                return _pinterestLayout[itemIndex];
            }
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
