# Binance USDT‑M Futures (5m) Auto‑Trading Bot Skeleton (Portfolio + Rotation)

> ⚠️ 교육/실험 목적 코드입니다. 실거래는 큰 손실이 날 수 있습니다.  
> 본 프로젝트는 **도박이 아니라 “구조화된 실험”**을 목표로 하지만, **수익을 보장하지 않습니다.**

## 목표 체크리스트(요구사항 반영)

- [x] 5분봉 기반 (config.backtest.timeframe="5m")
- [x] 레버리지 3~4배 기준 (risk.leverage)
- [x] 1회 리스크 1~2% 제한 (risk.per_trade_risk)
- [x] 하루 최대 손실 4% 초과 시 자동 정지 (risk.daily_loss_limit)
- [x] 자동 종목 교체(로테이션) (selector + 6h refresh)
- [x] VPS 24시간 운영 (run_live.py)
- [x] **Discord Webhook 알림** (discord.py)
- [x] 360일 백테스트 + 최근 180일 워크포워드 (run_backtest.py / run_walkforward.py)
- [x] 수수료/슬리피지/펀딩비 반영 (cost.*)

---

## 아키텍처(엔진 분리)

### 1) 종목 선별 엔진 (`selector.py`)
- 유니버스: USDT‑M Perpetual(Linear Swap) 목록
- 스코어링(기본):
  - **유동성(거래대금)**: 24h quoteVolume (live), 또는 백테스트에서는 24h(288 bars) `volume*close` 합
  - **변동성**: ATR% (ATR/close)
- `top_n` 종목만 트레이딩 대상으로 사용
- `refresh_interval_sec` 기본 6시간마다 갱신

### 2) 전략 엔진 (`strategies.py`)
- ADX 기반 레짐 분기:
  - **트렌드 구간**: Donchian Breakout + EMA50/EMA200 필터 + ATR 손절/익절 + ATR 트레일링
  - **레인지 구간**: Bollinger Band 이탈 + RSI 과매수/과매도 + ATR 손절/익절
- 같은 종목은 **1포지션만 유지** (중복 진입 방지)

### 3) 리스크 관리 엔진 (`risk.py`)
- 포지션 사이징:
  - 손절까지 손실이 `equity * per_trade_risk` 가 되도록 수량 결정
  - 동시에 `max_margin_fraction`으로 과도한 마진 점유 제한
- 일일 손실 제한:
  - UTC 기준 day_start_equity 대비 -4%면 **당일 신규 진입 중단**
  - 옵션으로 보유 포지션도 즉시 청산 가능 (`close_on_daily_stop`)
- 킬스위치:
  - peak 대비 -30% 하락 시 전체 중지/청산

### 4) 주문 실행 엔진 (`exchange.py`, `live.py`)
- 진입: Market(테이커) 주문
- 보호 주문: STOP_MARKET + TAKE_PROFIT_MARKET (reduceOnly)
- 마진모드/레버리지 자동 설정(베스트 에포트)

### 5) 모니터링/알림 (`discord.py`, logs/)
- Discord Webhook으로:
  - 종목 리프레시
  - 진입
  - 스탑 업데이트(트레일링)
  - 일일 스탑/킬스위치
  - 에러
  - 주기적 heartbeat(기본 1시간)

---

## 설치

```bash
python -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
```

### 설정
1) `config.yaml.example` → `config.yaml` 복사 후 값 조정  
2) `.env.example` → `.env` 복사 후 API 키/시크릿/Discord Webhook 입력  
3) 실행 전 환경변수 로드(예: Linux)

```bash
export $(grep -v '^#' .env | xargs)
```

---

## 백테스트(360일)

```bash
python run_backtest.py --config config.yaml --out bt_out
```

- 결과 파일:
  - `bt_out/<timestamp>/equity.csv`
  - `bt_out/<timestamp>/trades.csv`

---

## 워크포워드(최근 180일)

```bash
python run_walkforward.py --config config.yaml --out wf_out
```

- `wf_out/<timestamp>/equity_combined.csv` / `trades_combined.csv` 생성

---

## 실거래(주의)

```bash
python run_live.py --config config.yaml --state state/state.json
```

### 권장
- 처음에는 **Testnet** 또는 소액(현재 850달러라면 더 소액도 가능)으로 충분히 검증 후 확대.
- VPS에서는 `systemd`로 서비스화 권장.

---

## 현실적인 비용 모델(요약)

- 테이커 수수료: `cost.taker_fee`
- 슬리피지: `cost.slippage` (진입/청산 각각 1회 적용)
- 펀딩: 8시간마다 `funding_rate_per_8h * funding_cost_multiplier` 만큼 **비용으로만** 반영(보수적)

> 실제 펀딩은 +/−이며 “받는” 경우도 있지만, 여기서는 급사 위험을 낮추기 위해 보수적으로 비용 가정치를 사용합니다.

---

## 한계(중요)

- 5분봉 OHLCV 기반 백테스트는 **캔들 내 체결 순서/호가/부분체결**을 완벽히 재현할 수 없습니다.
- 실거래에서는 거래소 오류/네트워크/VPS 재시작/포지션 수동 변경 등을 고려해 **더 강한 리컨실리에이션**이 필요합니다.
