using System.Windows;
using System.Windows.Controls;

namespace BtcTradingBot.Views;

/// <summary>
/// ScrollViewer.VerticalOffset는 DependencyProperty가 아니라 애니메이션 불가.
/// 이 Attached Property로 감싸서 DoubleAnimation 가능하게 함.
/// </summary>
public static class ScrollViewerBehavior
{
    public static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffset",
            typeof(double),
            typeof(ScrollViewerBehavior),
            new PropertyMetadata(0.0, OnVerticalOffsetChanged));

    public static double GetVerticalOffset(DependencyObject obj) =>
        (double)obj.GetValue(VerticalOffsetProperty);

    public static void SetVerticalOffset(DependencyObject obj, double value) =>
        obj.SetValue(VerticalOffsetProperty, value);

    private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
            sv.ScrollToVerticalOffset((double)e.NewValue);
    }
}
