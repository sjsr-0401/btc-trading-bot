using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BtcTradingBot.Models;
using BtcTradingBot.Services;

namespace BtcTradingBot.ViewModels;

/// <summary>멀티코인 USDM 모드 전용 ViewModel</summary>
public partial class MultiViewModel : ObservableObject
{
    private MultiTradingEngine? _engine;

    // === 상태 ===
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "준비됨";
    [ObservableProperty] private string _engineState = "준비됨";
    [ObservableProperty] private string _balance = "--";
    [ObservableProperty] private double _totalPnl;
    [ObservableProperty] private string _winRate = "0%";
    [ObservableProperty] private string _dailyInfo = "--";
    [ObservableProperty] private string _maxDrawdown = "0.00%";
    [ObservableProperty] private string _profitFactor = "--";

    // === 차트 VM (재사용) ===
    public ChartViewModel ChartVm { get; } = new();

    // === 포지션 대시보드 ===
    public ObservableCollection<PositionCardVm> PositionCards { get; } = new();
    [ObservableProperty] private string _selectedChartSymbol = "";

    // === 선택된 종목 리스트 ===
    public ObservableCollection<SelectedSymbolVm> SelectedSymbols { get; } = new();

    // === 로그 ===
    public ObservableCollection<string> LogEntries { get; } = new();

    // === API 키 (config에서 로드) ===
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _apiSecret = "";

    // === 설정 ===
    [ObservableProperty] private bool _isTestMode = true;
    [ObservableProperty] private double _testBalance = 100;
    [ObservableProperty] private int _leverage = 3;

    public bool HasApiKeys => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    partial void OnIsTestModeChanged(bool value) => _ = UpdateBalanceDisplayAsync();
    partial void OnTestBalanceChanged(double value) => _ = UpdateBalanceDisplayAsync();

    private async Task UpdateBalanceDisplayAsync()
    {
        if (IsRunning) return;

        if (IsTestMode)
        {
            Balance = $"${TestBalance:N2}";
            return;
        }

        // 실제 잔고 조회
        if (!HasApiKeys)
        {
            Balance = "API 키 필요";
            return;
        }

        try
        {
            using var api = new BinanceApi(ApiKey, ApiSecret);
            await api.SyncServerTime();
            var bal = await api.GetBalance();
            Balance = $"${bal:N2}";
            LogEntries.Add($"[INFO] USDM 잔고: {bal:N2} USDT");
        }
        catch (Exception ex)
        {
            Balance = "조회 실패";
            LogEntries.Add($"[ERROR] 잔고 조회 실패: {ex.Message}");
        }
    }

    public void LoadFromConfig(BotConfig config)
    {
        ApiKey = config.ApiKey;
        ApiSecret = config.ApiSecret;
    }

    public async Task InitializeAsync()
    {
        await UpdateBalanceDisplayAsync();
        await ChartVm.InitializeAsync("", "", "BTCUSDT");
    }

