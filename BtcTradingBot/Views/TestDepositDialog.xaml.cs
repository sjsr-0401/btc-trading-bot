using System.Windows;

namespace BtcTradingBot.Views;

public partial class TestDepositDialog : Window
{
    public double ResultBalance { get; private set; }

    public TestDepositDialog(double currentBalance)
    {
        InitializeComponent();
        ResultBalance = currentBalance;
        BalanceInput.Text = currentBalance.ToString("F0");
        BalanceInput.SelectAll();
        BalanceInput.Focus();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            BalanceInput.Text = tag;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(BalanceInput.Text, out var val) && val >= 25)
        {
            ResultBalance = val;
            DialogResult = true;
        }
        else
        {
            ThemedDialog.Alert("입력 오류", "최소 25 USDT 이상 입력하세요.");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
