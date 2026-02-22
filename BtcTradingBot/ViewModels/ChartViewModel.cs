using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BtcTradingBot.Collections;
using BtcTradingBot.Models;
using BtcTradingBot.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace BtcTradingBot.ViewModels;

public partial class ChartViewModel : ObservableObject, IDisposable
{
    private const int MaxCandles = 3000;
    private const int LoadPerRequest = 1500;

    private BinanceWebSocketService? _ws;
    private readonly PriceTickBuffer _tickBuffer = new();
    private CandleAggregator _aggregator;
    private readonly IndicatorSet _indicators = new();
    private readonly DispatcherTimer _uiTimer;

    // 캔들 + EMA 내부 버퍼 (RingBuffer)
    private readonly RingBuffer<FinancialPointI> _candlePoints = new(MaxCandles);
    private readonly RingBuffer<double> _ema7Points = new(MaxCandles);
    private readonly RingBuffer<double> _ema21Points = new(MaxCandles);
    private readonly RingBuffer<double> _ema50Points = new(MaxCandles);
    private readonly RingBuffer<double> _volumePoints = new(MaxCandles);
    private readonly RingBuffer<long> _timestamps = new(MaxCandles);

    // UI 바인딩용 컬렉션 (Batch 지원)
    public BatchObservableCollection<FinancialPointI> CandleValues { get; } = new();
    public BatchObservableCollection<double> Ema7Values { get; } = new();
    public BatchObservableCollection<double> Ema21Values { get; } = new();
    public BatchObservableCollection<double> Ema50Values { get; } = new();
    public BatchObservableCollection<double> VolumeValues { get; } = new();

    // 트레이드 마커
    public ObservableCollection<RectangularSection> Sections { get; } = new();
    private readonly ObservableCollection<ObservablePoint> _entryMarkers = new();
    private readonly ObservableCollection<ObservablePoint> _exitMarkers = new();

    // X축 레이블용 타임스탬프 (CandleValues와 동기화)
    public List<long> TimestampLabels { get; } = new();

    // 호가 데이터
    public ObservableCollection<OrderBookEntry> OrderBookBids { get; } = new();
    public ObservableCollection<OrderBookEntry> OrderBookAsks { get; } = new();
    [ObservableProperty] private double _orderBookMaxQty;
    private DateTime _lastOrderBookUpdate = DateTime.MinValue;
    private bool _isUpdatingOrderBook;

    [ObservableProperty] private string _currentPrice = "--";
    [ObservableProperty] private string _priceChange = "0.00%";
    [ObservableProperty] private string _priceChangeColor = "#9CA3AF";
    [ObservableProperty] private string _selectedTimeFrame = "15m";

    private double _open24h;
    private string _symbol = "BTCUSDT";
    private string _loadedSymbol = "";  // 실제 로드 성공한 심볼
    private int _pricePrecision = 2;
    private BinanceApi? _restApi;
    private bool _isLoading;

    public ISeries[] CandleSeries { get; }
    public ISeries[] VolumeSeries { get; }

