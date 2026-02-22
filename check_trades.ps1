$apiKey = "l3LZiKPMZPJJZ02JaS9UfA9kz2MchUSi5ChCSRUYwdX238TwkoadJ0nFaKQSkdt4"
$apiSecret = "IPbxgiZ7YZ9aeXMCzgwlf5mdYlcbg9DHrQrmRcl087lTKV9y56ROGXgwHFBAvqfD"
$base = "https://fapi.binance.com"

$st = Invoke-RestMethod "$base/fapi/v1/time"
$ts = $st.serverTime

$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = [Text.Encoding]::UTF8.GetBytes($apiSecret)

# Open positions
$qp = "timestamp=$ts&recvWindow=5000"
$sig = [BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($qp))).Replace("-","").ToLower()
$headers = @{"X-MBX-APIKEY"=$apiKey}
$positions = Invoke-RestMethod "$base/fapi/v2/positionRisk?$qp&signature=$sig" -Headers $headers
$open = $positions | Where-Object { [double]$_.positionAmt -ne 0 }
Write-Host "=== OPEN POSITIONS ==="
if ($open) { $open | ForEach-Object { Write-Host "$($_.symbol) amt=$($_.positionAmt) entry=$($_.entryPrice) pnl=$($_.unRealizedProfit) lev=$($_.leverage)" } }
else { Write-Host "(none)" }

# Recent trades for DOGEUSDT
$qp2 = "symbol=DOGEUSDT&limit=20&timestamp=$ts&recvWindow=5000"
$sig2 = [BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($qp2))).Replace("-","").ToLower()
$trades = Invoke-RestMethod "$base/fapi/v1/userTrades?$qp2&signature=$sig2" -Headers $headers
Write-Host ""
Write-Host "=== RECENT DOGE TRADES ==="
$trades | ForEach-Object {
    $t = [DateTimeOffset]::FromUnixTimeMilliseconds($_.time).LocalTime.ToString("MM-dd HH:mm:ss")
    Write-Host "$t $($_.side) qty=$($_.qty) price=$($_.price) pnl=$($_.realizedPnl) fee=$($_.commission)"
}

# Recent orders for DOGEUSDT
$qp3 = "symbol=DOGEUSDT&limit=20&timestamp=$ts&recvWindow=5000"
$sig3 = [BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($qp3))).Replace("-","").ToLower()
$orders = Invoke-RestMethod "$base/fapi/v1/allOrders?$qp3&signature=$sig3" -Headers $headers
Write-Host ""
Write-Host "=== RECENT DOGE ORDERS ==="
$orders | Select-Object -Last 15 | ForEach-Object {
    $t = [DateTimeOffset]::FromUnixTimeMilliseconds($_.updateTime).LocalTime.ToString("MM-dd HH:mm:ss")
    Write-Host "$t $($_.side) $($_.type) stopPrice=$($_.stopPrice) status=$($_.status) qty=$($_.origQty)"
}
