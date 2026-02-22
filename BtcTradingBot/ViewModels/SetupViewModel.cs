using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BtcTradingBot.Models;
using BtcTradingBot.Services;

namespace BtcTradingBot.ViewModels;

public partial class SetupViewModel : ObservableObject
{
    [ObservableProperty] private int _currentStep;  // 0=welcome, 1=guide, 2=input, 3=success
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _apiSecret = "";
    [ObservableProperty] private string _testStatus = "";
    [ObservableProperty] private string _testStatusColor = "#9CA3AF";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _balanceText = "";

    public event Action? OnSetupComplete;

    [RelayCommand]
    private void NextStep() => CurrentStep = Math.Min(CurrentStep + 1, 3);

    [RelayCommand]
    private void PrevStep() => CurrentStep = Math.Max(CurrentStep - 1, 0);

    [RelayCommand]
    private void GoToInput() => CurrentStep = 2;

    [RelayCommand]
    private async Task TestConnection()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret))
        {
            TestStatus = "API Key와 Secret을 입력하세요.";
            TestStatusColor = "#EF4444";
            return;
        }

        IsTesting = true;
        TestStatus = "연결 테스트 중...";
        TestStatusColor = "#9CA3AF";

        try
        {
            using var api = new BinanceApi(ApiKey.Trim(), ApiSecret.Trim());
            await api.SyncServerTime();
            var balance = await api.GetBalance();

            TestStatus = "연결 성공!";
            TestStatusColor = "#22C55E";
            if (balance < 0.01)
                BalanceText = "선물 지갑 잔고: 0 USDT\n(Spot 지갑에서 Futures 지갑으로 이체하세요: Wallet → Transfer)";
            else
                BalanceText = $"선물 지갑 잔고: {balance:N2} USDT";

            // 설정 저장
            var config = ConfigService.Exists() ? ConfigService.Load() : new BotConfig();
            config.ApiKey = ApiKey.Trim();
            config.ApiSecret = ApiSecret.Trim();
            ConfigService.Save(config);

            CurrentStep = 3;
        }
        catch (Exception ex)
        {
            TestStatus = $"연결 실패: {ex.Message}";
            TestStatusColor = "#EF4444";
            BalanceText = "";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void Complete()
    {
        OnSetupComplete?.Invoke();
    }

    [RelayCommand]
    private void StartTestMode()
    {
        var config = ConfigService.Exists() ? ConfigService.Load() : new BotConfig();
        config.IsTestMode = true;
        ConfigService.Save(config);
        OnSetupComplete?.Invoke();
    }

    [RelayCommand]
    private void OpenBinanceSignup()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.binance.com/en/register") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private void OpenApiManagement()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.binance.com/en/my/settings/api-management") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
