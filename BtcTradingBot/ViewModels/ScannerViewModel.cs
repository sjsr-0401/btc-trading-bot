using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BtcTradingBot.Models;
using BtcTradingBot.Services;

namespace BtcTradingBot.ViewModels;

public partial class ScannerViewModel : ObservableObject, IDisposable
{
    private readonly ScannerService _scanner = new();
    private readonly SemaphoreSlim _scanMutex = new(1, 1);
    private CancellationTokenSource? _cts;
    private System.Timers.Timer? _autoTimer;
    private List<SymbolInfo> _symbols = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _lastScanTime = "--";
    [ObservableProperty] private string _scanStatus = "스캔 대기";
    [ObservableProperty] private int _scanProgress;
    [ObservableProperty] private int _totalSymbols;

    // === 설정 ===
    private int _scanIntervalSec = 60;
    private int _scanCoinCount = 10;

    /// <summary>봇 실행 중 여부 (MainViewModel에서 설정)</summary>
    public bool IsBotRunning { get; set; }

    /// <summary>포지션 보유 중 여부 (MainViewModel에서 설정)</summary>
    public bool HasOpenPosition { get; set; }

    /// <summary>현재 트레이딩 중인 심볼 (MainViewModel에서 설정)</summary>
    public string CurrentSymbol { get; set; } = "";

    /// <summary>자동 진입 기준 점수 (MainViewModel에서 설정)</summary>
    public int AutoEntryScore { get; set; } = 50;

    public ObservableCollection<CoinScanResult> ScanResults { get; } = new();

    /// <summary>코인 클릭 시 MainViewModel에 전달</summary>
    public event Action<string>? OnCoinSelected;

    /// <summary>더 좋은 코인 발견 시 (symbol, readinessScore)</summary>
    public event Action<string, int>? OnBetterCoinFound;

    public void SetSymbols(List<SymbolInfo> symbols)
    {
        _symbols = symbols;
        TotalSymbols = symbols.Count;
    }

    public void UpdateSettings(int intervalSec, int coinCount)
    {
        _scanIntervalSec = Math.Clamp(intervalSec, 30, 300);
        _scanCoinCount = Math.Clamp(coinCount, 5, 20);

        // 타이머 재시작
        if (_autoTimer != null)
        {
            _autoTimer.Interval = _scanIntervalSec * 1000;
        }
    }

    public async Task InitialScanAsync()
    {
        if (_symbols.Count == 0)
        {
            ScanStatus = "코인 목록 없음";
            return;
        }
        await RunScanAsync();
        StartAutoTimer();
    }

    [RelayCommand]
    private async Task RefreshScan()
    {
        if (IsScanning) return;
        await RunScanAsync();
    }

    [RelayCommand]
    private void SelectCoin(CoinScanResult? result)
    {
        if (result?.Symbol?.Symbol == null) return;
        OnCoinSelected?.Invoke(result.Symbol.Symbol);
    }

    private async Task RunScanAsync()
    {
        if (_symbols.Count == 0)
        {
            ScanStatus = "스캔할 코인 없음";
            return;
        }

        // SemaphoreSlim으로 동시 실행 방지
        if (!await _scanMutex.WaitAsync(0)) return;

        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        oldCts?.Dispose();

        IsScanning = true;
        ScanStatus = $"0/{_symbols.Count} 스캔 중...";
        ScanProgress = 0;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = await _scanner.ScanAsync(_symbols, _cts.Token,
                progress: (current, total) =>
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        ScanProgress = current;
                        ScanStatus = $"{current}/{total} 스캔 중...";
                    });
                });
            sw.Stop();

            Application.Current.Dispatcher.Invoke(() =>
            {
                ScanResults.Clear();
                foreach (var r in results) ScanResults.Add(r);
                LastScanTime = DateTime.Now.ToString("HH:mm");
                ScanStatus = $"{results.Count}개 완료 ({sw.Elapsed.TotalSeconds:F1}초)";
            });

            // 자동 전환 체크: 현재 코인보다 훨씬 좋은 코인이 있으면 알림
            CheckForBetterCoin(results);
        }
        catch (OperationCanceledException) { ScanStatus = "취소됨"; }
        catch (Exception ex) { ScanStatus = $"오류: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _scanMutex.Release();
        }
    }

    private void CheckForBetterCoin(List<CoinScanResult> results)
    {
        if (results.Count == 0) return;

        var best = results[0]; // ReadinessScore 내림차순 정렬됨
        if (best.Symbol.Symbol == CurrentSymbol) return;

        // 1위 코인이 AutoEntryScore 이상이면 전환 추천
        if (best.ReadinessScore >= AutoEntryScore)
        {
            OnBetterCoinFound?.Invoke(best.Symbol.Symbol, best.ReadinessScore);
        }
    }

    private void StartAutoTimer()
    {
        _autoTimer?.Dispose();
        _autoTimer = new System.Timers.Timer(_scanIntervalSec * 1000);
        _autoTimer.Elapsed += async (_, _) =>
        {
            // 봇 실행 중 + 포지션 보유 중이면 스캔 스킵 (API rate limit 보호)
            // 대기 중이면 자동 전환을 위해 스캔 허용
            if (IsBotRunning && HasOpenPosition) return;
            if (!await _scanMutex.WaitAsync(0)) return;
            _scanMutex.Release(); // WaitAsync(0) 성공하면 바로 릴리즈 — RunScanAsync 내부에서 다시 잡음
            await RunScanAsync();
        };
        _autoTimer.AutoReset = true;
        _autoTimer.Start();
    }

    public void Dispose()
    {
        _autoTimer?.Dispose();
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _scanMutex.Dispose();
    }
}
