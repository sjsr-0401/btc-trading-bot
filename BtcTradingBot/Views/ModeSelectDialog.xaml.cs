using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BtcTradingBot.Views;

public partial class ModeSelectDialog : Window
{
    /// <summary>"Classic" 또는 "Multi". null이면 종료 선택.</summary>
    public string? SelectedMode { get; private set; }

    public ModeSelectDialog(string? lastMode = null)
    {
        InitializeComponent();

        // 마지막 선택 하이라이트
        if (lastMode == "Classic")
            HighlightCard(ClassicCard, "#FF3B82F6");
        else if (lastMode == "Multi")
            HighlightCard(MultiCard, "#FF22C55E");
    }

    private static void HighlightCard(System.Windows.Controls.Border card, string color)
    {
        card.BorderBrush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Classic_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectedMode = "Classic";
        DialogResult = true;
    }

    private void Multi_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectedMode = "Multi";
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = null;
        DialogResult = false;
    }
}
