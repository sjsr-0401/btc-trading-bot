using System.Windows;
using System.Windows.Input;

namespace BtcTradingBot.Views;

public partial class TrayConfirmDialog : Window
{
    /// <summary>"트레이로 이동" 선택 시 true, "종료" 선택 시 false, "취소"면 null</summary>
    public bool? UserChoice { get; private set; }

    public TrayConfirmDialog()
    {
        InitializeComponent();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        UserChoice = true;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        UserChoice = false;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserChoice = null;
        DialogResult = false;
    }
}