    public ChartViewModel()
    {
        _aggregator = new CandleAggregator(TimeFrame.M15);

        // 150ms UI 갱신 타이머 (체감 차이 없이 CPU 부하 감소)
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _uiTimer.Tick += OnUiTick;

        CandleSeries = [
            new CandlesticksSeries<FinancialPointI>
            {
                Values = CandleValues,
                UpStroke = new SolidColorPaint(SKColors.LimeGreen) { StrokeThickness = 1 },
                UpFill = new SolidColorPaint(new SKColor(34, 197, 94)),
                DownStroke = new SolidColorPaint(SKColors.Red) { StrokeThickness = 1 },
                DownFill = new SolidColorPaint(new SKColor(239, 68, 68)),
            },
            new LineSeries<double>
            {
                Values = Ema7Values,
                Stroke = new SolidColorPaint(new SKColor(59, 130, 246)) { StrokeThickness = 1.5f },
                GeometrySize = 0, Fill = null, LineSmoothness = 0,
            },
            new LineSeries<double>
            {
                Values = Ema21Values,
                Stroke = new SolidColorPaint(new SKColor(249, 115, 22)) { StrokeThickness = 1.5f },
                GeometrySize = 0, Fill = null, LineSmoothness = 0,
            },
            new LineSeries<double>
            {
                Values = Ema50Values,
                Stroke = new SolidColorPaint(new SKColor(168, 85, 247)) { StrokeThickness = 1.5f },
                GeometrySize = 0, Fill = null, LineSmoothness = 0,
            },
            // 진입 마커
            new ScatterSeries<ObservablePoint>
            {
                Values = _entryMarkers,
                GeometrySize = 14,
                Stroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 1.5f },
                Fill = new SolidColorPaint(new SKColor(30, 144, 255)),
                ZIndex = 10,
            },
            // 청산 마커
            new ScatterSeries<ObservablePoint>
            {
                Values = _exitMarkers,
                GeometrySize = 12,
                Stroke = new SolidColorPaint(new SKColor(255, 165, 0)) { StrokeThickness = 2f },
                Fill = new SolidColorPaint(new SKColor(255, 165, 0, 80)),
                ZIndex = 10,
            },
        ];

