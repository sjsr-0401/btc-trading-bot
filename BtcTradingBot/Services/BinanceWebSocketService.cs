using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

/// <summary>
/// Binance Futures WebSocket 실시간 스트림.
/// aggTrade로 체결 틱을 수신하고, 자동 재연결을 지원한다.
/// </summary>
public class BinanceWebSocketService : IDisposable
{
    private const string BaseWs = "wss://fstream.binance.com/ws/";
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly int[] _backoffMs = [2000, 5000, 10000, 15000, 30000];

    public event Action<PriceTick>? OnTick;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task StartAsync(string symbol, CancellationToken externalToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var stream = $"{symbol.ToLower()}@aggTrade";
        var uri = new Uri(BaseWs + stream);

        int attempt = 0;
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(uri, _cts.Token);
                attempt = 0;

                await ReceiveLoop(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                if (_cts.Token.IsCancellationRequested) break;
                var delay = _backoffMs[Math.Min(attempt, _backoffMs.Length - 1)];
                attempt++;
                try { await Task.Delay(delay, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        using var ms = new MemoryStream();
        while (_ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.Count == 0) continue;

            ms.Write(buf, 0, result.Count);
            if (!result.EndOfMessage) continue; // 조각 메시지 → 누적 대기

            try
            {
                using var json = JsonDocument.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                var root = json.RootElement;

                // aggTrade: {"e":"aggTrade","E":ts,"s":"BTCUSDT","p":"price","q":"qty","T":tradeTime,...}
                if (root.TryGetProperty("p", out var pElem) && root.TryGetProperty("q", out var qElem))
                {
                    var price = double.Parse(pElem.GetString()!, CultureInfo.InvariantCulture);
                    var qty = double.Parse(qElem.GetString()!, CultureInfo.InvariantCulture);
                    long ts = root.TryGetProperty("T", out var tElem) ? tElem.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    OnTick?.Invoke(new PriceTick(ts, price, qty, "aggTrade"));
                }
            }
            catch { /* 파싱 실패 무시 — 성능 우선 */ }
            finally { ms.SetLength(0); }
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
