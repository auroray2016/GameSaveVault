using System.Windows;
using System.Windows.Media;

namespace GameSaveManager;

/// <summary>
/// 让元素（通常是放图片的 Border/Grid）按圆角真正裁剪自己的内容。
/// WPF 的 Border.ClipToBounds 只按矩形裁剪，子元素的直角会盖过 CornerRadius；
/// 挂上本属性后会随元素实际尺寸生成等半径的 RectangleGeometry 赋给 Clip。
/// 用法：local:CornerClip.Radius="8"
/// </summary>
public static class CornerClip
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius", typeof(CornerRadius), typeof(CornerClip),
            new PropertyMetadata(new CornerRadius(0), OnRadiusChanged));

    public static void SetRadius(DependencyObject element, CornerRadius value) => element.SetValue(RadiusProperty, value);
    public static CornerRadius GetRadius(DependencyObject element) => (CornerRadius)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= OnSizeChanged;
        fe.SizeChanged += OnSizeChanged;
        fe.Loaded -= OnLoaded;
        fe.Loaded += OnLoaded;
        Apply(fe);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((FrameworkElement)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged || e.HeightChanged) Apply((FrameworkElement)sender);
    }

    private static void Apply(FrameworkElement fe)
    {
        var r = GetRadius(fe);
        double w = fe.ActualWidth, h = fe.ActualHeight;
        if (w <= 0 || h <= 0 || (r.TopLeft <= 0 && r.TopRight <= 0 && r.BottomRight <= 0 && r.BottomLeft <= 0))
        {
            fe.ClearValue(UIElement.ClipProperty);
            return;
        }

        // 统一半径（本项目所有圆角图容器四角等半径）；夹到不超过短边的一半
        double radius = Math.Min(Math.Max(Math.Max(r.TopLeft, r.TopRight), Math.Max(r.BottomRight, r.BottomLeft)),
                                 Math.Min(w, h) / 2.0);
        fe.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }
}
