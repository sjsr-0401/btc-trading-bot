using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BtcTradingBot.Models;
using BtcTradingBot.ViewModels;

namespace BtcTradingBot.Views;

public partial class ScannerControl : UserControl
{
    public ScannerControl()
    {
        InitializeComponent();
    }

    private void CoinRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is CoinScanResult result &&
            DataContext is ScannerViewModel vm)
        {
            vm.SelectCoinCommand.Execute(result);
        }
    }
}
