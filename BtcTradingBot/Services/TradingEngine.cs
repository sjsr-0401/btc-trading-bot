using System.IO;
using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

public class TradingEngine : IDisposable
{
    private readonly BotConfig _cfg;
    private BinanceApi? _api;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _closeLock = new(1, 1);
    private StreamWriter? _logWriter;
    private int _precision = 3;
    private int _pricePrecision = 2;

    // 바이낸스 선물 수수료 (Taker 0.05%, 진입+청산 왕복)
    private const double FeeRate = 0.0005;

    // 엔진별 거래 후 휴식 시간 (초)
    private const long KyjCooldownSec = 90 * 60;   // 90분

    // 에러 재시도 간격
    private const int ErrorRetryBaseMs = 30_000;    // 기본 30초
    private const int ErrorRetryMaxMs  = 180_000;   // 최대 3분

    // 상태
    public int TotalTrades { get; private set; }
    public int TotalWins { get; private set; }
    public int TotalLosses { get; private set; }
    public double TotalPnl { get; private set; }
    public int ConsecLosses { get; private set; }
    public int ConsecWins { get; private set; }
    public int DailyTrades { get; private set; }
    public double DailyPnl { get; private set; }
    public DateTime? CooldownUntil { get; private set; }
    public double HighestProfit { get; private set; }
    public double CurrentBalance { get; private set; }

    // SL/TP 가격 (UI 표시용)
    public double SlPrice { get; private set; }
    public double TpPrice { get; private set; }

    // 최대 드로다운 (MDD)
    public double PeakBalance { get; private set; }
    public double MaxDrawdownPct { get; private set; }

    // 수익 팩터
    public double TotalGrossProfit { get; private set; }
    public double TotalGrossLoss { get; private set; }

    private string _pendingType = "N";
    private int _pendingScore;
    private int _pendingCount;
    private double _pendingPrice;
    private int _signalSkipCycles;    // 만료 후 재탐지 방지
    private long _lastTradeTime;
    private DateTime _lastTradeDate = DateTime.Today;
    private double _dailyStartBalance;

    // 페이퍼 트레이딩 상태
    private Position _paperPosition = new("N", 0, 0, 0);
    private double _paperSlPrice;
    private double _paperTpPrice;
    private double _paperEntryFee;
    public DateTime? PositionOpenTime { get; private set; }
    private bool _breakEvenActivated;
    private bool _stateRestored;

    // 실전 포지션 추적 (외부 청산 감지용)
    private string _openPosType = "N";
    private double _openEntryPrice;
    private double _openQty;

    public event Action<string>? OnLog;
    public event Action<Position, double, double>? OnPositionUpdate; // pos, price, unrealizedPnl
    public event Action? OnStatsChanged;
    public event Action<TradeRecord>? OnTradeComplete;
    public event Action<TradeMarkerInfo>? OnTradeMarker;
    public event Action<SignalResult>? OnAnalysisResult;
    public event Action<string>? OnEngineState;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    private bool IsTest => _cfg.IsTestMode;

    private string CoinTag => _cfg.Symbol.Replace("USDT", "");

