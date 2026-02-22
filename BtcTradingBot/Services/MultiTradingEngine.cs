using System.IO;
using System.Text.Json;
using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

/// <summary>
/// USDM 멀티코인 트레이딩 엔진: 최대 4코인 동시 운용
/// 메인 루프: 종목 갱신 → 리스크 체크 → 포지션 관리 → 신규 진입
/// 테스트 모드: 페이퍼 트레이딩 / 실전 모드: Binance USDM 실거래
/// </summary>
public class MultiTradingEngine : IDisposable
{
    private readonly RiskManager _risk;
    private readonly SymbolSelector _selector;
    private BinanceApi? _api;
    private CancellationTokenSource? _cts;
    private StreamWriter? _logWriter;

    private const double FeeRate = 0.0004; // Python: taker_fee = 0.0004
    private const double Slippage = 0.00015; // Python: slippage per side
    private const int PollIntervalSec = 20;

    // 포지션 상태
    public Dictionary<string, PositionState> Positions { get; } = new();

    // 심볼별 정밀도 캐시
    private readonly Dictionary<string, int> _qtyPrecision = new();
    private readonly Dictionary<string, int> _pricePrecision = new();

    // 재진입 쿨다운 (청산 후 5분간 같은 심볼 진입 금지)
    private readonly Dictionary<string, DateTime> _cooldowns = new();
    private const int CooldownSeconds = 300; // 5분 = 1 캔들

    // 접근 불가 심볼 블랙리스트 (-4411 등 권한 오류)
    private readonly HashSet<string> _blacklist = new();

    // 포지션 상태 영속 저장
    private readonly string _positionsFile;

    // 하트비트 카운터
    private int _loopCount;

    // 통계
    public int TotalTrades { get; private set; }
    public int TotalWins { get; private set; }
    public int TotalLosses { get; private set; }
    public double TotalPnl { get; private set; }
    public double TotalGrossProfit { get; private set; }
    public double TotalGrossLoss { get; private set; }

    // 이벤트
    public event Action<string>? OnLog;
    public event Action? OnStatsChanged;
    public event Action<string, PositionState, double>? OnPositionUpdate; // symbol, pos, unrealizedPnl
    public event Action<List<SelectedSymbol>>? OnSymbolsRefreshed;
    public event Action<string>? OnEngineState;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public bool IsTestMode { get; init; }
    public double InitialBalance { get; init; } = 100;
    public int Leverage { get; init; } = 3;
    public string ApiKey { get; init; } = "";
    public string ApiSecret { get; init; } = "";

