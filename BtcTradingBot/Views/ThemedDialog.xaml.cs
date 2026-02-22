using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BtcTradingBot.Views;

public partial class ThemedDialog : Window
{
    private ThemedDialog()
    {
        InitializeComponent();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>알림 메시지 (확인 버튼만)</summary>
    public static void Alert(string title, string message, AlertType type = AlertType.Warning)
    {
        var dlg = new ThemedDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.CancelBtn.Visibility = Visibility.Collapsed;
        dlg.OkBtn.Content = "확인";
        ApplyIcon(dlg, type);
        SetOwner(dlg);
        dlg.ShowDialog();
    }

    /// <summary>확인 질문 (예/아니오)</summary>
    public static bool Confirm(string title, string message, string yesText = "예", string noText = "아니오")
    {
        var dlg = new ThemedDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.CancelBtn.Visibility = Visibility.Visible;
        dlg.CancelBtn.Content = noText;
        dlg.OkBtn.Content = yesText;
        ApplyIcon(dlg, AlertType.Question);
        SetOwner(dlg);
        return dlg.ShowDialog() == true;
    }

    private static void SetOwner(ThemedDialog dlg)
    {
        var mainWin = Application.Current.MainWindow;
        if (mainWin != null && mainWin.IsVisible)
            dlg.Owner = mainWin;
        else
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private static void ApplyIcon(ThemedDialog dlg, AlertType type)
    {
        switch (type)
        {
            case AlertType.Warning:
                dlg.IconCircle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
                dlg.IconText.Text = "!";
                break;
            case AlertType.Error:
                dlg.IconCircle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
                dlg.IconText.Text = "✕";
                break;
            case AlertType.Question:
                dlg.IconCircle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6));
                dlg.IconText.Text = "?";
                break;
            case AlertType.Info:
                dlg.IconCircle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6));
                dlg.IconText.Text = "i";
                break;
        }
    }
}

public enum AlertType { Warning, Error, Question, Info }
