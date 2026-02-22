using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

public class BinanceApi : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string BaseUrl = "https://fapi.binance.com";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly byte[] _secretBytes;
    private readonly bool _readOnly;
    private long _timeOffset;
    private DateTime _lastTimeSync = DateTime.MinValue;

    public BinanceApi(string apiKey, string apiSecret)
    {
        _apiKey = apiKey;
        _secretBytes = Encoding.UTF8.GetBytes(apiSecret);
        _readOnly = string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret);

        // 24시간 안정성: DNS 변경 대응 + 커넥션 풀 재활용
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", apiKey);
    }

    /// <summary>주문/계정 변경 메서드 호출 전 가드. 테스트 모드(빈 키)에서 절대 실행 못함.</summary>
    private void EnsureAuthenticated()
    {
        if (_readOnly)
            throw new InvalidOperationException("READ-ONLY 모드: API 키 없이 주문/계정 변경 불가. 테스트 모드에서 실전 API가 호출되었습니다.");
    }

    public async Task SyncServerTime()
    {
        try
        {
            var json = await GetJson("/fapi/v1/time").ConfigureAwait(false);
            if (json.TryGetProperty("serverTime", out var st))
                _timeOffset = st.GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastTimeSync = DateTime.UtcNow;
        }
        catch { _timeOffset = 0; }
    }

    /// <summary>1시간마다 자동 재동기화 (24시간 운영 시 drift 방지)</summary>
    public async Task EnsureTimeSynced()
    {
        if ((DateTime.UtcNow - _lastTimeSync).TotalMinutes >= 30)
            await SyncServerTime();
    }

    private long Timestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffset;

    private string Sign(string queryString)
    {
        using var hmac = new HMACSHA256(_secretBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
        return Convert.ToHexString(hash).ToLower();
    }

    private Task<JsonElement> GetJson(string path, Dictionary<string, string>? parms = null, bool signed = false)
        => WithRetry(async () =>
    {
        var p = parms != null ? new Dictionary<string, string>(parms) : new();
        if (signed)
        {
            await EnsureTimeSynced();
            p["timestamp"] = Timestamp().ToString();
            var qs = BuildQuery(p);
            p["signature"] = Sign(qs);
        }
        var query = BuildQuery(p);
        var url = $"{BaseUrl}{path}?{query}";
        using var resp = await _http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        ThrowOnApiError(root);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Response status code does not indicate success: {(int)resp.StatusCode} ({resp.ReasonPhrase}).", null, resp.StatusCode);
        return root;
    });

    private Task<JsonElement> PostJson(string path, Dictionary<string, string>? parms = null)
        => WithRetry(async () =>
    {
        var p = parms != null ? new Dictionary<string, string>(parms) : new();
        await EnsureTimeSynced();
        p["timestamp"] = Timestamp().ToString();
        var qs = BuildQuery(p);
        p["signature"] = Sign(qs);
        var query = BuildQuery(p);
        var url = $"{BaseUrl}{path}?{query}";
        using var resp = await _http.PostAsync(url, null);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        ThrowOnApiError(root);
        return root;
    });

    private Task<JsonElement> DeleteJson(string path, Dictionary<string, string>? parms = null)
        => WithRetry(async () =>
    {
        var p = parms != null ? new Dictionary<string, string>(parms) : new();
        await EnsureTimeSynced();
        p["timestamp"] = Timestamp().ToString();
        var qs = BuildQuery(p);
        p["signature"] = Sign(qs);
        var query = BuildQuery(p);
        var url = $"{BaseUrl}{path}?{query}";
        using var resp = await _http.DeleteAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        ThrowOnApiError(root);
        return root;
    });

    private void ThrowOnApiError(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty("code", out var code) &&
            json.TryGetProperty("msg", out var msg))
        {
            var c = code.GetInt32();
            if (c != 200 && c != 0)
            {
                // -1021: 타임스탬프 오류 → 다음 요청에서 즉시 재동기화
                if (c == -1021) _lastTimeSync = DateTime.MinValue;
                throw new Exception($"Binance API [{c}]: {msg.GetString()}");
            }
        }
    }

    private static string BuildQuery(Dictionary<string, string> p) =>
        string.Join("&", p.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static double P(string? s) => double.Parse(s!, Inv);

    private static readonly int[] RetryDelaysMs = [500, 1500, 4000];

    private async Task<T> WithRetry<T>(Func<Task<T>> action)
    {
        for (int i = 0; ; i++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (HttpRequestException ex) when (i < RetryDelaysMs.Length)
            {
                // 429 Too Many Requests → 더 긴 대기
                int delay = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? Math.Max(RetryDelaysMs[i], 30_000)
                    : RetryDelaysMs[i];
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (i < RetryDelaysMs.Length)
            {
                await Task.Delay(RetryDelaysMs[i]).ConfigureAwait(false);
            }
        }
    }

    // === Public API ===

    public async Task<double> GetPrice(string symbol)
    {
        var json = await GetJson("/fapi/v1/ticker/price", new() { ["symbol"] = symbol });
        return json.TryGetProperty("price", out var p) ? P(p.GetString()) : 0;
    }

    public async Task<OrderBookData> GetOrderBook(string symbol, int limit = 10)
    {
        var json = await GetJson("/fapi/v1/depth",
            new() { ["symbol"] = symbol, ["limit"] = limit.ToString() });

        var bids = new List<OrderBookEntry>();
        var asks = new List<OrderBookEntry>();

        if (json.TryGetProperty("bids", out var bidsArr))
            foreach (var b in bidsArr.EnumerateArray())
                bids.Add(new OrderBookEntry(P(b[0].GetString()), P(b[1].GetString())));

        if (json.TryGetProperty("asks", out var asksArr))
            foreach (var a in asksArr.EnumerateArray())
                asks.Add(new OrderBookEntry(P(a[0].GetString()), P(a[1].GetString())));

        return new OrderBookData(bids, asks);
    }

    public async Task<List<Candle>?> GetKlines(string symbol, string interval, int limit = 200, long? endTime = null)
    {
        var p = new Dictionary<string, string>
        {
            ["symbol"] = symbol, ["interval"] = interval, ["limit"] = limit.ToString()
        };
        if (endTime.HasValue) p["endTime"] = endTime.Value.ToString();
        var json = await GetJson("/fapi/v1/klines", p);
        if (json.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Candle>();
        foreach (var k in json.EnumerateArray())
        {
            list.Add(new Candle(
                k[0].GetInt64(),
                P(k[1].GetString()),
                P(k[2].GetString()),
                P(k[3].GetString()),
                P(k[4].GetString()),
                P(k[5].GetString()),
                P(k[7].GetString()),
                P(k[9].GetString())
            ));
        }
        return list;
    }

    public async Task<Position> GetPosition(string symbol)
    {
        EnsureAuthenticated();
        var json = await GetJson("/fapi/v2/positionRisk", new() { ["symbol"] = symbol }, signed: true);
        if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in json.EnumerateArray())
            {
                if ((p.GetProperty("symbol").GetString() ?? "") != symbol) continue;
                var amt = P(p.GetProperty("positionAmt").GetString() ?? "0");
                var entry = P(p.GetProperty("entryPrice").GetString() ?? "0");
                var pnl = P(p.GetProperty("unRealizedProfit").GetString() ?? "0");
                if (amt > 0) return new Position("L", Math.Abs(amt), entry, pnl);
                if (amt < 0) return new Position("S", Math.Abs(amt), entry, pnl);
            }
        }
        return new Position("N", 0, 0, 0);
    }

    public async Task<double> GetBalance()
    {
        EnsureAuthenticated();
        var json = await GetJson("/fapi/v2/balance", signed: true);
        if (json.ValueKind == JsonValueKind.Array)
        {
            double total = 0;
            foreach (var b in json.EnumerateArray())
            {
                var asset = b.GetProperty("asset").GetString() ?? "";
                if (asset is "USDT" or "USDC")
                    total += P(b.GetProperty("balance").GetString() ?? "0");
            }
            if (total > 0) return total;
        }
        return 0;
    }

    public async Task<int> GetPrecision(string symbol)
    {
        var json = await GetJson("/fapi/v1/exchangeInfo");
        if (json.TryGetProperty("symbols", out var syms))
            foreach (var s in syms.EnumerateArray())
                if (s.GetProperty("symbol").GetString() == symbol)
                    foreach (var f in s.GetProperty("filters").EnumerateArray())
                        if (f.GetProperty("filterType").GetString() == "LOT_SIZE")
                        {
                            var step = P(f.GetProperty("stepSize").GetString());
                            if (step >= 1) return 0;
                            int prec = 0;
                            while (step < 1) { step *= 10; prec++; }
                            return prec;
                        }
        return 3;
    }

    public async Task SetLeverage(string symbol, int leverage)
    {
        EnsureAuthenticated();
        await PostJson("/fapi/v1/leverage", new() { ["symbol"] = symbol, ["leverage"] = leverage.ToString() });
    }

    public async Task SetMarginType(string symbol, string type = "ISOLATED")
    {
        EnsureAuthenticated();
        try { await PostJson("/fapi/v1/marginType", new() { ["symbol"] = symbol, ["marginType"] = type }); }
        catch { /* already set */ }
    }

    public async Task<bool> MarketOrder(string symbol, string side, double quantity)
    {
        EnsureAuthenticated();
        var json = await PostJson("/fapi/v1/order", new()
        {
            ["symbol"] = symbol, ["side"] = side,
            ["type"] = "MARKET", ["quantity"] = quantity.ToString(Inv)
        });
        return json.TryGetProperty("orderId", out _);
    }

    /// <summary>Algo Order API: STOP_MARKET — algoId 반환</summary>
    public async Task<long> StopMarket(string symbol, string side, double quantity, double stopPrice, int pricePrecision = 2)
    {
        EnsureAuthenticated();
        var json = await PostJson("/fapi/v1/algoOrder", new()
        {
            ["symbol"] = symbol, ["side"] = side,
            ["algoType"] = "CONDITIONAL",
            ["type"] = "STOP_MARKET",
            ["quantity"] = quantity.ToString(Inv),
            ["triggerPrice"] = stopPrice.ToString($"F{pricePrecision}", Inv),
            ["reduceOnly"] = "true",
            ["workingType"] = "MARK_PRICE"
        });
        return json.TryGetProperty("algoId", out var id) ? id.GetInt64() : 0;
    }

    /// <summary>Algo Order API: TAKE_PROFIT_MARKET — algoId 반환</summary>
    public async Task<long> TakeProfitMarket(string symbol, string side, double quantity, double stopPrice, int pricePrecision = 2)
    {
        EnsureAuthenticated();
        var json = await PostJson("/fapi/v1/algoOrder", new()
        {
            ["symbol"] = symbol, ["side"] = side,
            ["algoType"] = "CONDITIONAL",
            ["type"] = "TAKE_PROFIT_MARKET",
            ["quantity"] = quantity.ToString(Inv),
            ["triggerPrice"] = stopPrice.ToString($"F{pricePrecision}", Inv),
            ["reduceOnly"] = "true",
            ["workingType"] = "MARK_PRICE"
        });
        return json.TryGetProperty("algoId", out var id) ? id.GetInt64() : 0;
    }

    /// <summary>Algo 주문 개별 취소 (algoId 기반)</summary>
    public async Task CancelAlgoOrder(long algoId)
    {
        if (algoId <= 0) return;
        EnsureAuthenticated();
        await DeleteJson("/fapi/v1/algoOrder", new() { ["algoId"] = algoId.ToString() });
    }

    /// <summary>모든 심볼의 Algo 주문 전체 취소 (시작 시 정리용)</summary>
    public async Task<int> CancelAllAlgoOrders()
    {
        EnsureAuthenticated();
        int cancelled = 0;
        try
        {
            var json = await GetJson("/fapi/v1/algoOpenOrders", signed: true);
            var arr = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("orders", out var o)
                ? o : json;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.TryGetProperty("algoId", out var id))
                    {
                        try { await CancelAlgoOrder(id.GetInt64()); cancelled++; }
                        catch { }
                    }
        }
        catch { }
        return cancelled;
    }

    /// <summary>전체 주문 취소: 일반 + Algo 둘 다</summary>
    public async Task CancelAllOrders(string symbol)
    {
        EnsureAuthenticated();
        // 일반 주문 취소
        try { await DeleteJson("/fapi/v1/allOpenOrders", new() { ["symbol"] = symbol }); }
        catch { }

        // Algo 주문: 조회 후 개별 취소 (응답: {"total":N,"orders":[...]})
        try
        {
            var json = await GetJson("/fapi/v1/algoOpenOrders", new() { ["symbol"] = symbol }, signed: true);
            var arr = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("orders", out var o)
                ? o : json;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.TryGetProperty("algoId", out var id))
                        try { await CancelAlgoOrder(id.GetInt64()); } catch { }
        }
        catch { }
    }

    /// <summary>열린 포지션 전체 조회 (positionAmt != 0)</summary>
    public async Task<List<Position>> GetAllPositions()
    {
        EnsureAuthenticated();
        var json = await GetJson("/fapi/v2/positionRisk", signed: true);
        var result = new List<Position>();
        if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in json.EnumerateArray())
            {
                var sym = p.GetProperty("symbol").GetString() ?? "";
                var amt = P(p.GetProperty("positionAmt").GetString() ?? "0");
                if (amt == 0) continue;
                var entry = P(p.GetProperty("entryPrice").GetString() ?? "0");
                var pnl = P(p.GetProperty("unRealizedProfit").GetString() ?? "0");
                var leverage = p.TryGetProperty("leverage", out var lv) ? int.Parse(lv.GetString() ?? "1") : 1;
                string side = amt > 0 ? "L" : "S";
                result.Add(new Position(side, Math.Abs(amt), entry, pnl) { Symbol = sym, Leverage = leverage });
            }
        }
        return result;
    }

    /// <summary>심볼의 미체결 주문 조회: 일반 + Algo (SL/TP 가격 복원용)</summary>
    public async Task<List<OpenOrder>> GetOpenOrders(string symbol)
    {
        EnsureAuthenticated();
        var result = new List<OpenOrder>();

        // 일반 주문
        try
        {
            var json = await GetJson("/fapi/v1/openOrders", new() { ["symbol"] = symbol }, signed: true);
            if (json.ValueKind == JsonValueKind.Array)
                foreach (var o in json.EnumerateArray())
                {
                    var type = o.GetProperty("type").GetString() ?? "";
                    var stopStr = o.TryGetProperty("stopPrice", out var sp) ? sp.GetString() : "0";
                    result.Add(new OpenOrder(type, P(stopStr ?? "0")));
                }
        }
        catch { }

        // Algo 주문 (STOP_MARKET, TAKE_PROFIT_MARKET) — 응답: {"total":N,"orders":[...]}
        try
        {
            var json = await GetJson("/fapi/v1/algoOpenOrders", new() { ["symbol"] = symbol }, signed: true);
            var arr = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("orders", out var ordersArr)
                ? ordersArr : json;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var o in arr.EnumerateArray())
                {
                    var type = o.TryGetProperty("type", out var ot) ? ot.GetString() ?? "" : "";
                    var trigStr = o.TryGetProperty("triggerPrice", out var tp) ? tp.GetString() : "0";
                    result.Add(new OpenOrder(type, P(trigStr ?? "0")));
                }
        }
        catch { }

        return result;
    }

    /// <summary>심볼별 펀딩비 조회 (Python: max_abs_funding 필터용)</summary>
    public async Task<Dictionary<string, double>> GetFundingRates()
    {
        var result = new Dictionary<string, double>();
        try
        {
            var json = await GetJson("/fapi/v1/premiumIndex");
            if (json.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in json.EnumerateArray())
                {
                    var sym = item.GetProperty("symbol").GetString() ?? "";
                    var rateStr = item.TryGetProperty("lastFundingRate", out var r) ? r.GetString() : "0";
                    result[sym] = P(rateStr ?? "0");
                }
            }
        }
        catch { }
        return result;
    }

    // ═══ 멀티코인 지원 API ═══

    public async Task<JsonElement> GetTicker24h()
    {
        return await GetJson("/fapi/v1/ticker/24hr");
    }

    public async Task<List<SymbolInfo>> GetTopSymbols(int count = 5)
    {
        var tickerJson = await GetJson("/fapi/v1/ticker/24hr");
        var exchangeJson = await GetJson("/fapi/v1/exchangeInfo");

        // exchangeInfo에서 심볼별 정밀도 추출
        var infoMap = new Dictionary<string, (string baseAsset, int pricePrecision, int qtyPrecision, double minNotional)>();
        if (exchangeJson.TryGetProperty("symbols", out var syms))
        {
            foreach (var s in syms.EnumerateArray())
            {
                var sym = s.GetProperty("symbol").GetString() ?? "";
                var status = s.GetProperty("status").GetString() ?? "";
                if (status != "TRADING") continue;

                // PERPETUAL USDT 마진만 허용 (XAG/XAU 등 특수 계약 제외)
                var contractType = s.TryGetProperty("contractType", out var ct) ? ct.GetString() : "";
                if (contractType != "PERPETUAL") continue;
                if (!sym.EndsWith("USDT")) continue;

                var baseAsset = s.GetProperty("baseAsset").GetString() ?? "";
                int pPrec = s.TryGetProperty("pricePrecision", out var pp) ? pp.GetInt32() : 2;
                int qPrec = s.TryGetProperty("quantityPrecision", out var qp) ? qp.GetInt32() : 3;
                double minNot = 5.0;

                if (s.TryGetProperty("filters", out var filters))
                    foreach (var f in filters.EnumerateArray())
                        if (f.GetProperty("filterType").GetString() == "MIN_NOTIONAL")
                        {
                            var notStr = f.TryGetProperty("notional", out var n) ? n.GetString() : null;
                            if (notStr != null) minNot = P(notStr);
                        }

                infoMap[sym] = (baseAsset, pPrec, qPrec, minNot);
            }
        }

        // 티커에서 USDT 마진 선물만 필터 + 거래량 정렬
        var tickers = new List<(string symbol, double price, double quoteVolume)>();
        if (tickerJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tickerJson.EnumerateArray())
            {
                var sym = t.GetProperty("symbol").GetString() ?? "";
                if (!sym.EndsWith("USDT")) continue;
                if (!infoMap.ContainsKey(sym)) continue;

                var price = P(t.GetProperty("lastPrice").GetString());
                var vol = P(t.GetProperty("quoteVolume").GetString());
                tickers.Add((sym, price, vol));
            }
        }

        return tickers
            .OrderByDescending(t => t.quoteVolume)
            .Take(count)
            .Select(t =>
            {
                var info = infoMap[t.symbol];
                return new SymbolInfo(
                    t.symbol, info.baseAsset, t.price, t.quoteVolume,
                    info.pricePrecision, info.qtyPrecision, info.minNotional
                );
            })
            .ToList();
    }

    public async Task<int> GetPricePrecision(string symbol)
    {
        var json = await GetJson("/fapi/v1/exchangeInfo");
        if (json.TryGetProperty("symbols", out var syms))
            foreach (var s in syms.EnumerateArray())
                if (s.GetProperty("symbol").GetString() == symbol)
                    return s.TryGetProperty("pricePrecision", out var pp) ? pp.GetInt32() : 2;
        return 2;
    }

    public void Dispose() => _http.Dispose();
}
