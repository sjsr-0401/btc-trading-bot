import hmac, hashlib, time, urllib.request, json

api_key = 'l3LZiKPMZPJJZ02JaS9UfA9kz2MchUSi5ChCSRUYwdX238TwkoadJ0nFaKQSkdt4'
api_secret = 'IPbxgiZ7YZ9aeXMCzgwlf5mdYlcbg9DHrQrmRcl087lTKV9y56ROGXgwHFBAvqfD'

# 서버 시간 동기화
with urllib.request.urlopen('https://fapi.binance.com/fapi/v1/time') as r:
    ts = json.loads(r.read())['serverTime']
params = f'timestamp={ts}'
sig = hmac.new(api_secret.encode(), params.encode(), hashlib.sha256).hexdigest()
url = f'https://fapi.binance.com/fapi/v2/positionRisk?{params}&signature={sig}'

req = urllib.request.Request(url, headers={'X-MBX-APIKEY': api_key})
try:
    with urllib.request.urlopen(req) as r:
        data = json.loads(r.read())
except urllib.error.HTTPError as e:
    print('HTTP Error:', e.code, e.read().decode())
    exit(1)

active = [p for p in data if float(p['positionAmt']) != 0]
if not active:
    print('현재 열린 포지션 없음')
else:
    for p in active:
        amt = float(p['positionAmt'])
        side = 'LONG' if amt > 0 else 'SHORT'
        pnl = float(p['unRealizedProfit'])
        print(f"{p['symbol']} | {side} | 수량: {amt} | 진입가: {p['entryPrice']} | 현재가: {p['markPrice']} | 미실현PnL: {pnl:.4f} USDT")
