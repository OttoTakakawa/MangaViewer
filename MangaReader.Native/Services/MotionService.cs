using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MangaReader.Native.Services;

public static class MotionService
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(220);

    public static IEasingFunction StandardEase { get; } = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static IEasingFunction MicroEase { get; } = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    public static void FadeIn(DependencyObject target, DependencyProperty property, double from = 0, double to = 1)
    {
        var storyboard = new Storyboard();
        AddDouble(storyboard, target, property, from, to, Normal, StandardEase);
        storyboard.Begin();
    }

    public static void ShowWithFade(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0;
        element.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(0, 1, Normal)
        {
            EasingFunction = StandardEase
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    public static void HideWithFade(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        var animation = CreateDoubleAnimation(0, Fast, MicroEase);
        animation.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
            element.Opacity = 1;
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    public static void PlayPageSwapFeedback(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0.72;
        var animation = new DoubleAnimation(0.72, 1, Fast)
        {
            EasingFunction = MicroEase
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    public static void ScaleTo(UIElement element, double scale, TimeSpan? duration = null)
    {
        var transform = EnsureScaleTransform(element);
        var animation = CreateDoubleAnimation(scale, duration ?? Normal, StandardEase);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone());
    }

    public static void PressBounce(UIElement element)
    {
        ScaleTo(element, 0.985, Fast);
    }

    public static void ShowDrawer(UIElement element, double offset = 28)
    {
        var transform = EnsureTranslateTransform(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        element.Opacity = 0;
        transform.X = offset;
        element.Visibility = Visibility.Visible;
        element.BeginAnimation(UIElement.OpacityProperty, CreateDoubleAnimation(1, Normal, StandardEase));
        transform.BeginAnimation(TranslateTransform.XProperty, CreateDoubleAnimation(0, Normal, StandardEase));
    }

    public static void HideDrawer(UIElement element, double offset = 28)
    {
        var transform = EnsureTranslateTransform(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        var opacity = CreateDoubleAnimation(0, Fast, MicroEase);
        opacity.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
            element.Opacity = 1;
            transform.X = 0;
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacity);
        transform.BeginAnimation(TranslateTransform.XProperty, CreateDoubleAnimation(offset, Fast, MicroEase));
    }

    public static DoubleAnimation CreateDoubleAnimation(double to, TimeSpan duration, IEasingFunction easing)
    {
        return new DoubleAnimation(to, duration)
        {
            EasingFunction = easing
        };
    }

    private static ScaleTransform EnsureScaleTransform(UIElement element)
    {
        if (element.RenderTransform is ScaleTransform { IsFrozen: false } scale)
        {
            return scale;
        }

        var currentScaleX = element.RenderTransform is ScaleTransform existingScale ? existingScale.ScaleX : 1;
        var currentScaleY = element.RenderTransform is ScaleTransform existingScaleForY ? existingScaleForY.ScaleY : 1;
        scale = new ScaleTransform(currentScaleX, currentScaleY);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        return scale;
    }

    private static TranslateTransform EnsureTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } translate)
        {
            return translate;
        }

        var currentX = element.RenderTransform is TranslateTransform existingTranslate ? existingTranslate.X : 0;
        var currentY = element.RenderTransform is TranslateTransform existingTranslateForY ? existingTranslateForY.Y : 0;
        translate = new TranslateTransform(currentX, currentY);
        element.RenderTransform = translate;
        return translate;
    }

    private static void AddDouble(
        Storyboard storyboard,
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        TimeSpan duration,
        IEasingFunction easing,
        TimeSpan? delay = null)
    {
        var animation = new DoubleAnimation(from, to, duration)
        {
            BeginTime = delay,
            EasingFunction = easing
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }
}