    public TradingEngine(BotConfig config)
    {
        _cfg = config;
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        InitLogFile();

        if (IsTest)
        {
            // 테스트 모드: 공개 API만 사용 (빈 키로 생성)
            _api = new BinanceApi("", "");
            _precision = 3;
            _pricePrecision = await _api.GetPricePrecision(_cfg.Symbol);
            if (!_stateRestored)
            {
                CurrentBalance = _cfg.TestBalance;
                _paperPosition = new Position("N", 0, 0, 0);
            }
            string posInfo = _paperPosition.Type != "N"
                ? $" | 복원: {(_paperPosition.Type == "L" ? "LONG" : "SHORT")} @ {_paperPosition.EntryPrice:N2}"
                : "";
            Log("[TEST] START! [{0}] 가상 잔고 {1:F2} USDT | {2} x{3} | {4} USDT{5}",
                _cfg.SelectedEngine, CurrentBalance, _cfg.Symbol, _cfg.Leverage, _cfg.TradeUsdt, posInfo);
        }
        else
        {
            _api = new BinanceApi(_cfg.ApiKey, _cfg.ApiSecret);
            await _api.SyncServerTime();
            _precision = Math.Clamp(await _api.GetPrecision(_cfg.Symbol), 0, 8);
            _pricePrecision = await _api.GetPricePrecision(_cfg.Symbol);

            // 기존 포지션 감지 → 있으면 레버리지/마진 변경 스킵
            bool hasExistingPosition = false;
            try
            {
                var existPos = await _api.GetPosition(_cfg.Symbol);
                if (existPos.Type != "N" && existPos.Amount > 0)
                    hasExistingPosition = true;
            }
            catch { }

            if (!hasExistingPosition)
            {
                await _api.SetLeverage(_cfg.Symbol, _cfg.Leverage);
                await _api.SetMarginType(_cfg.Symbol);
            }

            CurrentBalance = await _api.GetBalance();
            Log("START! [{0}] {1:F2} USDT | {2} x{3} | {4} USDT",
                _cfg.SelectedEngine, CurrentBalance, _cfg.Symbol, _cfg.Leverage, _cfg.TradeUsdt);

            // 기존 포지션 SL/TP 적용
            try
            {
                var existPos = await _api.GetPosition(_cfg.Symbol);
                if (existPos.Type != "N" && existPos.Amount > 0)
                {
                    var cp = await _api.GetPrice(_cfg.Symbol);
                    var c15 = await _api.GetKlines(_cfg.Symbol, "15m", 200);
                    if (c15 != null)
                    {
                        var (sl, tp, _atr) = Strategy.CalcSlTp(c15);
                        string closeSide = existPos.Type == "L" ? "SELL" : "BUY";
                        double slPrice, tpPrice;
                        if (existPos.Type == "L")
                        {
                            slPrice = Math.Round(existPos.EntryPrice * (1 - sl / 100), _pricePrecision);
                            tpPrice = Math.Round(existPos.EntryPrice * (1 + tp / 100), _pricePrecision);
                        }
                        else
                        {
                            slPrice = Math.Round(existPos.EntryPrice * (1 + sl / 100), _pricePrecision);
                            tpPrice = Math.Round(existPos.EntryPrice * (1 - tp / 100), _pricePrecision);
                        }

                        // 기존 주문 정리 후 새로 걸기
                        await _api.CancelAllOrders(_cfg.Symbol);
                        await _api.StopMarket(_cfg.Symbol, closeSide, existPos.Amount, slPrice, _pricePrecision);
                        await _api.TakeProfitMarket(_cfg.Symbol, closeSide, existPos.Amount, tpPrice, _pricePrecision);

                        SlPrice = slPrice;
                        TpPrice = tpPrice;
                        PositionOpenTime = DateTime.Now;
                        _lastTradeTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        _openPosType = existPos.Type;
                        _openEntryPrice = existPos.EntryPrice;
                        _openQty = existPos.Amount;

                        string side = existPos.Type == "L" ? "LONG" : "SHORT";
                        Log("[기존 포지션] {0} {1} @ {2:N2} | SL:{3} TP:{4}",
                            side, existPos.Amount, existPos.EntryPrice, slPrice, tpPrice);
                        OnEngineState?.Invoke($"{CoinTag} 기존 포지션 감지 — SL/TP 적용됨");
                    }
                }
            }
            catch (Exception ex) { Log("기존 포지션 SL/TP 설정 실패: {0}", ex.Message); }
        }

        PeakBalance = CurrentBalance;
        _dailyStartBalance = CurrentBalance;

        int cnt = 0;
        int consecErrors = 0;
        ResetPending();

        // KYJ 사전분석: 최근 8봉(2시간) 내 EMA 크로스 탐색
        if (_cfg.SelectedEngine == "KYJ")
        {
            try
            {
                var preC15 = await _api!.GetKlines(_cfg.Symbol, "15m", 200);
                var preC1h = await _api.GetKlines(_cfg.Symbol, "1h", 200);
                var preC4h = await _api.GetKlines(_cfg.Symbol, "4h", 200);
                if (preC15 != null && preC1h != null && preC4h != null)
                {
                    var preSig = Strategy.PreAnalyze(preC15, preC1h, preC4h);
                    OnAnalysisResult?.Invoke(preSig);
                    if (preSig.Direction != "W")
                    {
                        _pendingType = preSig.Direction;
                        _pendingScore = preSig.Score;
                        _pendingCount = 0;
                        _pendingPrice = preC15[^1].Close;
                        Log("사전분석: {0} score:{1} [{2}]",
                            preSig.Direction == "L" ? "LONG" : "SHORT",
                            preSig.Score, string.Join(" | ", preSig.Reasons));
                        OnEngineState?.Invoke($"{CoinTag} 사전분석: {(preSig.Direction == "L" ? "LONG" : "SHORT")} 신호 감지! ({preSig.Score}점)");
                    }
                    else
                    {
                        Log("사전분석: {0}", string.Join(" | ", preSig.Reasons));
                    }
                }
            }
            catch (Exception ex) { Log("사전분석 실패: {0}", ex.Message); }
        }

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                cnt++;
                var now = DateTime.Now;

                // 일일 리셋
                if (now.Date != _lastTradeDate)
                {
                    if (DailyTrades > 0)
                        Log("DAILY: {0}t PnL:{1:F2}", DailyTrades, DailyPnl);
                    DailyTrades = 0;
                    DailyPnl = 0;
                    _dailyStartBalance = CurrentBalance;
                    _lastTradeDate = now.Date;
                    Log("══════ {0:yyyy-MM-dd} ══════", now);
                }

                // 일일 제한
                if (DailyTrades >= _cfg.MaxDailyTrades)
                {
                    OnEngineState?.Invoke($"{CoinTag} 일일 한도 도달 ({DailyTrades}/{_cfg.MaxDailyTrades})");
                    if (cnt % 10 == 1) Log("일일한도 도달");
                    await Task.Delay(_cfg.CheckIntervalSeconds * 1000, _cts.Token);
                    continue;
                }

                // 잔고 소진 방어
                if (!IsTest) CurrentBalance = await _api.GetBalance();
                if (CurrentBalance < _cfg.TradeUsdt * 0.5)
                {
                    Log("[FATAL] 잔고 부족 — 거래 불가 ({0:F2} USDT)", CurrentBalance);
                    OnEngineState?.Invoke($"{CoinTag} 잔고 부족 — 중지됨");
                    break;
                }

                // 일일 손실 제한 (당일 시작 잔고 기준)
                double dailyLossBase = _dailyStartBalance > 0 ? _dailyStartBalance : CurrentBalance;
                if (dailyLossBase > 0 && DailyPnl < 0 &&
                    Math.Abs(DailyPnl) / dailyLossBase * 100 >= _cfg.MaxDailyLossPct)
                {
                    OnEngineState?.Invoke($"{CoinTag} 손실 한도 도달 ({DailyPnl:F2} USDT)");
                    if (cnt % 10 == 1) Log("손실한도 도달");
                    await Task.Delay(_cfg.CheckIntervalSeconds * 1000, _cts.Token);
                    continue;
                }

                // 쿨다운
                if (CooldownUntil.HasValue && now < CooldownUntil.Value)
                {
                    var rm = (int)(CooldownUntil.Value - now).TotalMinutes;
                    OnEngineState?.Invoke($"{CoinTag} 쿨다운 {rm}분 남음");
                    if (cnt % 5 == 1) Log("쿨다운 {0}분 남음", rm);
                    await Task.Delay(_cfg.CheckIntervalSeconds * 1000, _cts.Token);
                    continue;
                }
                else if (CooldownUntil.HasValue)
                {
                    CooldownUntil = null;
                    ConsecLosses = 0;
                    Log("쿨다운 해제, 재시작");
                }

                var pos = IsTest ? _paperPosition : await _api.GetPosition(_cfg.Symbol);
                var cp = await _api.GetPrice(_cfg.Symbol);

                // 외부 청산 감지 (바이낸스 SL/TP 서버 주문 체결)
                if (!IsTest && pos.Type == "N" && _openPosType != "N" && _openEntryPrice > 0)
                {
                    bool longPos = _openPosType == "L";
                    double closePrice;
                    string extReason;
                    if (TpPrice > 0 && (longPos ? cp >= TpPrice * 0.99 : cp <= TpPrice * 1.01))
                    {
                        closePrice = TpPrice;
                        extReason = "TP";
                    }
                    else
                    {
                        closePrice = SlPrice > 0 ? SlPrice : cp;
                        extReason = _breakEvenActivated ? "SL(손익분기)" : "SL";
                    }
                    double extPnl = longPos
                        ? (closePrice - _openEntryPrice) * _openQty
                        : (_openEntryPrice - closePrice) * _openQty;
                    Log("외부청산 감지 [{0}] @ {1} pnl:{2:F2}", extReason, closePrice, extPnl);
                    var extPos = new Position(_openPosType, _openQty, _openEntryPrice, 0);
                    UpdateDrawdown();
                    RecordTrade(extPos, closePrice, extPnl, extReason);
                    try { OnTradeMarker?.Invoke(new TradeMarkerInfo("EXIT", closePrice, null, null, DateTime.Now)); } catch { }
                    try { OnPositionUpdate?.Invoke(new Position("N", 0, 0, 0), cp, 0); } catch { }
                    _openPosType = "N"; _openEntryPrice = 0; _openQty = 0;
                    SlPrice = 0; TpPrice = 0; HighestProfit = 0;
                    _breakEvenActivated = false; PositionOpenTime = null;
                    continue;
                }

                // 미실현 PnL 계산
                double unrealizedPnl = 0;
                if (pos.Type != "N" && pos.EntryPrice > 0)
                {
                    unrealizedPnl = pos.Type == "L"
                        ? (cp - pos.EntryPrice) * pos.Amount
                        : (pos.EntryPrice - cp) * pos.Amount;

                    // 테스트 모드: 예상 청산 수수료 차감 (실전은 바이낸스가 자체 계산)
                    if (IsTest)
                        unrealizedPnl -= cp * pos.Amount * FeeRate;
                }
                if (double.IsNaN(unrealizedPnl) || double.IsInfinity(unrealizedPnl))
                    unrealizedPnl = 0;
                OnPositionUpdate?.Invoke(pos, cp, unrealizedPnl);

                // 테스트 모드: SL/TP 트리거 체크
                if (IsTest && pos.Type != "N" && pos.EntryPrice > 0)
                {
                    bool slHit = pos.Type == "L" ? cp <= _paperSlPrice : cp >= _paperSlPrice;
                    bool tpHit = pos.Type == "L" ? cp >= _paperTpPrice : cp <= _paperTpPrice;
                    if (slHit)
                    {
                        Log("[TEST] SL 트리거! {0:F2}", _paperSlPrice);
                        await ClosePaperPosition(pos, _paperSlPrice, "SL");
                        await Task.Delay(5000, _cts.Token);
                        continue;
                    }
                    if (tpHit)
                    {
                        Log("[TEST] TP 트리거! {0:F2}", _paperTpPrice);
                        await ClosePaperPosition(pos, _paperTpPrice, "TP");
                        await Task.Delay(5000, _cts.Token);
                        continue;
                    }
                }

                // 포지션 있을 때: 트레일링
                if (pos.Type != "N" && pos.EntryPrice > 0 && cp > 0)
                {
                    double pfp = pos.Type == "L"
                        ? (cp - pos.EntryPrice) / pos.EntryPrice * 100
                        : (pos.EntryPrice - cp) / pos.EntryPrice * 100;
                    OnEngineState?.Invoke($"{CoinTag} 포지션 보유 중 ({(pos.Type == "L" ? "LONG" : "SHORT")} {pfp:+0.00;-0.00}%)");
                    if (pfp > HighestProfit) HighestProfit = pfp;

                    // 손익분기 보호: 수익 1.5% 이상 도달 시 SL을 진입가로 이동
                    if (!_breakEvenActivated && pfp >= 1.5)
                    {
                        if (IsTest)
                        {
                            _paperSlPrice = pos.EntryPrice;
                            SlPrice = pos.EntryPrice;
                            _breakEvenActivated = true;
                            Log("손익분기 보호: SL → {0:N2}", pos.EntryPrice);
                        }
                        else if (_api != null)
                        {
                            try
                            {
                                string beSide = pos.Type == "L" ? "SELL" : "BUY";
                                await _api.CancelAllOrders(_cfg.Symbol);
                                await _api.StopMarket(_cfg.Symbol, beSide, pos.Amount, pos.EntryPrice, _pricePrecision);
                                if (TpPrice > 0)
                                    await _api.TakeProfitMarket(_cfg.Symbol, beSide, pos.Amount, TpPrice, _pricePrecision);
                                SlPrice = pos.EntryPrice;
                                _breakEvenActivated = true;
                                Log("손익분기 보호: SL → {0:N2}", pos.EntryPrice);
                            }
                            catch (Exception ex) { Log("ERR: 손익분기 SL 변경 실패: {0} (다음 사이클 재시도)", ex.Message); }
                        }
                    }

                    if (HighestProfit >= 3.0 && HighestProfit - pfp >= 1.2)
                    {
                        Log("TRAIL peak:{0:F2}% now:{1:F2}%", HighestProfit, pfp);
                        if (IsTest)
                            await ClosePaperPosition(pos, cp, "Trail");
                        else
                            await ClosePosition(pos, cp, "Trail");
                        await Task.Delay(5000, _cts.Token);
                        continue;
                    }
                }

                // 데이터
                var c15 = await _api.GetKlines(_cfg.Symbol, "15m", 200);
                var c1h = await _api.GetKlines(_cfg.Symbol, "1h", 200);
                if (c15 == null || c1h == null)
                {
                    await Task.Delay(_cfg.CheckIntervalSeconds * 1000, _cts.Token);
                    continue;
                }

                // 포지션 없을 때: 신호
                if (pos.Type == "N")
                {
                    await AnalyzeKYJ(c15, c1h, cp);
                }

                if (cnt % 5 == 0) OnStatsChanged?.Invoke();

                consecErrors = 0; // 정상 완료 시 리셋
                await Task.Delay(_cfg.CheckIntervalSeconds * 1000, _cts.Token);
            }
            catch (TaskCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                consecErrors++;
                int delay = Math.Min(ErrorRetryBaseMs * consecErrors, ErrorRetryMaxMs);
                Log("ERR: {0} (retry #{1}, {2}s 후)", ex.Message, consecErrors, delay / 1000);

                if (consecErrors >= 10)
                {
                    Log("[FATAL] 연속 {0}회 오류 — 봇 중지", consecErrors);
                    OnEngineState?.Invoke($"{CoinTag} 연속 오류로 중지됨");
                    break;
                }

                try { await Task.Delay(delay, _cts.Token); }
                catch (TaskCanceledException) { break; }
            }
        }

        // 종료 시 상태 저장/정리
        if (IsTest)
        {
            // 페이퍼 상태 저장 (포지션 유무 무관 — 잔고/통계 포함)
            try
            {
                ConfigService.SavePaperState(CreatePaperState());
                if (_paperPosition.Type != "N")
                    Log("[SHUTDOWN] 테스트 상태 저장됨 (포지션: {0} @ {1:N2})", _paperPosition.Type, _paperPosition.EntryPrice);
                else
                    Log("[SHUTDOWN] 테스트 상태 저장됨 (잔고: {0:N2})", CurrentBalance);
            }
            catch { /* ignore save errors */ }
        }
        else if (_api != null && !_skipShutdownClose)
        {
            var pos = await _api.GetPosition(_cfg.Symbol);
            if (pos.Type != "N")
            {
                var cp = await _api.GetPrice(_cfg.Symbol);
                Log("[SHUTDOWN] 실전 포지션 정리 {0} @ {1:F2}", pos.Type, cp);
                await ClosePosition(pos, cp, "종료");
            }
        }
        else if (_skipShutdownClose)
        {
            Log("[SHUTDOWN] 자동전환 — 포지션 유지");
        }
        _api?.Dispose();
        _api = null;
        Log("엔진 종료");
    }

    private bool _skipShutdownClose;

    /// <summary>봇 중지. skipClose=true이면 종료 시 포지션 청산 안 함 (자동전환 시 사용)</summary>
    public void Stop(bool skipClose = false)
    {
        _skipShutdownClose = skipClose;
        _cts?.Cancel();
    }

    public async Task ManualClose()
    {
        if (_api == null) return;
        if (!await _closeLock.WaitAsync(5000)) { Log("ERR: 청산 타임아웃 — 다시 시도하세요"); return; }
        try
        {
            if (IsTest)
            {
                if (_paperPosition.Type != "N")
                {
                    var cp = await _api.GetPrice(_cfg.Symbol);
                    await ClosePaperPosition(_paperPosition, cp, "수동");
                }
                else Log("포지션 없음");
                return;
            }

            var pos = await _api.GetPosition(_cfg.Symbol);
            if (pos.Type != "N")
            {
                var cp = await _api.GetPrice(_cfg.Symbol);
                await ClosePosition(pos, cp, "수동");
            }
            else Log("포지션 없음");
        }
        finally { _closeLock.Release(); }
    }

    // ═══ 페이퍼 트레이딩 ═══

    private void OpenPaperPosition(string direction, double usdt, double sl, double tp, double price)
    {
        double qty = Math.Round(usdt * _cfg.Leverage / price, _precision);
        if (qty <= 0) { Log("[TEST] 수량 부족"); return; }

        // 진입 수수료 차감 (명목가치 × 수수료율)
        double entryFee = price * qty * FeeRate;
        CurrentBalance -= entryFee;
        _paperEntryFee = entryFee;

        _paperPosition = new Position(direction, qty, price, 0);

        if (direction == "L")
        {
            _paperSlPrice = Math.Round(price * (1 - sl / 100), _pricePrecision);
            _paperTpPrice = Math.Round(price * (1 + tp / 100), _pricePrecision);
        }
        else
        {
            _paperSlPrice = Math.Round(price * (1 + sl / 100), _pricePrecision);
            _paperTpPrice = Math.Round(price * (1 - tp / 100), _pricePrecision);
        }

        SlPrice = _paperSlPrice;
        TpPrice = _paperTpPrice;

        string label = direction == "L" ? "LONG" : "SHORT";
        Log("[TEST] {0} {1} @ {2:F2} x{3} (수수료: -{4:F4})", label, qty, price, _cfg.Leverage, entryFee);
        Log("[TEST]   SL: {0} (-{1}%)", _paperSlPrice, sl);
        Log("[TEST]   TP: {0} (+{1}%)", _paperTpPrice, tp);
        HighestProfit = 0;
        PositionOpenTime = DateTime.Now;
        _lastTradeTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try { OnTradeMarker?.Invoke(new TradeMarkerInfo(
            direction == "L" ? "ENTRY_LONG" : "ENTRY_SHORT",
            price, _paperSlPrice, _paperTpPrice, DateTime.Now)); } catch { /* subscriber error */ }
    }

    private Task ClosePaperPosition(Position pos, double cp, string reason = "")
    {
        double pnl = pos.EntryPrice > 0
            ? (pos.Type == "L" ? (cp - pos.EntryPrice) * pos.Amount : (pos.EntryPrice - cp) * pos.Amount)
            : 0;

        // 왕복 수수료 반영: 진입(이미 잔고에서 차감) + 청산
        double exitFee = cp * pos.Amount * FeeRate;
        double totalFee = _paperEntryFee + exitFee;
        pnl -= totalFee; // 순 PnL = 매매차익 - 왕복 수수료

        CurrentBalance += pnl + _paperEntryFee; // 잔고 = 이전잔고(진입fee차감됨) + 매매차익 - 청산fee
        _paperPosition = new Position("N", 0, 0, 0);
        _paperSlPrice = 0;
        _paperTpPrice = 0;
        _paperEntryFee = 0;
        SlPrice = 0;
        TpPrice = 0;
        HighestProfit = 0;
        _breakEvenActivated = false;
        PositionOpenTime = null;
        UpdateDrawdown();

        RecordTrade(pos, cp, pnl, reason);
        Log("[TEST] CLOSE {0} pnl:{1:F2} (수수료: -{2:F4}) | 잔고:{3:F2}", pos.Type, pnl, totalFee, CurrentBalance);

        try { OnTradeMarker?.Invoke(new TradeMarkerInfo("EXIT", cp, null, null, DateTime.Now)); } catch { /* subscriber error */ }
        try { OnPositionUpdate?.Invoke(_paperPosition, cp, 0); } catch { /* subscriber error */ }
        return Task.CompletedTask;
    }

    // ═══ 실전 트레이딩 ═══

    private async Task OpenPosition(string direction, double usdt, double sl, double tp, double price)
    {
        if (_api == null) return;
        double qty = Math.Round(usdt * _cfg.Leverage / price, _precision);
        if (qty <= 0) { Log("수량 부족"); return; }

        string side = direction == "L" ? "BUY" : "SELL";
        string closeSide = direction == "L" ? "SELL" : "BUY";

        bool ok = await _api.MarketOrder(_cfg.Symbol, side, qty);
        if (ok)
        {
            string label = direction == "L" ? "LONG" : "SHORT";
            Log("{0} {1} @ {2:F2} x{3}", label, qty, price, _cfg.Leverage);

            double slPrice, tpPrice;
            if (direction == "L")
            {
                slPrice = Math.Round(price * (1 - sl / 100), _pricePrecision);
                tpPrice = Math.Round(price * (1 + tp / 100), _pricePrecision);
            }
            else
            {
                slPrice = Math.Round(price * (1 + sl / 100), _pricePrecision);
                tpPrice = Math.Round(price * (1 - tp / 100), _pricePrecision);
            }

            SlPrice = slPrice;
            TpPrice = tpPrice;

            try
            {
                await _api.StopMarket(_cfg.Symbol, closeSide, qty, slPrice, _pricePrecision);
                Log("  SL: {0} (-{1}%)", slPrice, sl);
                await _api.TakeProfitMarket(_cfg.Symbol, closeSide, qty, tpPrice, _pricePrecision);
                Log("  TP: {0} (+{1}%)", tpPrice, tp);
            }
            catch (Exception ex)
            {
                Log("ERR: SL/TP 주문 실패 ({0}) — 수동 청산 필요!", ex.Message);
            }
            HighestProfit = 0;
            PositionOpenTime = DateTime.Now;
            _lastTradeTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _openPosType = direction;
            _openEntryPrice = price;
            _openQty = qty;

            try { OnTradeMarker?.Invoke(new TradeMarkerInfo(
                direction == "L" ? "ENTRY_LONG" : "ENTRY_SHORT",
                price, slPrice, tpPrice, DateTime.Now)); } catch { /* subscriber error */ }
        }
        else Log("{0} 주문 실패", direction);
    }

    private async Task ClosePosition(Position pos, double cp, string reason = "")
    {
        if (_api == null) return;
        string closeSide = pos.Type == "L" ? "SELL" : "BUY";
        await _api.CancelAllOrders(_cfg.Symbol);
        await _api.MarketOrder(_cfg.Symbol, closeSide, pos.Amount);

        double pnl = pos.EntryPrice > 0
            ? (pos.Type == "L" ? (cp - pos.EntryPrice) * pos.Amount : (pos.EntryPrice - cp) * pos.Amount)
            : 0;

        SlPrice = 0;
        TpPrice = 0;
        HighestProfit = 0;
        _breakEvenActivated = false;
        PositionOpenTime = null;
        _openPosType = "N"; _openEntryPrice = 0; _openQty = 0;
        UpdateDrawdown();

        RecordTrade(pos, cp, pnl, reason);
        Log("CLOSE {0} pnl:{1:F2}", pos.Type, pnl);

        try { OnTradeMarker?.Invoke(new TradeMarkerInfo("EXIT", cp, null, null, DateTime.Now)); } catch { /* subscriber error */ }
        try { OnPositionUpdate?.Invoke(new Position("N", 0, 0, 0), cp, 0); } catch { /* subscriber error */ }
    }

    // ═══ KYJ 분석 ═══

    private async Task AnalyzeKYJ(List<Candle> c15, List<Candle> c1h, double cp)
    {
        if (_pendingType != "N")
        {
            _pendingCount++;
            string crossName = _pendingType == "L" ? "골든크로스" : "데드크로스";
            OnEngineState?.Invoke($"{CoinTag} {crossName} 가격 확인 중 ({_pendingCount}/4)");
            if (_pendingCount > 4)
            {
                Log("{0} 신호 만료 — 가격 미확인", crossName);
                _signalSkipCycles = 5; // 동일 크로스 재탐지 방지 (5사이클)
                ResetPending();
            }
            else if (Strategy.ConfirmSignal(c15, _pendingType, _pendingPrice))
            {
                var (sl, tp, _atr) = Strategy.CalcSlTp(c15);
                double bal = IsTest ? CurrentBalance : await _api!.GetBalance();
                double inv = Math.Min(_cfg.TradeUsdt, bal * 0.9);
                if (inv >= 5)
                {
                    Log("{0} 확인완료! SL:{1}% TP:{2}%", crossName, sl, tp);
                    if (IsTest)
                        OpenPaperPosition(_pendingType, inv, sl, tp, cp);
                    else
                        await OpenPosition(_pendingType, inv, sl, tp, cp);
                }
                ResetPending();
            }
            else
            {
                Log("{0} 가격확인 {1}/4", crossName, _pendingCount);
            }
        }
        else if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastTradeTime > KyjCooldownSec)
        {
            // 만료 후 재탐지 방지
            if (_signalSkipCycles > 0)
            {
                _signalSkipCycles--;
                OnEngineState?.Invoke($"{CoinTag} 신호 만료 후 대기 ({_signalSkipCycles}사이클)");
                return;
            }

            OnEngineState?.Invoke($"{CoinTag} 분석 중...");
            var c4h = await _api!.GetKlines(_cfg.Symbol, "4h", 200);
            if (c4h == null) return;
            var sig = Strategy.CheckEmaSignal(c15, c1h, c4h, lookback: 3);
            OnAnalysisResult?.Invoke(sig);
            if (sig.Direction != "W" && sig.Score >= _cfg.AutoEntryScore)
            {
                _pendingType = sig.Direction;
                _pendingScore = sig.Score;
                _pendingCount = 0;
                _pendingPrice = cp;
                string crossName = sig.Direction == "L" ? "골든크로스" : "데드크로스";
                Log("신호감지! {0} score:{1} [{2}]", crossName, sig.Score, string.Join(" | ", sig.Reasons));
                OnEngineState?.Invoke($"{CoinTag} {crossName} 감지! ({sig.Score}점)");
            }
            else
            {
                string reason = sig.Reasons.Count > 0 ? sig.Reasons[0] : "기준 미달";
                OnEngineState?.Invoke($"{CoinTag} 대기 중 ({reason})");
            }
        }
        else
        {
            long elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastTradeTime;
            long remaining = KyjCooldownSec - elapsed;
            OnEngineState?.Invoke($"{CoinTag} 거래 후 휴식 ({remaining / 60}분 {remaining % 60}초 남음)");
        }
    }

    // ═══ 공통 ═══

    private void RecordTrade(Position pos, double cp, double pnl, string reason = "")
    {
        TotalTrades++;
        DailyTrades++;
        if (pnl > 0)
        {
            TotalWins++;
            ConsecWins++;
            ConsecLosses = 0;
            TotalGrossProfit += pnl;
        }
        else
        {
            TotalLosses++;
            ConsecLosses++;
            ConsecWins = 0;
            TotalGrossLoss += Math.Abs(pnl);
            if (ConsecLosses >= _cfg.MaxConsecLosses)
            {
                CooldownUntil = DateTime.Now.AddMinutes(_cfg.CooldownMinutes);
                Log("연속 {0}패! {1}분 쿨다운", ConsecLosses, _cfg.CooldownMinutes);
            }
        }
        TotalPnl += pnl;
        DailyPnl += pnl;
        HighestProfit = 0;
        try { OnTradeComplete?.Invoke(new TradeRecord
        {
            Time = DateTime.Now,
            Symbol = _cfg.Symbol,
            Side = pos.Type == "L" ? "LONG" : "SHORT",
            EntryPrice = pos.EntryPrice,
            ExitPrice = cp,
            Pnl = pnl,
            Balance = CurrentBalance,
            CloseReason = reason,
        }); } catch { /* subscriber error */ }
        try { OnStatsChanged?.Invoke(); } catch { /* subscriber error */ }
    }

    private void ResetPending()
    {
        _pendingType = "N";
        _pendingScore = 0;
        _pendingCount = 0;
        _pendingPrice = 0;
    }

    private void UpdateDrawdown()
    {
        if (CurrentBalance > PeakBalance)
            PeakBalance = CurrentBalance;
        if (PeakBalance > 0)
        {
            double dd = (PeakBalance - CurrentBalance) / PeakBalance * 100;
            if (dd > MaxDrawdownPct)
                MaxDrawdownPct = dd;
        }
    }

    private void InitLogFile()
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BtcTradingBot", "logs");
            Directory.CreateDirectory(logDir);
            var coin = _cfg.Symbol.Replace("USDT", "");
            var mode = _cfg.IsTestMode ? "test" : "classic";
            var logPath = Path.Combine(logDir, $"{mode}_{coin}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        }
        catch { /* 로그 파일 생성 실패해도 엔진은 동작 */ }
    }

    private void Log(string text)
    {
        var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {text}";
        try { OnLog?.Invoke(line); } catch { /* subscriber error */ }
        try { _logWriter?.WriteLine(line); } catch { }
    }

    private void Log(string format, params object[] args)
    {
        var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {string.Format(format, args)}";
        try { OnLog?.Invoke(line); } catch { /* subscriber error */ }
        try { _logWriter?.WriteLine(line); } catch { }
    }

    // ═══ 페이퍼 상태 저장/복원 ═══

    public PaperState CreatePaperState() => new()
    {
        Symbol = _cfg.Symbol,
        PositionType = _paperPosition.Type,
        Amount = _paperPosition.Amount,
        EntryPrice = _paperPosition.EntryPrice,
        SlPrice = _paperSlPrice,
        TpPrice = _paperTpPrice,
        Balance = CurrentBalance,
        EntryFee = _paperEntryFee,
        OpenTime = PositionOpenTime,
        TotalTrades = TotalTrades,
        TotalWins = TotalWins,
        TotalLosses = TotalLosses,
        TotalPnl = TotalPnl,
        PeakBalance = PeakBalance,
        MaxDrawdownPct = MaxDrawdownPct,
        TotalGrossProfit = TotalGrossProfit,
        TotalGrossLoss = TotalGrossLoss,
    };

    public void RestorePaperState(PaperState s)
    {
        _paperPosition = new Position(s.PositionType, s.Amount, s.EntryPrice, 0);
        _paperSlPrice = s.SlPrice;
        _paperTpPrice = s.TpPrice;
        SlPrice = s.SlPrice;
        TpPrice = s.TpPrice;
        CurrentBalance = s.Balance;
        _paperEntryFee = s.EntryFee;
        PositionOpenTime = s.OpenTime;
        TotalTrades = s.TotalTrades;
        TotalWins = s.TotalWins;
        TotalLosses = s.TotalLosses;
        TotalPnl = s.TotalPnl;
        PeakBalance = s.PeakBalance;
        MaxDrawdownPct = s.MaxDrawdownPct;
        TotalGrossProfit = s.TotalGrossProfit;
        TotalGrossLoss = s.TotalGrossLoss;
        _stateRestored = true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _api?.Dispose();
        _closeLock.Dispose();
        _logWriter?.Dispose();
        _logWriter = null;
    }
}