    [RelayCommand]
    private async Task ToggleBot()
    {
        if (IsRunning)
        {
            _engine?.Stop();
            _engine?.Dispose();
            _engine = null;
            IsRunning = false;
            StatusText = "중지됨";
            EngineState = "중지됨";
            return;
        }

        if (!IsTestMode && !HasApiKeys)
        {
            LogEntries.Add("[ERROR] 실전 모드에는 API 키가 필요합니다. config.json에 설정하세요.");
            return;
        }

        _engine = new MultiTradingEngine
        {
            IsTestMode = IsTestMode,
            InitialBalance = TestBalance,
            Leverage = Leverage,
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
        };

        WireEvents();
        IsRunning = true;
        StatusText = "실행중...";
        EngineState = "시작됨";

        _ = Task.Run(async () =>
        {
            try { await _engine.StartAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    LogEntries.Add($"[FATAL] {ex.Message}");
                    IsRunning = false;
                    StatusText = "오류로 중지됨";
                });
            }
        });
    }

    private void WireEvents()
    {
        if (_engine == null) return;

        _engine.OnLog += text => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LogEntries.Add(text);
            if (LogEntries.Count > 500) LogEntries.RemoveAt(0);
        });

        _engine.OnEngineState += state => Application.Current.Dispatcher.BeginInvoke(() =>
            EngineState = state);

        _engine.OnStatsChanged += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_engine == null) return;
            TotalPnl = _engine.TotalPnl;
            double wr = _engine.TotalTrades > 0 ? (double)_engine.TotalWins / _engine.TotalTrades * 100 : 0;
            WinRate = $"{wr:F0}% ({_engine.TotalTrades}t)";
            Balance = $"${_engine.GetEquityRaw():N2}";
            DailyInfo = $"{_engine.GetDailyPnl():+0.00;-0.00}";

            if (_engine.TotalGrossLoss > 0)
                ProfitFactor = $"{_engine.TotalGrossProfit / _engine.TotalGrossLoss:F2}";
            else if (_engine.TotalGrossProfit > 0)
                ProfitFactor = "∞";
            else
                ProfitFactor = "--";

            double peak = _engine.GetPeakEquity();
            double equity = _engine.GetEquityRaw();
            double mdd = peak > 0 ? (peak - equity) / peak * 100 : 0;
            MaxDrawdown = $"{mdd:F2}%";
        });

        _engine.OnPositionUpdate += (symbol, pos, pnl) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            UpdatePositionCard(symbol, pos, pnl);
        });

        _engine.OnSymbolsRefreshed += symbols => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            SelectedSymbols.Clear();
            foreach (var s in symbols)
                SelectedSymbols.Add(new SelectedSymbolVm(
                    s.Symbol.BaseAsset, s.Symbol.Symbol, s.Score, s.AtrPercent));
        });
    }

    private void UpdatePositionCard(string symbol, PositionState pos, double unrealizedPnl)
    {
        var card = PositionCards.FirstOrDefault(c => c.Symbol == symbol);

        if (!pos.IsOpen)
        {
            if (card != null) PositionCards.Remove(card);
            return;
        }

        if (card == null)
        {
            card = new PositionCardVm { Symbol = symbol, BaseAsset = symbol.Replace("USDT", "") };
            PositionCards.Add(card);
        }

        card.Side = pos.Side == "L" ? "LONG" : "SHORT";
        card.SideColor = pos.Side == "L" ? "#22C55E" : "#EF4444";
        card.EntryPrice = pos.EntryPrice;
        card.PnlText = $"{(unrealizedPnl >= 0 ? "+" : "")}{unrealizedPnl:F2}";
        card.PnlColor = unrealizedPnl >= 0 ? "#22C55E" : "#EF4444";
        card.Strategy = pos.StrategyTag;
        string pf = PriceFmt(pos.EntryPrice);
        double notional = pos.Amount * pos.EntryPrice;
        card.EntryText = $"{pos.EntryPrice.ToString(pf)}  (${notional:N1})";
        double currentPrice = pos.Amount > 0
            ? pos.EntryPrice + (pos.Side == "L" ? unrealizedPnl / pos.Amount : -unrealizedPnl / pos.Amount)
            : 0;
        card.CurrentText = currentPrice > 0 ? currentPrice.ToString(pf) : "--";
        card.SlText = pos.StopPrice > 0 ? pos.StopPrice.ToString(pf) : "--";
        card.TpText = pos.TpPrice > 0 ? pos.TpPrice.ToString(pf) : "--";
        card.LeverageText = $"{(_engine?.Leverage ?? Leverage)}x";
    }

    [RelayCommand]
    private async Task SelectChart(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return;
        SelectedChartSymbol = symbol;
        try { await ChartVm.ChangeSymbol(symbol); }
        catch (Exception ex) { LogEntries.Add($"[WARN] 차트 변경 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ClosePosition(string symbol)
    {
        if (_engine == null || string.IsNullOrEmpty(symbol)) return;
        try
        {
            await _engine.ManualClosePosition(symbol);
        }
        catch (Exception ex)
        {
            LogEntries.Add($"[ERROR] 수동 청산 실패: {ex.Message}");
        }
    }

    /// <summary>가격 크기에 맞는 소수점 포맷 (SPACE=0.01 → 6자리, BTC=95000 → 2자리)</summary>
    private static string PriceFmt(double price)
    {
        if (price <= 0) return "N2";
        if (price >= 100) return "N2";
        if (price >= 1) return "N4";
        if (price >= 0.01) return "N6";
        return "N8";
    }

    public void StopEngine()
    {
        _engine?.Stop();
        _engine?.Dispose();
        ChartVm.Dispose();
        IsRunning = false;
    }
}

/// <summary>포지션 카드 ViewModel (대시보드용)</summary>
public partial class PositionCardVm : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _baseAsset = "";
    [ObservableProperty] private string _side = "N";
    [ObservableProperty] private string _sideColor = "#888888";
    [ObservableProperty] private double _entryPrice;
    [ObservableProperty] private string _pnlText = "--";
    [ObservableProperty] private string _pnlColor = "#888888";
    [ObservableProperty] private string _strategy = "";
    [ObservableProperty] private string _entryText = "--";     // 진입가 ($금액)
    [ObservableProperty] private string _currentText = "--";   // 현재 시장가
    [ObservableProperty] private string _slText = "--";        // SL 가격
    [ObservableProperty] private string _tpText = "--";        // TP 가격
    [ObservableProperty] private string _leverageText = "--";  // 레버리지
}

/// <summary>선택된 종목 표시용</summary>
public record SelectedSymbolVm(string BaseAsset, string Symbol, double Score, double AtrPct);
