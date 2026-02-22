using System.Threading;
using System.Windows;
using BtcTradingBot.Services;
using BtcTradingBot.Views;

namespace BtcTradingBot;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Global\\BtcTradingBot_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("이미 실행 중입니다.", "BTC Trading Bot", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // config.json이 없거나 (테스트모드 아닌데 API 키 비어있으면) 온보딩 표시
        var config = ConfigService.Load();
        if (!ConfigService.Exists() ||
            (!config.IsTestMode && string.IsNullOrWhiteSpace(config.ApiKey)))
        {
            var setup = new SetupWindow();
            var result = setup.ShowDialog();
            if (result != true)
            {
                Shutdown();
                return;
            }
            // 온보딩 완료 후 config 다시 로드
            config = ConfigService.Load();
        }

        var mainWindow = new MainWindow();
        mainWindow.ApplyConfig(config);
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