    public MultiTradingEngine()
    {
        _risk = new RiskManager();
        _selector = new SymbolSelector();

        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BtcTradingBot");
        Directory.CreateDirectory(appDir);
        _positionsFile = Path.Combine(appDir, "positions.json");
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _api = new BinanceApi(ApiKey, ApiSecret);

        // 로그 파일 초기화 (%APPDATA%/BtcTradingBot/logs/)
        InitLogFile();

        // 레버리지 반영
        _risk.Leverage = Leverage;

        // 실전 모드: 서버 시간 동기화
        if (!IsTestMode)
        {
            await _api.SyncServerTime();
            Log("서버 시간 동기화 완료");
        }

        // 초기 잔고
        _risk.Equity = IsTestMode ? InitialBalance : await GetEquity();
        _risk.PeakEquity = _risk.Equity;
        _risk.DayStartEquity = _risk.Equity;

        string mode = IsTestMode ? "테스트" : "실전";
        Log("멀티코인 엔진 시작 [{0}] (잔고: {1:N2} USDT, 레버리지: {2}x)", mode, _risk.Equity, Leverage);

        // 실전 모드: 기존 포지션 복원
        if (!IsTestMode)
        {
            OnEngineState?.Invoke("기존 포지션 동기화...");
            await SyncExistingPositions();

            // 시작 시 모든 Algo 주문 전체 취소 (이전 중복 주문 포함)
            // 1) 심볼 없이 전체 조회+취소 시도
            int cleaned = await _api.CancelAllAlgoOrders();
            // 2) 현재 포지션 심볼별 정리
            foreach (var (sym, pos) in Positions)
            {
                if (!pos.IsOpen) continue;
                try { await _api.CancelAllOrders(sym); } catch { }
                pos.SlAlgoId = 0;
                pos.TpAlgoId = 0;
            }
            // 3) positions.json에 기록된 과거 심볼도 정리 (청산된 포지션의 유령 주문)
            var saved = LoadPositions();
            foreach (var sym in saved.Keys)
            {
                if (Positions.ContainsKey(sym) && Positions[sym].IsOpen) continue;
                try { await _api.CancelAllOrders(sym); cleaned++; } catch { }
            }
            if (cleaned > 0) Log("기존 주문 {0}건 정리 완료", cleaned);
        }

        OnEngineState?.Invoke("종목 선택 중...");

        // 초기 종목 선택
        await RefreshSymbols(force: true);

        int consecErrors = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await LoopOnce();
                consecErrors = 0;
                await Task.Delay(PollIntervalSec * 1000, _cts.Token);
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                consecErrors++;
                int delay = Math.Min(30_000 * consecErrors, 180_000);
                Log("ERR: {0} (retry #{1})", ex.Message, consecErrors);
                if (consecErrors >= 10)
                {
                    Log("[FATAL] 연속 {0}회 오류 — 엔진 중지", consecErrors);
                    OnEngineState?.Invoke("연속 오류로 중지됨");
                    break;
                }
                try { await Task.Delay(delay, _cts.Token); }
                catch (TaskCanceledException) { break; }
            }
        }

        SavePositions();
        Log("멀티코인 엔진 종료");
        _api?.Dispose();
        _api = null;
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>시작 시 거래소의 기존 포지션을 복원하여 관리 재개</summary>
    private async Task SyncExistingPositions()
    {
        if (_api == null) return;

        // 저장된 포지션 메타데이터 로드 (StrategyTag, Highest/Lowest 등)
        var saved = LoadPositions();

        try
        {
            var positions = await _api.GetAllPositions();
            if (positions.Count == 0)
            {
                Log("기존 포지션 없음");
                return;
            }

            foreach (var ep in positions)
            {
                if (string.IsNullOrEmpty(ep.Symbol)) continue;

                // 미체결 주문에서 SL/TP 가격 복원
                double slPrice = 0, tpPrice = 0;
                try
                {
                    var orders = await _api.GetOpenOrders(ep.Symbol);
                    foreach (var o in orders)
                    {
                        if (o.Type.Contains("STOP") && !o.Type.Contains("TAKE"))
                            slPrice = o.StopPrice;
                        else if (o.Type.Contains("TAKE_PROFIT"))
                            tpPrice = o.StopPrice;
                    }
                }
                catch { /* 주문 조회 실패 시 SL/TP 없이 진행 */ }

                // 정밀도 캐시
                try
                {
                    if (!_pricePrecision.ContainsKey(ep.Symbol))
                        _pricePrecision[ep.Symbol] = await _api.GetPricePrecision(ep.Symbol);
                }
                catch { }

                double margin = ep.Amount * ep.EntryPrice / Leverage;

                // 저장된 메타데이터가 있으면 복원, 없으면 기본값
                saved.TryGetValue(ep.Symbol, out var meta);

                var pos = new PositionState
                {
                    Symbol = ep.Symbol,
                    Side = ep.Type, // "L" or "S"
                    EntryPrice = ep.EntryPrice,
                    Amount = ep.Amount,
                    StopPrice = meta?.StopPrice > 0 ? meta.StopPrice : slPrice,
                    TpPrice = meta?.TpPrice > 0 ? meta.TpPrice : tpPrice,
                    Highest = meta?.Highest > 0 ? meta.Highest : ep.EntryPrice,
                    Lowest = meta?.Lowest > 0 ? meta.Lowest : ep.EntryPrice,
                    StrategyTag = meta?.StrategyTag ?? "DonchianBreakout",
                    OpenTime = meta?.OpenTime ?? DateTime.Now,
                    MarginUsed = margin,
                    // SL/TP algoId는 시작 시 cleanup 후 ensure_brackets에서 재설정
                };
                Positions[ep.Symbol] = pos;

                string label = ep.Type == "L" ? "LONG" : "SHORT";
                int pricePrec = _pricePrecision.GetValueOrDefault(ep.Symbol, 2);

                // SL/TP 없으면 ATR 기반으로 계산 + 거래소에 주문
                if (slPrice == 0 || tpPrice == 0)
                {
                    try
                    {
                        var candles = await _api.GetKlines(ep.Symbol, "5m", 300);
                        if (candles != null && candles.Count > 50)
                        {
                            double atr = IndicatorsUsdm.AtrValue(candles);
                            double stopMult = StrategyUsdm.StopAtrMultTrend; // 2.8
                            double tpR = StrategyUsdm.TpRTrend; // 2.2

                            if (slPrice == 0)
                            {
                                slPrice = ep.Type == "L"
                                    ? ep.EntryPrice - stopMult * atr
                                    : ep.EntryPrice + stopMult * atr;
                                pos.StopPrice = slPrice;
                            }
                            if (tpPrice == 0)
                            {
                                double stopDist = Math.Abs(ep.EntryPrice - slPrice);
                                tpPrice = ep.Type == "L"
                                    ? ep.EntryPrice + tpR * stopDist
                                    : ep.EntryPrice - tpR * stopDist;
                                pos.TpPrice = tpPrice;
                            }

                            // Algo Order API로 SL/TP 주문
                            string exitSide = ep.Type == "L" ? "SELL" : "BUY";
                            try
                            {
                                pos.SlAlgoId = await _api.StopMarket(ep.Symbol, exitSide, ep.Amount, slPrice, pricePrec);
                                Log("[{0}] SL 보호 주문 설정: {1} (id:{2})", ep.Symbol.Replace("USDT", ""), slPrice.ToString($"N{pricePrec}"), pos.SlAlgoId);
                            }
                            catch (Exception ex) { Log("[{0}] SL 주문 실패: {1}", ep.Symbol.Replace("USDT", ""), ex.Message); }

                            try
                            {
                                pos.TpAlgoId = await _api.TakeProfitMarket(ep.Symbol, exitSide, ep.Amount, tpPrice, pricePrec);
                                Log("[{0}] TP 보호 주문 설정: {1} (id:{2})", ep.Symbol.Replace("USDT", ""), tpPrice.ToString($"N{pricePrec}"), pos.TpAlgoId);
                            }
                            catch (Exception ex) { Log("[{0}] TP 주문 실패: {1}", ep.Symbol.Replace("USDT", ""), ex.Message); }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("[{0}] SL/TP 복구 실패: {1}", ep.Symbol.Replace("USDT", ""), ex.Message);
                    }
                }

                string slStr = slPrice > 0 ? $"SL:{slPrice.ToString($"N{pricePrec}")}" : "SL:없음";
                string tpStr = tpPrice > 0 ? $"TP:{tpPrice.ToString($"N{pricePrec}")}" : "TP:없음";
                Log("[{0}] 기존 포지션 복원: {1} {2} @ {3:N2} ({4} {5})",
                    ep.Symbol.Replace("USDT", ""), label, ep.Amount, ep.EntryPrice, slStr, tpStr);

                OnPositionUpdate?.Invoke(ep.Symbol, pos, ep.UnrealizedPnl);
            }

            Log("기존 포지션 {0}개 동기화 완료", positions.Count);
        }
        catch (Exception ex)
        {
            Log("포지션 동기화 오류: {0}", ex.Message);
        }
    }

    private async Task<double> GetEquity()
    {
        if (IsTestMode) return _risk.Equity;
        if (_api == null) return 0;
        return await _api.GetBalance();
    }

    private async Task LoopOnce()
    {
        if (_api == null) return;

        // 일일 리셋
        _risk.CheckDayReset();

        // 서버 시간 재동기화 (1시간마다, drift 방지)
        if (!IsTestMode)
            await _api.EnsureTimeSynced();

        // 잔고 갱신
        if (!IsTestMode)
            _risk.Equity = await GetEquity();

        // 킬스위치
        if (_risk.CheckKillSwitch())
        {
            Log("[KILL] 킬스위치 발동! (peak {0:N2} → {1:N2})", _risk.PeakEquity, _risk.Equity);
            OnEngineState?.Invoke("킬스위치 발동 — 전체 중지");
            await CloseAllPositions("킬스위치");
            _cts?.Cancel();
            return;
        }

        // 종목 갱신
        await RefreshSymbols();

        // 포지션 관리 (SL/TP 체크 + 트레일링)
        foreach (var (symbol, pos) in Positions.ToList())
        {
            if (!pos.IsOpen) continue;
            await ManagePosition(symbol, pos);
        }

        // 신규 진입
        if (_risk.CanOpenPosition(Positions))
        {
            await TryEntries();
        }
        else
        {
            int openCount = Positions.Values.Count(p => p.IsOpen);
            string reason = _risk.IsKillSwitchTriggered ? "킬스위치"
                : _risk.IsDailyHalted ? "일일 제한"
                : $"최대 포지션 ({openCount}/{_risk.MaxPositions})";
            OnEngineState?.Invoke($"진입 불가: {reason}");
        }

        OnStatsChanged?.Invoke();

        // 하트비트: 매 사이클 상태 로그 (엔진 생존 확인용)
        _loopCount++;
        if (_loopCount % 15 == 1) // 15사이클(~5분)마다 1회 로그
        {
            int openCount = Positions.Values.Count(p => p.IsOpen);
            var openSymbols = string.Join(",", Positions.Where(p => p.Value.IsOpen).Select(p => p.Key.Replace("USDT", "")));
            Log("[♥] 사이클#{0} 잔고:{1:N2} 포지션:{2} [{3}]",
                _loopCount, _risk.Equity, openCount, openSymbols);
        }
    }

    private async Task RefreshSymbols(bool force = false)
    {
        if (!force && !_selector.NeedsRefresh) return;
        if (_api == null) return;

        try
        {
            var symbols = await _selector.SelectSymbols(_api);
            OnSymbolsRefreshed?.Invoke(symbols);
            Log("종목 갱신 ({0}개): {1}", symbols.Count, string.Join(", ", symbols.Select(s => s.Symbol.BaseAsset)));
            OnEngineState?.Invoke($"종목: {symbols.Count}개 선택됨");
        }
        catch (Exception ex)
        {
            Log("종목 갱신 실패: {0}", ex.Message);
        }
    }

    private async Task ManagePosition(string symbol, PositionState pos)
    {
        if (_api == null) return;

        try
        {
            double cp = await _api.GetPrice(symbol);
            if (cp <= 0) return;

            // 실전 모드: 거래소 포지션 확인 (SL/TP 체결 감지)
            if (!IsTestMode)
            {
                var exchangePos = await _api.GetPosition(symbol);
                if (exchangePos.Type == "N")
                {
                    // 거래소에서 포지션 없음 → SL/TP 체결됨
                    RecordLiveClose(symbol, pos, cp, "SL/TP");
                    return;
                }
            }

            // 미실현 PnL 계산
            double unrealizedPnl = pos.Side == "L"
                ? (cp - pos.EntryPrice) * pos.Amount
                : (pos.EntryPrice - cp) * pos.Amount;
            if (IsTestMode)
                unrealizedPnl -= cp * pos.Amount * FeeRate;

            OnPositionUpdate?.Invoke(symbol, pos, unrealizedPnl);

            // SL/TP 체크 (테스트 모드만 — 실전은 거래소가 처리)
            if (IsTestMode)
            {
                bool slHit = pos.Side == "L" ? cp <= pos.StopPrice : cp >= pos.StopPrice;
                bool tpHit = pos.Side == "L" ? cp >= pos.TpPrice : cp <= pos.TpPrice;

                if (slHit)
                {
                    Log("[{0}] SL 트리거 @ {1:N2}", symbol, pos.StopPrice);
                    ClosePaperPosition(symbol, pos, pos.StopPrice, "SL");
                    return;
                }
                if (tpHit)
                {
                    Log("[{0}] TP 트리거 @ {1:N2}", symbol, pos.TpPrice);
                    ClosePaperPosition(symbol, pos, pos.TpPrice, "TP");
                    return;
                }
            }

            // === ensure_brackets: 내부 상태 추적 + 주기적 API 검증 ===
            bool changedStop = false;
            int pricePrec = _pricePrecision.GetValueOrDefault(symbol, 2);
            string exitSide = pos.Side == "L" ? "SELL" : "BUY";

            // 트레일링 스탑 (DonchianBreakout만, 5분 이후)
            if (pos.StrategyTag == "DonchianBreakout"
                && (DateTime.Now - pos.OpenTime).TotalSeconds >= 300)
            {
                var candles = await _api.GetKlines(symbol, "5m", 300);
                if (candles != null && candles.Count > 50)
                {
                    double lastHigh = candles[^2].High;
                    double lastLow = candles[^2].Low;
                    double atr = IndicatorsUsdm.AtrValue(candles);

                    double? newSl = StrategyUsdm.UpdateTrailingStop(pos, lastHigh, lastLow, atr);
                    if (newSl.HasValue)
                    {
                        pos.StopPrice = newSl.Value;
                        changedStop = true;
                        SavePositions();
                        Log("[{0}] 트레일링 SL → {1:N2}", symbol, newSl.Value);
                    }
                }
            }

            // === ensure_brackets (실전 모드) ===
            // 내부 플래그만 신뢰 (Algo Order API는 GetOpenOrders로 조회 불가)
            // SL/TP 체결은 GetPosition()으로 포지션 소멸 감지
            if (!IsTestMode)
            {
                // SL 변경됨 → 기존 SL/TP를 algoId로 직접 취소 후 새로 설정
                if (changedStop)
                {
                    // SL 취소
                    if (pos.SlAlgoId > 0)
                    {
                        try { await _api.CancelAlgoOrder(pos.SlAlgoId); }
                        catch { }
                        pos.SlAlgoId = 0;
                    }
                    // TP도 취소 (SL과 함께 재설정)
                    if (pos.TpAlgoId > 0)
                    {
                        try { await _api.CancelAlgoOrder(pos.TpAlgoId); }
                        catch { }
                        pos.TpAlgoId = 0;
                    }
                }

                // SL 없으면 생성
                if (pos.SlAlgoId <= 0 && pos.StopPrice > 0)
                {
                    try
                    {
                        pos.SlAlgoId = await _api.StopMarket(symbol, exitSide, pos.Amount, pos.StopPrice, pricePrec);
                        if (changedStop)
                            Log("[{0}] SL 갱신: {1} (id:{2})", symbol, pos.StopPrice.ToString($"N{pricePrec}"), pos.SlAlgoId);
                        else
                            Log("[{0}] SL 복구 (id:{1})", symbol, pos.SlAlgoId);
                    }
                    catch (Exception ex) when (ex.Message.Contains("-2021"))
                    {
                        Log("[{0}] SL 실패 (-2021, 다음 사이클 재시도)", symbol);
                    }
                    catch (Exception ex)
                    {
                        Log("[{0}] SL 주문 오류: {1}", symbol, ex.Message);
                    }
                }

                // TP 없으면 생성
                if (pos.TpAlgoId <= 0 && pos.TpPrice > 0)
                {
                    try
                    {
                        pos.TpAlgoId = await _api.TakeProfitMarket(symbol, exitSide, pos.Amount, pos.TpPrice, pricePrec);
                        Log("[{0}] TP 설정 (id:{1})", symbol, pos.TpAlgoId);
                    }
                    catch (Exception ex)
                    {
                        Log("[{0}] TP 주문 오류: {1}", symbol, ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("[{0}] 포지션 관리 오류: {1}", symbol, ex.Message);
        }
    }

    private async Task TryEntries()
    {
        if (_api == null) return;

        var symbols = _selector.GetCurrent();
        var existingSymbols = new HashSet<string>(Positions.Where(p => p.Value.IsOpen).Select(p => p.Key));

        foreach (var sel in symbols)
        {
            if (!_risk.CanOpenPosition(Positions)) break;
            if (existingSymbols.Contains(sel.Symbol.Symbol)) continue;

            // 블랙리스트 체크 (접근 불가 심볼)
            if (_blacklist.Contains(sel.Symbol.Symbol)) continue;

            // 쿨다운 체크: 청산 후 5분간 재진입 금지
            if (_cooldowns.TryGetValue(sel.Symbol.Symbol, out var cooldownUntil) && DateTime.UtcNow < cooldownUntil)
                continue;

            try
            {
                // Python: fetch_recent(sym, bars=600)
                var candles = await _api.GetKlines(sel.Symbol.Symbol, "5m", 600);
                if (candles == null || candles.Count < 250) continue;

                var signal = StrategyUsdm.Analyze(candles);
                if (signal.Direction == "W") continue;

                // Python: last = fetch_ticker_last(ex, sym)
                double last = await _api.GetPrice(sel.Symbol.Symbol);
                if (last <= 0 || signal.StopDistance <= 0) continue;

                // Python: entry_price = last * (1 + slippage) for long, (1 - slippage) for short
                double entryPrice = signal.Direction == "L"
                    ? last * (1 + Slippage)
                    : last * (1 - Slippage);

                // Python: SL/TP를 실제 entry_price 기준으로 재계산
                double atr = IndicatorsUsdm.AtrValue(candles);
                double stopMult, tpR;
                if (signal.StrategyTag == "DonchianBreakout")
                {
                    stopMult = StrategyUsdm.StopAtrMultTrend;
                    tpR = StrategyUsdm.TpRTrend;
                }
                else
                {
                    stopMult = StrategyUsdm.StopAtrMultMr;
                    tpR = StrategyUsdm.TpRMr;
                }

                double stopPrice, tpPrice, stopDistance;
                if (signal.Direction == "L")
                {
                    stopPrice = entryPrice - stopMult * atr;
                    stopDistance = entryPrice - stopPrice;
                    tpPrice = entryPrice + tpR * stopDistance;
                }
                else
                {
                    stopPrice = entryPrice + stopMult * atr;
                    stopDistance = stopPrice - entryPrice;
                    tpPrice = entryPrice - tpR * stopDistance;
                }

                if (stopDistance <= 0) continue;

                // 정밀도 캐시
                if (!_pricePrecision.ContainsKey(sel.Symbol.Symbol))
                    _pricePrecision[sel.Symbol.Symbol] = await _api.GetPricePrecision(sel.Symbol.Symbol);
                if (!_qtyPrecision.ContainsKey(sel.Symbol.Symbol))
                    _qtyPrecision[sel.Symbol.Symbol] = sel.Symbol.QuantityPrecision;

                int qtyPrec = _qtyPrecision[sel.Symbol.Symbol];
                int pricePrec = _pricePrecision[sel.Symbol.Symbol];

                var (qty, margin) = _risk.CalcPositionSize(entryPrice, stopDistance, qtyPrec, Positions);
                if (qty <= 0) continue;

                // 진입 로그 (가격 정밀도 적용)
                string pFmt = $"N{pricePrec}";
                string label = signal.Direction == "L" ? "LONG" : "SHORT";
                Log("[{0}] {1} {2} @ {3} (SL:{4} TP:{5}) [{6}]",
                    sel.Symbol.BaseAsset, label, qty,
                    entryPrice.ToString(pFmt), stopPrice.ToString(pFmt),
                    tpPrice.ToString(pFmt), signal.StrategyTag);

                if (IsTestMode)
                    OpenPaperPosition(sel.Symbol.Symbol, signal.Direction, signal.StrategyTag,
                        qty, entryPrice, stopPrice, tpPrice, margin);
                else
                    await OpenLivePosition(sel.Symbol.Symbol, signal.Direction, signal.StrategyTag,
                        qty, entryPrice, stopPrice, tpPrice, margin, pricePrec);

                // 루프 내 중복 방지
                existingSymbols.Add(sel.Symbol.Symbol);
                OnEngineState?.Invoke($"{sel.Symbol.BaseAsset} {label} 진입!");
            }
            catch (Exception ex)
            {
                // -4411: 약관 미동의 등 접근 불가 → 블랙리스트
                if (ex.Message.Contains("-4411") || ex.Message.Contains("-4412"))
                {
                    _blacklist.Add(sel.Symbol.Symbol);
                    Log("[{0}] 접근 불가 — 블랙리스트 추가: {1}", sel.Symbol.BaseAsset, ex.Message);
                }
                else
                {
                    Log("[{0}] 분석 오류: {1}", sel.Symbol.BaseAsset, ex.Message);
                }
            }
        }
    }

    // ═══ 실전 거래 ═══

    private async Task OpenLivePosition(string symbol, string side, string strategyTag,
        double qty, double entryPrice, double stopPrice, double tpPrice, double margin, int pricePrec)
    {
        if (_api == null) return;

        // 마진 타입 + 레버리지 설정
        try { await _api.SetMarginType(symbol, "ISOLATED"); } catch { /* already set */ }
        try { await _api.SetLeverage(symbol, Leverage); } catch { /* already set */ }

        // 시장가 진입
        string orderSide = side == "L" ? "BUY" : "SELL";
        bool success = await _api.MarketOrder(symbol, orderSide, qty);
        if (!success)
        {
            Log("[{0}] 진입 주문 실패!", symbol);
            return;
        }

        // 실제 체결 정보 조회
        var exchangePos = await _api.GetPosition(symbol);
        double actualEntry = exchangePos.EntryPrice > 0 ? exchangePos.EntryPrice : entryPrice;
        double actualQty = exchangePos.Amount > 0 ? exchangePos.Amount : qty;

        // 포지션 기록
        var pos = new PositionState
        {
            Symbol = symbol,
            Side = side,
            EntryPrice = actualEntry,
            Amount = actualQty,
            StopPrice = stopPrice,
            TpPrice = tpPrice,
            Highest = actualEntry,
            Lowest = actualEntry,
            StrategyTag = strategyTag,
            OpenTime = DateTime.Now,
            MarginUsed = margin,
        };
        Positions[symbol] = pos;

        Log("[{0}] 실전 진입 체결 (체결가: {1}, 수량: {2})", symbol, actualEntry, actualQty);

        // SL 주문
        string exitSide = side == "L" ? "SELL" : "BUY";
        try
        {
            pos.SlAlgoId = await _api.StopMarket(symbol, exitSide, actualQty, stopPrice, pricePrec);
            Log("[{0}] SL 설정 (id:{1})", symbol, pos.SlAlgoId);
        }
        catch (Exception ex)
        {
            Log("[{0}] SL 주문 오류: {1}", symbol, ex.Message);
        }

        // TP 주문
        try
        {
            pos.TpAlgoId = await _api.TakeProfitMarket(symbol, exitSide, actualQty, tpPrice, pricePrec);
            Log("[{0}] TP 설정 (id:{1})", symbol, pos.TpAlgoId);
        }
        catch (Exception ex)
        {
            Log("[{0}] TP 주문 오류: {1}", symbol, ex.Message);
        }

        SavePositions();
        OnPositionUpdate?.Invoke(symbol, pos, 0);
        OnStatsChanged?.Invoke();
    }

    /// <summary>거래소에서 포지션이 청산된 것을 감지했을 때 기록</summary>
    private void RecordLiveClose(string symbol, PositionState pos, double lastPrice, string reason)
    {
        // 대략적 PnL (실제 잔고는 GetBalance()에서 갱신)
        double pnl = pos.Side == "L"
            ? (lastPrice - pos.EntryPrice) * pos.Amount
            : (pos.EntryPrice - lastPrice) * pos.Amount;

        _risk.RecordPnl(pnl);

        TotalTrades++;
        TotalPnl += pnl;
        if (pnl > 0) { TotalWins++; TotalGrossProfit += pnl; }
        else { TotalLosses++; TotalGrossLoss += Math.Abs(pnl); }

        string label = pos.Side == "L" ? "LONG" : "SHORT";
        Log("[{0}] CLOSE {1} pnl:{2:+0.00;-0.00} ({3}) 잔고:{4:N2}",
            symbol.Replace("USDT", ""), label, pnl, reason, _risk.Equity);

        // 재진입 쿨다운 설정 (5분)
        _cooldowns[symbol] = DateTime.UtcNow.AddSeconds(CooldownSeconds);

        // 포지션 리셋
        Positions[symbol] = new PositionState { Symbol = symbol };
        SavePositions();
        OnPositionUpdate?.Invoke(symbol, Positions[symbol], 0);
        OnStatsChanged?.Invoke();
    }

    private async Task CloseLivePosition(string symbol, PositionState pos, double cp, string reason)
    {
        if (_api == null) return;

        try
        {
            // 기존 SL/TP Algo 주문 취소
            if (pos.SlAlgoId > 0) try { await _api.CancelAlgoOrder(pos.SlAlgoId); } catch { }
            if (pos.TpAlgoId > 0) try { await _api.CancelAlgoOrder(pos.TpAlgoId); } catch { }
            // 혹시 남은 일반 주문도 정리
            try { await _api.CancelAllOrders(symbol); } catch { }

            // 시장가 청산
            string exitSide = pos.Side == "L" ? "SELL" : "BUY";
            await _api.MarketOrder(symbol, exitSide, pos.Amount);
        }
        catch (Exception ex)
        {
            Log("[{0}] 실전 청산 주문 오류: {1}", symbol, ex.Message);
        }

        RecordLiveClose(symbol, pos, cp, reason);
    }

    // ═══ 페이퍼 트레이딩 ═══

    private void OpenPaperPosition(string symbol, string side, string strategyTag,
        double qty, double entryPrice, double stopPrice, double tpPrice, double margin)
    {
        double entryFee = entryPrice * qty * FeeRate;
        _risk.Equity -= entryFee;

        var pos = new PositionState
        {
            Symbol = symbol,
            Side = side,
            EntryPrice = entryPrice,
            Amount = qty,
            StopPrice = stopPrice,
            TpPrice = tpPrice,
            // Python: highest=entry_price, lowest=entry_price
            Highest = entryPrice,
            Lowest = entryPrice,
            StrategyTag = strategyTag,
            OpenTime = DateTime.Now,
            EntryFee = entryFee,
            MarginUsed = margin,
        };

        Positions[symbol] = pos;
        OnPositionUpdate?.Invoke(symbol, pos, 0);
        OnStatsChanged?.Invoke();
    }

    private void ClosePaperPosition(string symbol, PositionState pos, double exitPrice, string reason)
    {
        double pnl = pos.Side == "L"
            ? (exitPrice - pos.EntryPrice) * pos.Amount
            : (pos.EntryPrice - exitPrice) * pos.Amount;

        double exitFee = exitPrice * pos.Amount * FeeRate;
        pnl -= (pos.EntryFee + exitFee);

        _risk.Equity += pnl + pos.EntryFee; // 잔고 복원 + PnL
        _risk.RecordPnl(pnl);

        TotalTrades++;
        TotalPnl += pnl;
        if (pnl > 0) { TotalWins++; TotalGrossProfit += pnl; }
        else { TotalLosses++; TotalGrossLoss += Math.Abs(pnl); }

        string label = pos.Side == "L" ? "LONG" : "SHORT";
        Log("[{0}] CLOSE {1} pnl:{2:+0.00;-0.00} ({3}) 잔고:{4:N2}",
            symbol.Replace("USDT", ""), label, pnl, reason, _risk.Equity);

        // 재진입 쿨다운 설정 (5분)
        _cooldowns[symbol] = DateTime.UtcNow.AddSeconds(CooldownSeconds);

        // 포지션 리셋
        Positions[symbol] = new PositionState { Symbol = symbol };
        OnPositionUpdate?.Invoke(symbol, Positions[symbol], 0);
        OnStatsChanged?.Invoke();
    }

    // ═══ 공통 ═══

    /// <summary>특정 심볼 수동 시장가 청산</summary>
    public async Task ManualClosePosition(string symbol)
    {
        if (!Positions.TryGetValue(symbol, out var pos) || !pos.IsOpen)
        {
            Log("[{0}] 수동 청산: 포지션 없음", symbol);
            return;
        }

        try
        {
            double cp = _api != null ? await _api.GetPrice(symbol) : pos.EntryPrice;
            if (IsTestMode)
                ClosePaperPosition(symbol, pos, cp, "수동청산");
            else
                await CloseLivePosition(symbol, pos, cp, "수동청산");
            Log("[{0}] 수동 시장가 청산 완료", symbol);
        }
        catch (Exception ex)
        {
            Log("[{0}] 수동 청산 실패: {1}", symbol, ex.Message);
        }
    }

    private async Task CloseAllPositions(string reason)
    {
        foreach (var (symbol, pos) in Positions.ToList())
        {
            if (!pos.IsOpen) continue;
            try
            {
                double cp = _api != null ? await _api.GetPrice(symbol) : pos.EntryPrice;
                if (IsTestMode)
                    ClosePaperPosition(symbol, pos, cp, reason);
                else
                    await CloseLivePosition(symbol, pos, cp, reason);
            }
            catch (Exception ex)
            {
                Log("[{0}] 청산 실패: {1}", symbol, ex.Message);
            }
        }
    }

    public double GetEquityWithUnrealized()
    {
        double unrealized = 0;
        // 미실현 PnL은 OnPositionUpdate에서 실시간 갱신하므로 여기서는 잔고만 반환
        return _risk.Equity + unrealized;
    }

    public double GetEquityRaw() => _risk.Equity;
    public double GetPeakEquity() => _risk.PeakEquity;
    public bool IsDailyHalted() => _risk.IsDailyHalted;
    public double GetDailyPnl() => _risk.DailyPnl;

    private void InitLogFile()
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BtcTradingBot", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"multi_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        }
        catch { /* 로그 파일 생성 실패해도 엔진은 동작 */ }
    }

    public string? LogFilePath => (_logWriter?.BaseStream as FileStream)?.Name;

    private void Log(string text)
    {
        var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {text}";
        OnLog?.Invoke(line);
        try { _logWriter?.WriteLine(line); } catch { }
    }

    private void Log(string fmt, params object[] args)
    {
        var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {string.Format(fmt, args)}";
        OnLog?.Invoke(line);
        try { _logWriter?.WriteLine(line); } catch { }
    }

    // ═══ 포지션 상태 영속 저장/로드 ═══

    private void SavePositions()
    {
        try
        {
            var data = Positions
                .Where(p => p.Value.IsOpen)
                .ToDictionary(p => p.Key, p => new PositionSaveData
                {
                    Side = p.Value.Side,
                    StrategyTag = p.Value.StrategyTag,
                    StopPrice = p.Value.StopPrice,
                    TpPrice = p.Value.TpPrice,
                    Highest = p.Value.Highest,
                    Lowest = p.Value.Lowest,
                    OpenTime = p.Value.OpenTime,
                    SlAlgoId = p.Value.SlAlgoId,
                    TpAlgoId = p.Value.TpAlgoId,
                });
            File.WriteAllText(_positionsFile,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 저장 실패 무시 */ }
    }

    private Dictionary<string, PositionSaveData> LoadPositions()
    {
        try
        {
            if (File.Exists(_positionsFile))
            {
                var json = File.ReadAllText(_positionsFile);
                return JsonSerializer.Deserialize<Dictionary<string, PositionSaveData>>(json)
                    ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Dispose()
    {
        SavePositions();
        _cts?.Cancel();
        _cts?.Dispose();
        _api?.Dispose();
        _logWriter?.Dispose();
        _logWriter = null;
    }
}

public class PositionSaveData
{
    public string Side { get; set; } = "N";
    public string StrategyTag { get; set; } = "";
    public double StopPrice { get; set; }
    public double TpPrice { get; set; }
    public double Highest { get; set; }
    public double Lowest { get; set; }
    public DateTime OpenTime { get; set; }
    public long SlAlgoId { get; set; }
    public long TpAlgoId { get; set; }
}
