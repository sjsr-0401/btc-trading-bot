using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BtcTradingBot.Views;

public partial class MultiTradingControl : UserControl
{
    public MultiTradingControl()
    {
        InitializeComponent();

        // 로그 자동 스크롤
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MultiViewModel vm)
            {
                vm.LogEntries.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add && MultiLogListBox.Items.Count > 0)
                        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                        {
                            if (MultiLogListBox.Items.Count > 0)
                                MultiLogListBox.ScrollIntoView(MultiLogListBox.Items[^1]);
                        });
                };
            }
        };
    }

    private void TestModeToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ViewModels.MultiViewModel vm)
            vm.IsTestMode = !vm.IsTestMode;
        e.Handled = true;
    }
}
