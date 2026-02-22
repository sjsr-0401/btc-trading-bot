"""바이낸스 선물 API 래퍼"""
import requests
import hmac
import hashlib
import time
from urllib.parse import urlencode


class BinanceFuturesAPI:
    BASE = "https://fapi.binance.com"

    def __init__(self, api_key: str, api_secret: str):
        self.api_key = api_key
        self.api_secret = api_secret
        self._time_offset = 0
        self._sync_server_time()

    def _sync_server_time(self):
        """서버 시간 오프셋을 한 번 계산해두고 재사용"""
        try:
            r = requests.get(self.BASE + "/fapi/v1/time", timeout=5)
            server_time = r.json()["serverTime"]
            self._time_offset = server_time - int(time.time() * 1000)
        except Exception:
            self._time_offset = 0

    def _timestamp(self) -> int:
        return int(time.time() * 1000) + self._time_offset

    def _sign(self, params: dict) -> str:
        q = urlencode(params)
        return hmac.new(
            self.api_secret.encode(), q.encode(), hashlib.sha256
        ).hexdigest()

    def _headers(self) -> dict:
        return {"X-MBX-APIKEY": self.api_key}

    def get(self, path: str, params: dict = None, signed: bool = False):
        params = params or {}
        if signed:
            params["timestamp"] = self._timestamp()
            params["signature"] = self._sign(params)
        try:
            r = requests.get(
                self.BASE + path, params=params,
                headers=self._headers(), timeout=10
            )
            return r.json()
        except Exception as e:
            return {"error": str(e)}

    def post(self, path: str, params: dict = None):
        params = params or {}
        params["timestamp"] = self._timestamp()
        params["signature"] = self._sign(params)
        try:
            r = requests.post(
                self.BASE + path, params=params,
                headers=self._headers(), timeout=10
            )
            return r.json()
        except Exception as e:
            return {"error": str(e)}

    def delete(self, path: str, params: dict = None):
        params = params or {}
        params["timestamp"] = self._timestamp()
        params["signature"] = self._sign(params)
        try:
            r = requests.delete(
                self.BASE + path, params=params,
                headers=self._headers(), timeout=10
            )
            return r.json()
        except Exception as e:
            return {"error": str(e)}

    # === 편의 메서드 ===

    def get_price(self, symbol: str) -> float:
        data = self.get("/fapi/v1/ticker/price", {"symbol": symbol})
        if isinstance(data, dict) and "price" in data:
            return float(data["price"])
        return 0.0

    def get_klines(self, symbol: str, interval: str, limit: int = 200):
        data = self.get("/fapi/v1/klines", {
            "symbol": symbol, "interval": interval, "limit": limit
        })
        if data is None or isinstance(data, dict):
            return None
        candles = []
        for k in data:
            candles.append({
                "o": float(k[1]), "h": float(k[2]),
                "l": float(k[3]), "c": float(k[4]),
                "v": float(k[5]), "qv": float(k[7]),
                "tb": float(k[9])
            })
        return candles

    def get_position(self, symbol: str):
        data = self.get("/fapi/v2/positionRisk", {"symbol": symbol}, signed=True)
        if data and isinstance(data, list):
            for p in data:
                if p["symbol"] == symbol:
                    amt = float(p["positionAmt"])
                    entry = float(p["entryPrice"])
                    pnl = float(p["unRealizedProfit"])
                    if amt > 0:
                        return "L", abs(amt), entry, pnl
                    elif amt < 0:
                        return "S", abs(amt), entry, pnl
        return "N", 0, 0, 0

    def get_balance(self) -> float:
        data = self.get("/fapi/v2/balance", signed=True)
        if data and isinstance(data, list):
            for b in data:
                if b["asset"] == "USDT":
                    return float(b["availableBalance"])
        return 0.0

    def get_precision(self, symbol: str) -> int:
        data = self.get("/fapi/v1/exchangeInfo")
        if data and "symbols" in data:
            for s in data["symbols"]:
                if s["symbol"] == symbol:
                    for f in s["filters"]:
                        if f["filterType"] == "LOT_SIZE":
                            step = float(f["stepSize"])
                            if step >= 1:
                                return 0
                            p = 0
                            while step < 1:
                                step *= 10
                                p += 1
                            return p
        return 3

    def set_leverage(self, symbol: str, leverage: int):
        return self.post("/fapi/v1/leverage", {
            "symbol": symbol, "leverage": leverage
        })

    def set_margin_type(self, symbol: str, margin_type: str = "ISOLATED"):
        return self.post("/fapi/v1/marginType", {
            "symbol": symbol, "marginType": margin_type
        })

    def market_order(self, symbol: str, side: str, quantity: float):
        return self.post("/fapi/v1/order", {
            "symbol": symbol, "side": side,
            "type": "MARKET", "quantity": quantity
        })

    def stop_market(self, symbol: str, side: str, stop_price: float):
        return self.post("/fapi/v1/order", {
            "symbol": symbol, "side": side,
            "type": "STOP_MARKET", "stopPrice": stop_price,
            "closePosition": "true"
        })

    def take_profit_market(self, symbol: str, side: str, stop_price: float):
        return self.post("/fapi/v1/order", {
            "symbol": symbol, "side": side,
            "type": "TAKE_PROFIT_MARKET", "stopPrice": stop_price,
            "closePosition": "true"
        })

    def cancel_all_orders(self, symbol: str):
        return self.delete("/fapi/v1/allOpenOrders", {"symbol": symbol})
