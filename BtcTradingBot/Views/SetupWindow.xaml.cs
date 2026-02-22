using System.Windows;
using System.Windows.Input;
using BtcTradingBot.ViewModels;

namespace BtcTradingBot.Views;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        if (DataContext is SetupViewModel vm)
        {
            vm.OnSetupComplete += () =>
            {
                DialogResult = true;
                Close();
            };
        }
    }

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm)
            vm.ApiSecret = SecretBox.Password;
    }

    private void SkipToInput_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SetupViewModel vm)
            vm.GoToInputCommand.Execute(null);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm)
            vm.PrevStepCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
