using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BtcTradingBot.Models;
using BtcTradingBot.Services;
using BtcTradingBot.Views;

namespace BtcTradingBot.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private TradingEngine? _engine;

    // === 코인 선택 ===
    [ObservableProperty] private string _selectedSymbol = "BTCUSDT";
    [ObservableProperty] private string _selectedSymbolDisplay = "BTC / USDT";
    [ObservableProperty] private bool _isLoadingSymbols;
    public ObservableCollection<SymbolInfo> AvailableSymbols { get; } = new();

    // === 설정 ===
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _apiSecret = "";
    [ObservableProperty] private int _leverage = 4;
    [ObservableProperty] private double _tradeUsdt = 35;
    [ObservableProperty] private int _checkInterval = 60;
    [ObservableProperty] private int _maxDailyTrades = 4;
    [ObservableProperty] private double _maxDailyLossPct = 3.0;
    [ObservableProperty] private int _maxConsecLosses = 3;
    [ObservableProperty] private int _cooldownMinutes = 75;
    [ObservableProperty] private string _discordWebhook = "";
    [ObservableProperty] private int _scanIntervalSec = 60;
    [ObservableProperty] private int _scanCoinCount = 10;
    [ObservableProperty] private bool _autoSwitchEnabled;
    [ObservableProperty] private int _autoEntryScore = 50;
    [ObservableProperty] private int _directEntryScore = 60;
    private DateTime _lastAutoSwitchTime = DateTime.MinValue;
    private bool _isAutoSwitching;
    private bool _pendingShortOnly;   // 자동전환 시 ShortOnly 여부 전달용
    private string _engineSymbol = ""; // 엔진이 실제 거래 중인 심볼

    // 차트 라인 동기화용 추적값 (재시작·손익분기 변경 감지)
    private double _lastChartEntry, _lastChartSl, _lastChartTp;

    // === 모드 / 엔진 선택 ===
    [ObservableProperty] private string _currentMode = "Classic";
    [ObservableProperty] private string _selectedEngine = "KYJ";

    public bool IsClassicMode => CurrentMode == "Classic";
    public bool IsMultiMode => CurrentMode == "Multi";

    partial void OnCurrentModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsClassicMode));
        OnPropertyChanged(nameof(IsMultiMode));
    }

    // === 테스트 모드 ===
    [ObservableProperty] private bool _isTestMode;
    [ObservableProperty] private double _testBalance = 100;

    // === 상태 ===
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _positionText = "대기중";
    [ObservableProperty] private string _positionColor = "#888888";
    [ObservableProperty] private double _totalPnl;
    [ObservableProperty] private string _balance = "--";
    [ObservableProperty] private string _winRate = "0%";
    [ObservableProperty] private string _dailyInfo = "0t / 0.00";
    [ObservableProperty] private string _statusText = "준비됨";
    private double _cachedBalance;

    // === 엔진 분석 상태 ===
    [ObservableProperty] private string _engineState = "준비됨";
    [ObservableProperty] private string _lastDirection = "--";
    [ObservableProperty] private string _lastDirectionColor = "#888888";
    [ObservableProperty] private int _lastScore;
    [ObservableProperty] private string _analysisRsi = "--";
    [ObservableProperty] private string _analysisMacd = "--";
    [ObservableProperty] private string _analysisAdx = "--";
    [ObservableProperty] private string _analysisVolume = "--";
    [ObservableProperty] private string _analysisTrend1H = "--";
    [ObservableProperty] private string _analysisTrend4H = "--";
    [ObservableProperty] private string _analysisCandle = "--";
    [ObservableProperty] private bool _hasAnalysis;
    [ObservableProperty] private int _crossCount;
    [ObservableProperty] private string _crossInfo = "크로스 대기";

    // === 미실현 PnL ===
    [ObservableProperty] private string _unrealizedPnlText = "";
    [ObservableProperty] private string _unrealizedPnlColor = "#888888";

    // === SL/TP + 보유시간 표시 ===
    [ObservableProperty] private string _slTpText = "";
    [ObservableProperty] private string _holdDuration = "";

    // === MDD / 수익 팩터 / 평균 수익·손실 ===
    [ObservableProperty] private string _maxDrawdown = "0.00%";
    [ObservableProperty] private string _profitFactor = "--";
    [ObservableProperty] private string _avgWinLoss = "--";

    // === 페이지 네비게이션 ===
    [ObservableProperty] private bool _isScannerPage = true;
    [ObservableProperty] private bool _isTradingPage;
    public ScannerViewModel ScannerVm { get; } = new();

    // === 차트 VM ===
    public ChartViewModel ChartVm { get; } = new();

    /// <summary>포지션 보유 중인지 여부</summary>
    public bool HasOpenPosition => PositionText != "대기중";
    public bool CanEditSettings => !HasOpenPosition;

    public ObservableCollection<string> LogEntries { get; } = new();
    public ObservableCollection<TradeRecord> TradeRecords { get; } = new();

    partial void OnSelectedEngineChanged(string value)
    {
        // KYJ만 지원 (향후 멀티코인 모드 추가 예정)
    }

    partial void OnIsTestModeChanged(bool value)
    {
        ThemeService.ApplyTheme(value);
        if (value)
        {
            SetBalanceDisplay(TestBalance);
            StatusText = "테스트 모드";
        }
        else
        {
            _cachedBalance = 0;
            Balance = "--";
            StatusText = "준비됨";
        }
    }

    partial void OnPositionTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasOpenPosition));
        OnPropertyChanged(nameof(CanEditSettings));
    }

    partial void OnScanIntervalSecChanged(int value) =>
        ScannerVm.UpdateSettings(value, ScanCoinCount);

    partial void OnScanCoinCountChanged(int value) =>
        ScannerVm.UpdateSettings(ScanIntervalSec, value);

    partial void OnAutoEntryScoreChanged(int value) =>
        ScannerVm.AutoEntryScore = Math.Clamp(value, 45, 100);

    partial void OnSelectedSymbolChanged(string value)
    {
        // 엔진 실행 중이면 CurrentSymbol은 엔진 심볼 유지 (차트 전환으로 변경하지 않음)
        if (!IsRunning)
            ScannerVm.CurrentSymbol = value;

        // 표시 이름 갱신
        var info = AvailableSymbols.FirstOrDefault(s => s.Symbol == value);
        if (info != null)
            SelectedSymbolDisplay = info.DisplayName;
        else
        {
            // 스캐너 결과에서 찾기 (상위 10개 중 AvailableSymbols에 없는 코인)
            var scanInfo = ScannerVm.ScanResults.FirstOrDefault(r => r.Symbol.Symbol == value)?.Symbol;
            if (scanInfo != null)
            {
                AvailableSymbols.Add(scanInfo);
                SelectedSymbolDisplay = scanInfo.DisplayName;
                info = scanInfo;
            }
        }

        // 차트는 항상 변경 허용 (엔진은 _cfg.Symbol로 독립 동작)
        // info가 null이어도 기본 정밀도로 차트 로드 시도 (방어적 처리)
        _ = SafeChangeChart(value, info?.PricePrecision ?? 2);
    }

    private async Task SafeChangeChart(string symbol, int pricePrecision)
    {
        try
        {
            await ChartVm.ChangeSymbol(symbol, pricePrecision);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
                LogEntries.Add($"[WARN] 차트 로드 실패 ({symbol}): {ex.Message}"));
        }
    }

    public async Task LoadTopSymbolsAsync()
    {
        if (IsLoadingSymbols) return;
        IsLoadingSymbols = true;
        try
        {
            using var api = new BinanceApi("", "");
            // 스캐너와 동일한 개수 로드 → 클릭 가능한 모든 코인이 AvailableSymbols에 포함되도록
            var symbols = await api.GetTopSymbols(Math.Clamp(ScanCoinCount, 5, 20));
            AvailableSymbols.Clear();
            foreach (var s in symbols) AvailableSymbols.Add(s);

            // 현재 선택된 심볼이 목록에 없으면 첫 번째로 설정
            if (!symbols.Any(s => s.Symbol == SelectedSymbol) && symbols.Count > 0)
                SelectedSymbol = symbols[0].Symbol;
        }
        catch (Exception ex)
        {
            LogEntries.Add($"[WARN] 코인 목록 로드 실패: {ex.Message}");
            // 기본값 유지
            if (AvailableSymbols.Count == 0)
                AvailableSymbols.Add(new SymbolInfo("BTCUSDT", "BTC", 0, 0, 2, 3, 5));
        }
        finally { IsLoadingSymbols = false; }
    }

    /// <summary>config.json에서 설정 로드</summary>
    public void LoadFromConfig(BotConfig config)
    {
        ApiKey = config.ApiKey;
        ApiSecret = config.ApiSecret;
        SelectedSymbol = config.Symbol;
        Leverage = config.Leverage;
        TradeUsdt = config.TradeUsdt;
        CheckInterval = config.CheckIntervalSeconds;
        MaxDailyTrades = config.MaxDailyTrades;
        MaxDailyLossPct = config.MaxDailyLossPct;
        MaxConsecLosses = config.MaxConsecLosses;
        CooldownMinutes = config.CooldownMinutes;
        DiscordWebhook = config.DiscordWebhook;
        SelectedEngine = config.SelectedEngine;
        TestBalance = config.TestBalance;
        IsTestMode = config.IsTestMode;
        ScanIntervalSec = config.ScanIntervalSec;
        ScanCoinCount = config.ScanCoinCount;
        AutoSwitchEnabled = config.AutoSwitchEnabled;
        AutoEntryScore = config.AutoEntryScore;
        DirectEntryScore = config.DirectEntryScore;
    }

    /// <summary>차트 + 코인 목록 초기화 (API 키 없어도 공개 API로 로드)</summary>
    public async Task InitializeChartAsync()
    {
        await LoadTopSymbolsAsync();

        // 심볼 목록 로드 후 display 갱신 (LoadFromConfig 시점에는 목록이 비어있었으므로)
        var info = AvailableSymbols.FirstOrDefault(s => s.Symbol == SelectedSymbol);
        if (info != null)
            SelectedSymbolDisplay = info.DisplayName;

        int pricePrecision = info?.PricePrecision ?? 2;
        await ChartVm.InitializeAsync(ApiKey ?? "", ApiSecret ?? "", SelectedSymbol, pricePrecision);

        // 스캐너 초기화
        ScannerVm.CurrentSymbol = SelectedSymbol;
        ScannerVm.AutoEntryScore = AutoEntryScore;
        ScannerVm.OnCoinSelected += symbol =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                SelectedSymbol = symbol;
                NavigateToTrading();
            });
        };
        ScannerVm.OnBetterCoinFound += (symbol, score, shortOnly) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() => TryAutoSwitch(symbol, score, shortOnly));
        };

        try
        {
            // LoadTopSymbolsAsync에서 이미 로드한 목록 재사용 (중복 API 호출 제거)
            ScannerVm.UpdateSettings(ScanIntervalSec, ScanCoinCount);
            ScannerVm.SetSymbols(AvailableSymbols.ToList());
            _ = ScannerVm.InitialScanAsync();
        }
        catch (Exception ex)
        {
            LogEntries.Add($"[WARN] 스캐너 초기화 실패: {ex.Message}");
        }

        // 실전 모드 + 클래식: 기존 포지션 감지 → 자동 봇 시작
        if (!IsTestMode && IsClassicMode && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret))
        {
            try
            {
                using var posApi = new BinanceApi(ApiKey, ApiSecret);
                await posApi.SyncServerTime();
                var allPos = await posApi.GetAllPositions();
                if (allPos.Count > 0)
                {
                    var pos = allPos[0];
                    string side = pos.Type == "L" ? "LONG" : "SHORT";
                    var baseAsset = AvailableSymbols.FirstOrDefault(s => s.Symbol == pos.Symbol)?.BaseAsset ?? pos.Symbol.Replace("USDT", "");

                    // 레버리지 동기화
                    if (pos.Leverage > 0) Leverage = pos.Leverage;

                    // 코인 전환
                    if (pos.Symbol != SelectedSymbol)
                    {
                        SelectedSymbol = pos.Symbol;
                        var symInfo = AvailableSymbols.FirstOrDefault(s => s.Symbol == pos.Symbol);
                        if (symInfo != null) SelectedSymbolDisplay = $"{symInfo.BaseAsset} / USDT";
                    }

                    LogEntries.Add($"[포지션 감지] {baseAsset} {side} {pos.Amount} @ {PriceHelper.Format(pos.EntryPrice)} (x{pos.Leverage}) → 봇 자동 시작");

                    // 즉시 포지션 상태 설정 → 스캐너 자동전환 차단
                    PositionText = $"{baseAsset} {side} (감지됨)";
                    PositionColor = "#FFA726";
                    ScannerVm.HasOpenPosition = true;

                    // 봇 자동 시작
                    _isAutoSwitching = true;
                    NavigateToTrading();
                    await ToggleBotCommand.ExecuteAsync(null);
                    _isAutoSwitching = false;
                }
            }
            catch (Exception ex)
            {
                LogEntries.Add($"[WARN] 포지션 조회 실패: {ex.Message}");
            }
        }
    }

    private void SetBalanceDisplay(double bal)
    {
        _cachedBalance = bal;
        Balance = FormatBalance(bal);
    }

    private static string FormatBalance(double bal) =>
        bal switch
        {
            >= 1000 => $"${bal:N2}",
            >= 1 => $"${bal:F2}",
            > 0 => $"${bal:F4}",
            _ => "$0.00",
        };

    [RelayCommand]
    private void ToggleTestMode()
    {
        if (IsRunning)
        {
            ThemedDialog.Alert("알림", "봇 실행 중에는 모드를 변경할 수 없습니다.");
            return;
        }
        IsTestMode = !IsTestMode;
    }

    [RelayCommand]
    private void OpenTestSettings()
    {
        var dialog = new TestDepositDialog(TestBalance) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            TestBalance = dialog.ResultBalance;
            if (IsTestMode) SetBalanceDisplay(TestBalance);
        }
    }

    /// <summary>잔고 조회</summary>
    [RelayCommand]
    private async Task RefreshBalance()
    {
        if (IsTestMode)
        {
            SetBalanceDisplay(TestBalance);
            LogEntries.Add($"[TEST] 가상 잔고: {TestBalance} USDT");
            return;
        }
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret)) return;
        try
        {
            using var api = new BinanceApi(ApiKey, ApiSecret);
            await api.SyncServerTime();
            var bal = await api.GetBalance();
            SetBalanceDisplay(bal);
            LogEntries.Add($"[INFO] 잔고: {bal} USDT");
        }
        catch (Exception ex)
        {
            LogEntries.Add($"[ERROR] 잔고 조회 실패: {ex.Message}");
        }
    }

    /// <summary>엔진 즉시 정지 (앱 종료 시 사용)</summary>
    public void StopEngine()
    {
        _engine?.Stop();
        _engine?.Dispose();
        ScannerVm.Dispose();
        IsRunning = false;
    }

    /// <summary>포지션 청산 (외부 호출용 — 종료 전 청산)</summary>
    public async Task ClosePositionAsync()
    {
        if (_engine == null) return;
        try { await _engine.ManualClose(); }
        catch (Exception ex) { LogEntries.Add($"ERR: 청산 실패 — {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ToggleBot()
    {
        if (IsRunning)
        {
            if (HasOpenPosition)
            {
                if (!ThemedDialog.Confirm("봇 중지",
                    "포지션이 열려 있습니다.\n포지션을 청산하고 중지할까요?",
                    "청산 후 중지", "취소"))
                    return;

                try { await _engine!.ManualClose(); }
                catch (Exception ex) { LogEntries.Add($"ERR: 청산 실패 — {ex.Message}"); }
            }

            _engine?.Stop();
            IsRunning = false;
            ScannerVm.IsBotRunning = false;
            _engineSymbol = "";
            StatusText = "중지됨";
            EngineState = "중지됨";
            return;
        }

        if (!IsTestMode && (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret)))
        {
            ThemedDialog.Alert("입력 오류", "API Key와 Secret을 입력하세요.");
            return;
        }

        // 설정값 검증
        if (Leverage < 1 || Leverage > 125)
        {
            ThemedDialog.Alert("설정 오류", "레버리지는 1~125 사이여야 합니다.", AlertType.Error);
            return;
        }
        if (TradeUsdt < 5)
        {
            ThemedDialog.Alert("설정 오류", "거래 금액은 최소 5 USDT 이상이어야 합니다.", AlertType.Error);
            return;
        }
        if (CheckInterval < 10)
        {
            ThemedDialog.Alert("설정 오류", "체크 간격은 최소 10초 이상이어야 합니다.", AlertType.Error);
            return;
        }

        // 실전 모드: 잔고 검증
        if (!IsTestMode)
        {
            try
            {
                using var checkApi = new BinanceApi(ApiKey, ApiSecret);
                await checkApi.SyncServerTime();
                var bal = await checkApi.GetBalance();
                double minRequired = TradeUsdt / Leverage;
                if (bal < minRequired)
                {
                    ThemedDialog.Alert("잔고 부족",
                        $"현재 잔고: {bal:F2} USDT\n" +
                        $"최소 필요: {minRequired:F2} USDT\n" +
                        $"(거래금액 {TradeUsdt} ÷ 레버리지 {Leverage}x)\n\n" +
                        $"잔고를 충전하거나 거래금액을 줄이세요.",
                        AlertType.Error);
                    return;
                }
                SetBalanceDisplay(bal);

                // 고위험 경고: 거래금액이 잔고의 50% 초과 시
                if (!_isAutoSwitching && TradeUsdt > bal * 0.5)
                {
                    if (!ThemedDialog.Confirm("고위험 경고",
                        $"거래금액({TradeUsdt:F0} USDT)이 잔고({bal:F0} USDT)의 {TradeUsdt / bal * 100:F0}%입니다.\n" +
                        $"한 번의 손실로 큰 피해가 발생할 수 있습니다.\n계속하시겠습니까?"))
                        return;
                }

                // 전체 코인 포지션 확인 (실전 모드)
                var allPositions = await checkApi.GetAllPositions();
                if (allPositions.Count > 0)
                {
                    var pos = allPositions[0]; // 첫 번째 열린 포지션
                    string side = pos.Type == "L" ? "LONG" : "SHORT";
                    var baseAsset = AvailableSymbols.FirstOrDefault(s => s.Symbol == pos.Symbol)?.BaseAsset ?? pos.Symbol;

                    if (_isAutoSwitching && pos.Symbol != SelectedSymbol)
                    {
                        // 자동 전환: 다른 코인에 기존 포지션 있으면 전환 취소
                        LogEntries.Add($"[SCAN] 자동 전환 취소: {baseAsset}에 기존 {side} 포지션 있음");
                        _isAutoSwitching = false;
                        return;
                    }

                    // 레버리지 동기화
                    if (pos.Leverage > 0) Leverage = pos.Leverage;

                    // 포지션이 있는 코인이 현재 선택과 다르면 자동 전환
                    if (pos.Symbol != SelectedSymbol)
                    {
                        LogEntries.Add($"[포지션 감지] {baseAsset} {side} {pos.Amount} @ {PriceHelper.Format(pos.EntryPrice)} (x{pos.Leverage}) → 해당 코인으로 전환");
                        SelectedSymbol = pos.Symbol;
                        var symInfo = AvailableSymbols.FirstOrDefault(s => s.Symbol == pos.Symbol);
                        if (symInfo != null)
                            SelectedSymbolDisplay = $"{symInfo.BaseAsset} / USDT";
                    }

                    if (!_isAutoSwitching)
                    {
                        ThemedDialog.Alert("기존 포지션 발견",
                            $"{side} {pos.Amount} {baseAsset} @ {PriceHelper.Format(pos.EntryPrice)}\n" +
                            $"봇이 이 포지션을 자동 모니터링합니다.\n" +
                            $"(SL/TP 자동 설정)",
                            AlertType.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isAutoSwitching)
                {
                    LogEntries.Add($"[SCAN] 자동 전환 실패: {ex.Message}");
                    _isAutoSwitching = false;
                    return;
                }
                ThemedDialog.Alert("API 오류", $"잔고 확인 실패: {ex.Message}", AlertType.Error);
                return;
            }
        }

        // 테스트 모드: 이전 상태 복원 확인 (자동 전환 시 스킵)
        PaperState? savedState = null;
        if (IsTestMode && !_isAutoSwitching)
        {
            var ps = ConfigService.LoadPaperState();
            if (ps != null && ps.Balance > 0 && ps.Symbol == SelectedSymbol)
            {
                string posInfo = ps.PositionType != "N"
                    ? $"\n포지션: {(ps.PositionType == "L" ? "LONG" : "SHORT")} @ {PriceHelper.Format(ps.EntryPrice)}"
                    : "";
                if (ThemedDialog.Confirm("이전 테스트 복원",
                    $"잔고: {ps.Balance:N2} USDT{posInfo}\n" +
                    $"거래: {ps.TotalTrades}건 (PnL: {ps.TotalPnl:+0.00;-0.00})\n\n" +
                    "이전 상태를 복원하시겠습니까?",
                    "복원", "새로 시작"))
                    savedState = ps;
                else
                    ConfigService.DeletePaperState();
            }
        }

        var config = new BotConfig
        {
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            Symbol = SelectedSymbol,
            Leverage = Leverage,
            TradeUsdt = TradeUsdt,
            CheckIntervalSeconds = CheckInterval,
            MaxDailyTrades = MaxDailyTrades,
            MaxDailyLossPct = MaxDailyLossPct,
            MaxConsecLosses = MaxConsecLosses,
            CooldownMinutes = CooldownMinutes,
            DiscordWebhook = DiscordWebhook,
            SelectedEngine = SelectedEngine,
            IsTestMode = IsTestMode,
            TestBalance = TestBalance,
            AutoEntryScore = AutoEntryScore,
            DirectEntryScore = DirectEntryScore,
            ShortOnly = _isAutoSwitching && _pendingShortOnly,
        };

        _engine = new TradingEngine(config);
        _engineSymbol = SelectedSymbol;
        ScannerVm.CurrentSymbol = SelectedSymbol;
        if (savedState != null)
            _engine.RestorePaperState(savedState);
        _engine.OnLog += text => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LogEntries.Add(text);
            if (LogEntries.Count > 500) LogEntries.RemoveAt(0);
        });
        _engine.OnPositionUpdate += (pos, price, unrealizedPnl) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (pos.Type == "N")
            {
                PositionText = "대기중";
                PositionColor = "#888888";
                UnrealizedPnlText = "";
                UnrealizedPnlColor = "#888888";
                SlTpText = "";
                HoldDuration = "";
                ScannerVm.HasOpenPosition = false;
                _lastChartEntry = 0; _lastChartSl = 0; _lastChartTp = 0;
                ChartVm.UpdatePositionLines(0, 0, 0); // 포지션 종료 시 차트 라인 즉시 제거
            }
            else
            {
                // 차트 라인 동기화: 재시작·손익분기 SL 변경 시 자동 반영
                if (_engine != null && SelectedSymbol == _engineSymbol)
                {
                    double cEntry = pos.EntryPrice;
                    double cSl = _engine.SlPrice;
                    double cTp = _engine.TpPrice;
                    if (cEntry != _lastChartEntry || cSl != _lastChartSl || cTp != _lastChartTp)
                    {
                        _lastChartEntry = cEntry; _lastChartSl = cSl; _lastChartTp = cTp;
                        ChartVm.UpdatePositionLines(cEntry, cSl, cTp);
                    }
                }
                double pct = pos.EntryPrice > 0
                    ? (pos.Type == "L"
                        ? (price - pos.EntryPrice) / pos.EntryPrice * 100
                        : (pos.EntryPrice - price) / pos.EntryPrice * 100)
                    : 0;
                var coinName = _engineSymbol.Replace("USDT", "");
                PositionText = $"{coinName} {(pos.Type == "L" ? "LONG" : "SHORT")} {pct:+0.00;-0.00}%";
                PositionColor = pct >= 0 ? "#00C853" : "#FF1744";
                ScannerVm.HasOpenPosition = true;

                // 미실현 PnL 표시
                UnrealizedPnlText = $"{(unrealizedPnl >= 0 ? "+" : "")}{unrealizedPnl:F2} USDT";
                UnrealizedPnlColor = unrealizedPnl >= 0 ? "#00C853" : "#FF1744";

                // SL/TP 표시 (% 형식)
                if (_engine != null && _engine.SlPrice > 0 && _engine.TpPrice > 0 && pos.EntryPrice > 0)
                {
                    double slPct = Math.Abs((_engine.SlPrice - pos.EntryPrice) / pos.EntryPrice * 100);
                    double tpPct = Math.Abs((_engine.TpPrice - pos.EntryPrice) / pos.EntryPrice * 100);
                    SlTpText = $"SL -{slPct:F2}% / TP +{tpPct:F2}%";
                }
                else
                    SlTpText = "";

                // 보유 시간 표시
                if (_engine?.PositionOpenTime != null)
                {
                    var elapsed = DateTime.Now - _engine.PositionOpenTime.Value;
                    HoldDuration = elapsed.TotalHours >= 1
                        ? elapsed.ToString(@"h\:mm\:ss")
                        : elapsed.ToString(@"mm\:ss");
                }
                else HoldDuration = "";

                // 잔고에 미실현 PnL 반영
                if (_engine != null)
                    SetBalanceDisplay(_engine.CurrentBalance + unrealizedPnl);
            }
        });
        _engine.OnStatsChanged += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_engine == null) return;
            TotalPnl = _engine.TotalPnl;
            double wr = _engine.TotalTrades > 0 ? (double)_engine.TotalWins / _engine.TotalTrades * 100 : 0;
            string streak = _engine.ConsecWins > 0 ? $" {_engine.ConsecWins}W" : _engine.ConsecLosses > 0 ? $" {_engine.ConsecLosses}L" : "";
            WinRate = $"{wr:F0}% ({_engine.TotalTrades}t{streak})";
            SetBalanceDisplay(_engine.CurrentBalance);
            DailyInfo = $"{_engine.DailyTrades}t / {_engine.DailyPnl:+0.00;-0.00}";
            MaxDrawdown = $"{_engine.MaxDrawdownPct:F2}%";
            if (_engine.TotalGrossLoss > 0)
                ProfitFactor = $"{_engine.TotalGrossProfit / _engine.TotalGrossLoss:F2}";
            else if (_engine.TotalGrossProfit > 0)
                ProfitFactor = "∞";
            else
                ProfitFactor = "--";

            // 평균 수익/손실
            if (_engine.TotalTrades > 0)
            {
                double avgW = _engine.TotalWins > 0 ? _engine.TotalGrossProfit / _engine.TotalWins : 0;
                double avgL = _engine.TotalLosses > 0 ? _engine.TotalGrossLoss / _engine.TotalLosses : 0;
                AvgWinLoss = $"+{avgW:F2} / -{avgL:F2}";
            }
            else AvgWinLoss = "--";
        });
        _engine.OnTradeComplete += record => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            TradeRecords.Insert(0, record);
            if (TradeRecords.Count > 50) TradeRecords.RemoveAt(TradeRecords.Count - 1);
        });
        _engine.OnTradeMarker += marker => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // 현재 차트가 엔진 거래 코인과 같을 때만 마커 표시
            if (SelectedSymbol == _engineSymbol)
                ChartVm.AddTradeMarker(marker);
        });
        _engine.OnEngineState += state => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            EngineState = state;
        });
        _engine.OnAnalysisResult += sig => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            UpdateAnalysisDisplay(sig);
        });

        IsRunning = true;
        ScannerVm.IsBotRunning = true;
        StatusText = "실행중...";
        EngineState = "시작됨";

        _ = Task.Run(async () =>
        {
            try { await _engine.StartAsync(); }
            catch (OperationCanceledException) { /* 정상 종료 */ }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    LogEntries.Add($"[FATAL] {ex.Message}");
                    IsRunning = false;
                    ScannerVm.IsBotRunning = false;
                    _engineSymbol = "";
                    StatusText = "오류로 중지됨";
                });
            }
        });

        return;
    }

    [RelayCommand]
    private async Task ManualClose()
    {
        if (_engine == null) return;
        if (ThemedDialog.Confirm("포지션 청산", "현재 포지션을 청산하시겠습니까?"))
        {
            try { await _engine.ManualClose(); }
            catch (Exception ex) { LogEntries.Add($"ERR: 수동 청산 실패 — {ex.Message}"); }
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        var config = new BotConfig
        {
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            Symbol = SelectedSymbol,
            Leverage = Leverage,
            TradeUsdt = TradeUsdt,
            CheckIntervalSeconds = CheckInterval,
            MaxDailyTrades = MaxDailyTrades,
            MaxDailyLossPct = MaxDailyLossPct,
            MaxConsecLosses = MaxConsecLosses,
            CooldownMinutes = CooldownMinutes,
            DiscordWebhook = DiscordWebhook,
            SelectedEngine = SelectedEngine,
            IsTestMode = IsTestMode,
            TestBalance = TestBalance,
            ScanIntervalSec = ScanIntervalSec,
            ScanCoinCount = ScanCoinCount,
            AutoSwitchEnabled = AutoSwitchEnabled,
            AutoEntryScore = AutoEntryScore,
            DirectEntryScore = DirectEntryScore,
        };
        ConfigService.Save(config);
        StatusText = "설정 저장됨";
    }

    [RelayCommand]
    private void SetTradePercent(string percentStr)
    {
        double balance = IsTestMode ? TestBalance : _cachedBalance;
        if (balance <= 0) return;
        double percent = double.Parse(percentStr, System.Globalization.CultureInfo.InvariantCulture);
        TradeUsdt = Math.Round(balance * percent / 100.0, 2);
    }

    [RelayCommand]
    private void NavigateToScanner()
    {
        IsScannerPage = true;
        IsTradingPage = false;
    }

    [RelayCommand]
    private void NavigateToTrading()
    {
        IsScannerPage = false;
        IsTradingPage = true;
    }

    private void TryAutoSwitch(string newSymbol, int score, bool shortOnly = false)
    {
        if (!AutoSwitchEnabled) return;
        if (HasOpenPosition) return;
        if (newSymbol == _engineSymbol && IsRunning) return;

        // 5분 쿨다운: 핑퐁 방지
        if ((DateTime.Now - _lastAutoSwitchTime).TotalMinutes < 5) return;

        _lastAutoSwitchTime = DateTime.Now;
        _pendingShortOnly = shortOnly;

        var prevSymbol = IsRunning ? _engineSymbol : SelectedSymbol;
        string modeTag = shortOnly ? " [숏전용]" : "";
        LogEntries.Add($"[SCAN] 자동 전환: {prevSymbol} → {newSymbol} (준비도 {score}점{modeTag})");

        // 엔진 정지 + 리소스 정리 (자동전환: 포지션 청산 안 함)
        if (IsRunning)
        {
            _engine?.Stop(skipClose: true);
            _engine?.Dispose();
            _engine = null;
            IsRunning = false;
            ScannerVm.IsBotRunning = false;
        }

        // 심볼 변경 + 트레이딩 페이지로 전환 + 봇 시작
        _isAutoSwitching = true;
        SelectedSymbol = newSymbol;
        NavigateToTrading();
        _ = ToggleBotCommand.ExecuteAsync(null).ContinueWith(_ =>
            Application.Current.Dispatcher.BeginInvoke(() => _isAutoSwitching = false));
    }

    private void UpdateAnalysisDisplay(SignalResult sig)
    {
        HasAnalysis = true;
        LastScore = sig.Score;

        if (sig.Direction == "L") { LastDirection = "LONG"; LastDirectionColor = "#22C55E"; }
        else if (sig.Direction == "S") { LastDirection = "SHORT"; LastDirectionColor = "#EF4444"; }
        else { LastDirection = "대기"; LastDirectionColor = "#888888"; }

        // KYJ 분석 표시 — 실제 신호(L/S)만 크로스로 카운트
        if (sig.Direction != "W")
        {
            CrossCount++;
            string crossType = sig.Direction == "L" ? "골든크로스" : "데드크로스";
            CrossInfo = $"{crossType} #{CrossCount} ({sig.Score}점)";
        }
        else
        {
            string reason = sig.Reasons.Count > 0 ? sig.Reasons[0] : "대기";
            CrossInfo = $"{reason}";
        }

        if (sig.Detail is { } d)
        {
            AnalysisRsi = $"{d.Rsi:F1} ({d.RsiScore:+0;-0})";
            AnalysisMacd = $"{(d.MacdHist > 0 ? "양" : "음")} ({d.MacdScore:+0;-0})";
            AnalysisAdx = $"{d.Adx:F1} ({d.AdxScore:+0;-0}){(d.Adx < 20 ? " 약함" : "")}";
            AnalysisVolume = $"x{d.VolumeRatio:F2} ({d.VolumeScore:+0;-0})";
            AnalysisTrend1H = $"{d.TrendH1} ({d.TrendH1Score:+0;-0})";
            AnalysisTrend4H = $"{d.Trend4H} ({d.Trend4HScore:+0;-0})";
            AnalysisCandle = $"{(d.IsBullishCandle ? "양봉" : "음봉")} ({d.CandleScore:+0;-0})";
        }
    }
}