        VolumeSeries = [
            new ColumnSeries<double>
            {
                Values = VolumeValues,
                Fill = new SolidColorPaint(new SKColor(59, 130, 246, 80)),
                Stroke = null,
            }
        ];
    }

    public async Task InitializeAsync(string apiKey, string apiSecret, string symbol = "BTCUSDT")
    {
        _symbol = symbol;
        _restApi = new BinanceApi(apiKey, apiSecret);
        await _restApi.SyncServerTime();
        await LoadCandlesAndStart();
        // InitializeAsync는 직접 await하므로 _loadedSymbol은 LoadCandlesAndStart 내부에서 설정됨
    }

    private async Task LoadCandlesAndStart()
    {
        if (_restApi == null || _isLoading) return;
        _isLoading = true;
        var targetSymbol = _symbol; // 로드 시작 시점의 심볼 캡처

        try
        {
            _uiTimer.Stop();
            await StopWebSocket();

            // REST로 히스토리 캔들 로드
            var tf = SelectedTimeFrame switch
            {
                "1m" => "1m",
                "5m" => "5m",
                "1h" => "1h",
                "4h" => "4h",
                "1D" => "1d",
                _ => "15m",
            };

            // 1달치 로드 (타임프레임에 따라 필요 개수 계산)
            var candles = await LoadMonthOfCandles(tf);
            if (candles == null || candles.Count == 0) return;

            // 데이터 초기화
            var tfEnum = SelectedTimeFrame switch
            {
                "1m" => TimeFrame.M1,
                "5m" => TimeFrame.M5,
                "1h" => TimeFrame.H1,
                "4h" => TimeFrame.H4,
                "1D" => TimeFrame.D1,
                _ => TimeFrame.M15,
            };
            _aggregator = new CandleAggregator(tfEnum);
            _indicators.Reset();

            _candlePoints.Clear();
            _ema7Points.Clear();
            _ema21Points.Clear();
            _ema50Points.Clear();
            _volumePoints.Clear();
            _timestamps.Clear();
            ClearMarkers();

            // 히스토리 채우기
            foreach (var c in candles)
            {
                _candlePoints.Add(new FinancialPointI(c.High, c.Open, c.Close, c.Low));
                var (e7, e21, e50) = _indicators.Update(c.Close);
                _ema7Points.Add(e7);
                _ema21Points.Add(e21);
                _ema50Points.Add(e50);
                _volumePoints.Add(c.Volume);
                _timestamps.Add(c.OpenTime);
            }

            _aggregator.InitFromHistory(candles);

            // 24시간 기준가 (24h 전에 가장 가까운 캔들)
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long target24h = now - 24 * 60 * 60 * 1000;
            _open24h = candles[0].Open;
            foreach (var c in candles)
            {
                if (c.OpenTime <= target24h) _open24h = c.Open;
                else break;
            }

            // UI 컬렉션 동기화
            SyncCollections();

            // 현재가 업데이트
            var lastCandle = candles[^1];
            UpdatePriceDisplay(lastCandle.Close);

            // WebSocket 시작
            _ws = new BinanceWebSocketService();
            _ws.OnTick += tick => _tickBuffer.Push(tick);
            _ = Task.Run(() => _ws.StartAsync(_symbol));

            // 100ms 타이머 시작
            _uiTimer.Start();

            // 로드 성공 기록
            _loadedSymbol = targetSymbol;
        }
        catch (Exception)
        {
            // 로드 실패 시 타이머/WS 복구 시도하지 않음 — 차트는 이전 데이터 유지
            // _loadedSymbol 갱신하지 않아 같은 심볼 재시도 가능
        }
        finally
        {
            _isLoading = false;

            // 로드 중 심볼이 변경되었으면 새 심볼로 재로드
            if (_symbol != targetSymbol)
                _ = LoadCandlesAndStart();
        }
    }

    private async Task<List<Candle>?> LoadMonthOfCandles(string tf)
    {
        // 첫 배치 로드
        var batch1 = await _restApi!.GetKlines(_symbol, tf, LoadPerRequest);
        if (batch1 == null || batch1.Count == 0) return batch1;

        // 15m이면 1500개로 약 15일 → 2번째 배치로 한달 채움
        // 5m이면 1500개로 약 5일 → 추가 배치 필요하지만 3000개(10일)까지만
        if (batch1.Count >= LoadPerRequest && tf is "15m" or "5m" or "1m")
        {
            long firstTime = batch1[0].OpenTime;
            // firstTime 이전 데이터를 endTime으로 요청
            var batch2 = await LoadKlinesBefore(tf, firstTime, LoadPerRequest);
            if (batch2 != null && batch2.Count > 0)
            {
                batch2.AddRange(batch1);
                return batch2;
            }
        }

        return batch1;
    }

    private async Task<List<Candle>?> LoadKlinesBefore(string tf, long endTimeMs, int limit)
    {
        return await _restApi!.GetKlines(_symbol, tf, limit, endTimeMs - 1);
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        // 호가 2초 간격 폴링 (중첩 방지)
        if (_restApi != null && !_isUpdatingOrderBook && (DateTime.UtcNow - _lastOrderBookUpdate).TotalSeconds >= 2.0)
        {
            _lastOrderBookUpdate = DateTime.UtcNow;
            _isUpdatingOrderBook = true;
            _ = UpdateOrderBookAsync();
        }

        var tick = _tickBuffer.TakeLatest();
        if (tick == null) return;

        UpdatePriceDisplay(tick.Price);

        var update = _aggregator.Update(tick);
        if (update == null) return;

        var candle = update.Candle;

        if (update.IsNew && _candlePoints.Count > 0)
        {
            // 새 캔들 — 이전 캔들 확정 시 EMA 업데이트
            if (update.IsClosed && _candlePoints.Count > 0)
            {
                var prev = _candlePoints.Last();
                var (e7, e21, e50) = _indicators.Update(prev.Close);
                _ema7Points.UpdateLast(e7);
                _ema21Points.UpdateLast(e21);
                _ema50Points.UpdateLast(e50);

                if (Ema7Values.Count > 0)
                {
                    Ema7Values[^1] = e7;
                    Ema21Values[^1] = e21;
                    Ema50Values[^1] = e50;
                }
            }

            // 새 캔들 추가
            var fp = new FinancialPointI(candle.High, candle.Open, candle.Close, candle.Low);
            _candlePoints.Add(fp);
            _volumePoints.Add(candle.Volume);
            _timestamps.Add(candle.OpenTime);

            var (pe7, pe21, pe50) = _indicators.Peek(candle.Close);
            _ema7Points.Add(pe7);
            _ema21Points.Add(pe21);
            _ema50Points.Add(pe50);

            CandleValues.Add(fp);
            VolumeValues.Add(candle.Volume);
            Ema7Values.Add(pe7);
            Ema21Values.Add(pe21);
            Ema50Values.Add(pe50);
            TimestampLabels.Add(candle.OpenTime);

            if (CandleValues.Count > MaxCandles)
            {
                CandleValues.RemoveAt(0);
                VolumeValues.RemoveAt(0);
                Ema7Values.RemoveAt(0);
                Ema21Values.RemoveAt(0);
                Ema50Values.RemoveAt(0);
                TimestampLabels.RemoveAt(0);
            }
        }
        else if (_candlePoints.Count > 0)
        {
            // 진행중 캔들 업데이트 — OHLC만 변경
            var fp = new FinancialPointI(candle.High, candle.Open, candle.Close, candle.Low);
            _candlePoints.UpdateLast(fp);
            _volumePoints.UpdateLast(candle.Volume);

            var (pe7, pe21, pe50) = _indicators.Peek(candle.Close);
            _ema7Points.UpdateLast(pe7);
            _ema21Points.UpdateLast(pe21);
            _ema50Points.UpdateLast(pe50);

            if (CandleValues.Count > 0)
            {
                CandleValues[^1] = fp;
                VolumeValues[^1] = candle.Volume;
                Ema7Values[^1] = pe7;
                Ema21Values[^1] = pe21;
                Ema50Values[^1] = pe50;
            }
        }
    }

    private string _lastPriceStr = "";
    private void UpdatePriceDisplay(double price)
    {
        // 동일 값이면 PropertyChanged 발생하지 않도록 스킵
        var priceStr = _pricePrecision <= 2 ? $"${price:N2}" : $"${price.ToString($"N{_pricePrecision}")}";

        if (priceStr == _lastPriceStr) return;
        _lastPriceStr = priceStr;

        CurrentPrice = priceStr;
        if (_open24h > 0)
        {
            var pct = (price - _open24h) / _open24h * 100;
            PriceChange = $"{(pct >= 0 ? "+" : "")}{pct:F2}%";
            PriceChangeColor = pct >= 0 ? "#22C55E" : "#EF4444";
        }
    }

    private void SyncCollections()
    {
        // Batch 모드: 모든 변경을 모아서 마지막에 단일 Reset 이벤트 발행
        CandleValues.BeginBatch();
        Ema7Values.BeginBatch();
        Ema21Values.BeginBatch();
        Ema50Values.BeginBatch();
        VolumeValues.BeginBatch();

        CandleValues.Clear();
        Ema7Values.Clear();
        Ema21Values.Clear();
        Ema50Values.Clear();
        VolumeValues.Clear();
        TimestampLabels.Clear();

        foreach (var p in _candlePoints) CandleValues.Add(p);
        foreach (var v in _ema7Points) Ema7Values.Add(v);
        foreach (var v in _ema21Points) Ema21Values.Add(v);
        foreach (var v in _ema50Points) Ema50Values.Add(v);
        foreach (var v in _volumePoints) VolumeValues.Add(v);
        foreach (var t in _timestamps) TimestampLabels.Add(t);

        VolumeValues.EndBatch();
        Ema50Values.EndBatch();
        Ema21Values.EndBatch();
        Ema7Values.EndBatch();
        CandleValues.EndBatch();
    }

    [RelayCommand]
    private async Task ChangeTimeFrame(string tf)
    {
        SelectedTimeFrame = tf;
        await LoadCandlesAndStart();
    }

    public async Task ChangeSymbol(string symbol, int pricePrecision = 2)
    {
        // 이미 같은 심볼이 성공적으로 로드되었으면 스킵
        if (symbol == _loadedSymbol) return;
        _symbol = symbol;
        _pricePrecision = pricePrecision;
        await LoadCandlesAndStart();
    }

    public string FormatTimestamp(int index)
    {
        if (index < 0 || index >= TimestampLabels.Count) return "";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(TimestampLabels[index]).LocalDateTime;
        return SelectedTimeFrame switch
        {
            "1m" or "5m" => dt.ToString("HH:mm"),
            "15m" => dt.ToString("M/d HH:mm"),
            "1h" => dt.ToString("M/d HH:mm"),
            "4h" => dt.ToString("M/d HH시"),
            "1D" => dt.ToString("yy/M/d"),
            _ => dt.ToString("M/d HH:mm"),
        };
    }

    public void AddTradeMarker(TradeMarkerInfo marker)
    {
        if (marker.Action.StartsWith("ENTRY"))
        {
            Sections.Clear();

            // 진입선 (파란 실선)
            Sections.Add(MakeHLine(marker.Price, new SKColor(30, 144, 255), null));

            // SL선 (빨간 점선)
            if (marker.SlPrice.HasValue)
                Sections.Add(MakeHLine(marker.SlPrice.Value, new SKColor(239, 68, 68),
                    new DashEffect([6f, 4f])));

            // TP선 (초록 점선)
            if (marker.TpPrice.HasValue)
                Sections.Add(MakeHLine(marker.TpPrice.Value, new SKColor(34, 197, 94),
                    new DashEffect([6f, 4f])));

            // 진입 마커 점
            _entryMarkers.Add(new ObservablePoint(CandleValues.Count - 1, marker.Price));
        }
        else // EXIT
        {
            Sections.Clear();
            _entryMarkers.Clear();
            _exitMarkers.Add(new ObservablePoint(CandleValues.Count - 1, marker.Price));
        }
    }

    public void ClearMarkers()
    {
        Sections.Clear();
        _entryMarkers.Clear();
        _exitMarkers.Clear();
    }

    private static RectangularSection MakeHLine(double price, SKColor color, PathEffect? dashEffect)
    {
        var paint = new SolidColorPaint(color) { StrokeThickness = 1.5f };
        if (dashEffect != null) paint.PathEffect = dashEffect;

        return new RectangularSection
        {
            Yi = price,
            Yj = price,
            Stroke = paint,
            Fill = null,
            ZIndex = 5,
        };
    }

    private async Task UpdateOrderBookAsync()
    {
        try
        {
            var data = await _restApi!.GetOrderBook(_symbol, 10);
            double maxQty = 0;

            // Bids — in-place 교체 (CollectionChanged Reset 방지)
            UpdateInPlace(OrderBookBids, data.Bids, ref maxQty);

            // Asks — 가격 오름차순 역순 (위에 높은 가격)
            var reversedAsks = new List<OrderBookEntry>(data.Asks.Count);
            for (int i = data.Asks.Count - 1; i >= 0; i--)
                reversedAsks.Add(data.Asks[i]);
            UpdateInPlace(OrderBookAsks, reversedAsks, ref maxQty);

            OrderBookMaxQty = maxQty;
        }
        catch { /* 호가 갱신 실패 무시 */ }
        finally { _isUpdatingOrderBook = false; }
    }

    private static void UpdateInPlace(ObservableCollection<OrderBookEntry> target, List<OrderBookEntry> source, ref double maxQty)
    {
        int i = 0;
        for (; i < source.Count && i < target.Count; i++)
        {
            if (source[i].Qty > maxQty) maxQty = source[i].Qty;
            if (target[i].Price != source[i].Price || target[i].Qty != source[i].Qty)
                target[i] = source[i];
        }
        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
        for (; i < source.Count; i++)
        {
            if (source[i].Qty > maxQty) maxQty = source[i].Qty;
            target.Add(source[i]);
        }
    }

    /// <summary>RingBuffer 직접 접근으로 Y축 범위 계산 (ObservableCollection 오버헤드 회피)</summary>
    public (double yMin, double yMax) GetYRange(int start, int end)
    {
        double yMin = double.MaxValue, yMax = double.MinValue;
        int count = _candlePoints.Count;
        if (count == 0) return (0, 0);
        if (start < 0) start = 0;
        if (end >= count) end = count - 1;
        if (start > end) return (0, 0);
        for (int i = start; i <= end; i++)
        {
            var c = _candlePoints[i];
            if (c.Low < yMin) yMin = c.Low;
            if (c.High > yMax) yMax = c.High;
        }
        return (yMin, yMax);
    }

    private async Task StopWebSocket()
    {
        if (_ws != null)
        {
            await _ws.StopAsync();
            _ws.Dispose();
            _ws = null;
        }
        _tickBuffer.Clear();
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _ws?.Dispose();
        _ws = null;
        _tickBuffer.Clear();
        _restApi?.Dispose();
    }
}
