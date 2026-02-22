# BTC Trading Bot - Project Agent

## 프로젝트 개요
바이낸스 선물(Futures) 자동매매 봇을 WPF 데스크톱 앱으로 제공한다.
초보 유저도 쉽게 사용할 수 있는 UI/UX를 목표로 한다.

- **플랫폼**: WPF (.NET 8.0 Windows)
- **MVVM**: CommunityToolkit.Mvvm
- **API**: Binance Futures (`fapi.binance.com`)
- **WebSocket**: Binance Futures Stream (`wss://fstream.binance.com/ws`)
- **차트**: LiveCharts2 (`LiveChartsCore.SkiaSharpView.WPF`)
- **테마**: 다크 모드 (App.xaml에 정의 완료)

---

## 현재 상태

### 완료된 것 ✅
| 영역 | 파일 | 설명 |
|------|------|------|
| Models | `BotConfig`, `Candle`, `Position`, `SignalResult` | 4개 모델 전부 완료 |
| API | `BinanceApi.cs` | 선물 API 전체 (주문, 잔고, 포지션, SL/TP) |
| 지표 | `Indicators.cs` | EMA, RSI, MACD, ATR, ADX |
| 전략 | `Strategy.cs` | 멀티타임프레임 EMA 크로스 + 확인 시스템 |
| 엔진 | `TradingEngine.cs` | 자동매매 루프, 트레일링, 리스크 관리 전부 |
| ViewModel | `MainViewModel.cs` | 설정/상태 바인딩, 봇 제어 커맨드 |
| 컨버터 | `BoolToColorConverter.cs` | PnL 색상, 버튼 텍스트 변환 |
| 테마 | `App.xaml` | 다크 테마 리소스 (색상, TextBox, Label 스타일) |

### 미완성 ❌
| 영역 | 상태 | 설명 |
|------|------|------|
| **MainWindow UI** | 빈 Grid | XAML 컨트롤 전혀 없음 |
| **로그인/API키 입력** | 없음 | 유저 온보딩 플로우 없음 |
| **비트코인 차트** | 없음 | 실시간 캔들 차트 필요 |
| **WebSocket 실시간** | 없음 | 가격 스트리밍 없음 |
| **거래 컨트롤 UI** | 없음 | 레버리지, 금액 등 설정 패널 |
| **설정 저장** | 없음 | JSON 파일 저장/로드 없음 |
| **Discord 알림** | config만 존재 | C# 엔진에 웹훅 전송 미구현 |
| **앱 아이콘** | 없음 | assets/ 폴더 비어있음 |

---

## 목표 기능 (Phase별)

### Phase 1: 유저 온보딩 — 로그인 & API 키 설정

**목표**: 처음 실행한 유저가 헤매지 않고 3분 안에 봇을 시작할 수 있게 한다.

#### 1-1. 첫 실행 화면 (Welcome / Setup Wizard)
- 앱 첫 실행 시 → 설정 파일(`config.json`) 없으면 온보딩 화면 표시
- 단계별 가이드:
  1. **"바이낸스 계정이 있으신가요?"** → 없으면 가입 링크 제공
  2. **"API 키 발급 가이드"** → 스크린샷 포함 단계별 설명
     - 바이낸스 로그인 → API 관리 → 키 생성
     - **선물 거래 권한** 체크 필수 안내
     - IP 제한 설정 권장 (보안)
     - ⚠️ "출금 권한은 절대 체크하지 마세요" 경고
  3. **API Key / Secret 입력** → 입력 후 연결 테스트 버튼
  4. **연결 성공** → 잔고 표시 & 메인 화면으로 이동

#### 1-2. API 키 인증 방식
- **방식**: API Key + Secret을 유저가 직접 입력
  - 바이낸스는 OAuth 미제공 → API 키 방식이 유일
  - 유저 비밀번호가 아닌 **제한된 권한의 키**임을 명확히 안내
- **보안**:
  - Secret은 `PasswordBox`로 마스킹
  - 로컬 `config.json`에 저장 (암호화 선택적)
  - 앱 종료 시 메모리에서 제거

#### 1-3. 설정 저장/로드
- `%AppData%/BtcTradingBot/config.json` 또는 앱 실행 경로
- 저장 항목: API Key, Secret, Symbol, Leverage, TradeUsdt, 기타 설정
- 앱 재실행 시 자동 로드 → 바로 메인 화면

---

### Phase 2: 실시간 가격 & 캔들스틱 차트 (WebSocket + 100ms 스로틀)

**목표**: WebSocket으로 실시간 가격을 수신하고, 100ms 단위 UI 갱신으로 끊김 없는 60fps 차트를 제공한다.

#### 2-1. 실시간 데이터 파이프라인

##### WebSocket 서비스 (`BinanceWebSocketService`)
- **엔드포인트**: `wss://fstream.binance.com/ws`
- **스트림**: `{symbol}@aggTrade` (체결 데이터) + `{symbol}@markPrice@1s` (마크 가격)
- 수신 메시지 → `PriceTick` DTO로 파싱 (timestamp, price, qty, source)
- 자동 재연결: 연결 끊김 시 2초 → 5초 → 10초 exponential backoff
- CancellationToken 지원, StartAsync/StopAsync 인터페이스

##### 100ms UI 스로틀링 규칙
- 틱은 초당 수십~수백 개 들어올 수 있음
- **UI 업데이트는 100ms마다 1회만 실행**
- 100ms 동안 들어온 틱 중 **최신 가격(last)**만 사용
- 차트 갱신도 100ms tick에 동기화

##### 스레드 안전 모델
```
[WebSocket 스레드]                    [UI 스레드 (100ms Timer)]
   │                                      │
   ├─ tick 수신                            │
   ├─ volatile _lastTick에 기록    ───→    ├─ _lastTick 읽기
   │  (ConcurrentQueue 또는 volatile)      ├─ 헤더 가격 갱신
   │                                      ├─ CandleAggregator 업데이트
   │                                      ├─ 차트 시리즈 반영
   │                                      └─ (100ms 후 반복)
```

#### 2-2. 캔들 집계 (`CandleAggregator`)

##### 틱 → OHLCV 변환 규칙
- **입력**: PriceTick (timestamp, price, qty)
- **타임프레임**: M1, M5, M15, H1, H4 (enum)
- 틱 timestamp를 타임프레임 bucket으로 floor하여 캔들 시작 시간 계산
- **같은 bucket**: High/Low 갱신, Close = last, Volume 누적
- **새 bucket**: 이전 캔들 확정(Completed), 새 캔들 생성(O=H=L=C=last)
- 이벤트: `OnCandleUpdate(Candle updated, bool isNewCandle, bool isCandleClosed)`

##### 초기 로딩 + 라이브 결합
- 앱 시작 시 REST `GetKlines()`로 최근 500~1000개 로드
- 로드된 캔들로 EMA 초기화 (SMA seed)
- 이후 WebSocket 틱으로 실시간 반영 (마지막 캔들 close 갱신)

#### 2-3. EMA 계산 (성능 최적화)

##### `IndicatorState` — O(1) 업데이트
- EMA 7/21/50 각각의 상태 저장: `lastEma`, `alpha` (= 2/(period+1))
- **캔들 확정 시**: EMA = alpha * close + (1 - alpha) * prevEma → O(1)
- **진행중 캔들 (선택)**: 마지막 EMA 기반으로 1스텝 임시 업데이트만 허용
- **전체 재계산 금지** — 매 틱마다 200개 캔들 돌리면 성능 사망

#### 2-4. 차트 데이터 구조 (성능/링버퍼)

##### `RingBuffer<T>` — 고정 길이 컬렉션
- 최대 1000~1500 캔들 (줌/팬 고려)
- ObservableCollection에 5000개 add/remove 하면 UI 느려짐 → 링버퍼로 해결
- 100ms마다 **전체 재할당 금지**
- 변경 범위:
  - 진행중 캔들: 마지막 아이템 값만 수정
  - 새 캔들: 1개 추가 (가득 차면 앞에서 drop)

##### LiveCharts2 시리즈 구성
- **CandlesticksSeries**: OHLC 캔들
- **LineSeries x3**: EMA 7 (파랑), EMA 21 (주황), EMA 50 (보라)
- **ColumnSeries**: 볼륨 바 (하단 별도 영역)
- **현재가 라인**: Section/Annotation으로 수평 점선
- **포지션 진입가 라인**: 롱=초록, 숏=빨강 수평 라인

##### 차트 기능
- **타임프레임 전환**: 15분 / 1시간 / 4시간 버튼
- **줌/팬**: 마우스 휠 줌, 드래그 이동
- 타임프레임 변경 시: WebSocket 재구독 + REST 재로딩 + Series 초기화

#### 2-5. 심볼/타임프레임 변경 플로우
1. WebSocket 기존 스트림 해제
2. REST로 새 심볼/타임프레임 캔들 로드
3. IndicatorState 리셋 & EMA 재계산 (로드된 캔들 기반)
4. RingBuffer 초기화 & Series 재바인딩
5. WebSocket 새 스트림 구독
6. 100ms tick 재개

#### 2-6. 성능 가드레일
- WebSocket 수신 루프에서 로그/디버그 출력 금지
- UI 컬렉션 변경: 100ms당 최대 1~2회
- 캔들 최대 1500개 제한
- EMA 라인도 동일 길이 유지
- GC 압박 최소화 (DTO 재사용 고려, 우선은 단순 구현)

---

### Phase 3: 거래 UI — 핵심 기능 배치

**목표**: 바이낸스보다 단순하지만, 필수 기능은 모두 갖추고 이쁘게 배치한다.

#### 3-1. 메인 레이아웃 (3단 구조)
```
┌─────────────────────────────────────────────────┐
│  헤더: BTC/USDT  $XX,XXX.XX  ▲0.00%  잔고: $XX  │
├──────────────────────┬──────────────────────────┤
│                      │  거래 설정 패널           │
│                      │  ┌─────────────────────┐ │
│                      │  │ 레버리지: [4x]  ▼   │ │
│   비트코인 차트       │  │ 금액: [35] USDT     │ │
│   (캔들스틱 + EMA)    │  │ 심볼: [BTCUSDT] ▼  │ │
│                      │  │                     │ │
│                      │  │ [  봇 시작  ]       │ │
│                      │  │ [수동 청산]          │ │
│                      │  └─────────────────────┘ │
│                      │                          │
│                      │  포지션 정보              │
│                      │  상태: LONG +2.35%       │
│                      │  PnL: +$4.20             │
│                      │  승률: 58% (12t)         │
├──────────────────────┴──────────────────────────┤
│  로그 패널 (스크롤)                               │
│  [02/12 14:30:01] 신호감지! L score:55 [...]     │
│  [02/12 14:31:02] 확인완료! L SL:1.5% TP:2.8%   │
└─────────────────────────────────────────────────┘
```

#### 3-2. 거래 설정 패널
- **레버리지** 슬라이더/드롭다운 (1x ~ 20x)
- **거래 금액** (USDT) 입력 + 퀵 버튼 (25%, 50%, 75%, 100%)
- **심볼 선택** (기본 BTCUSDT, 드롭다운으로 주요 코인)
- **체크 간격** (초 단위, 기본 60)
- **리스크 관리**:
  - 일일 최대 거래 횟수
  - 일일 최대 손실 %
  - 연속 손실 쿨다운
- **Discord 웹훅** (선택적)

#### 3-3. 포지션 & 상태 표시
- 현재 포지션 (LONG/SHORT/대기중) + 색상 코딩
- 미실현 PnL (실시간)
- 진입가, 수량
- 누적 PnL, 승률, 총 거래수
- 오늘 거래수 / 오늘 PnL

#### 3-4. 컨트롤 버튼
- **봇 시작/중지** 토글 (큰 버튼, 상태에 따라 색상 변경)
- **수동 청산** (확인 다이얼로그 포함)
- **설정 저장** (현재 설정을 config.json에 저장)

#### 3-5. 로그 패널
- 타임스탬프 + 메시지 (최대 500줄)
- 자동 스크롤
- 색상 코딩: 일반=회색, 신호=파랑, 체결=초록, 에러=빨강

---

### Phase 4: 입금 및 자금 관리 안내

**목표**: 유저가 "어디서 입금하지?" 하고 헤매지 않게 안내한다.

#### 4-1. 핵심 사실
- **이 앱에서 직접 입금은 불가능**
  - 바이낸스 API는 입금 주소 조회만 가능, 실제 입금은 바이낸스에서 직접
  - 규제 및 보안상 외부 앱에서 입출금 처리는 제공하지 않음
- **선물 지갑 이체도 바이낸스에서 직접**
  - 현물 → 선물 지갑 이체 필요 (바이낸스 앱/웹)

#### 4-2. 앱 내 안내 방식
- **"자금 관리" 가이드 버튼/섹션**:
  1. 바이낸스 앱/웹에서 USDT 입금 (은행이체, 카드, P2P, 코인전송)
  2. 현물 지갑 → USDⓈ-M 선물 지갑으로 이체
  3. 이 앱에서 잔고 확인 후 봇 시작
- **잔고 표시**: 앱에서 선물 지갑 USDT 잔고 실시간 조회 (API로 가능)
- **최소 권장 자금**: 안내 문구 (예: "최소 50 USDT 이상 권장")

#### 4-3. 앱에서 가능한 것 vs 불가능한 것
| 기능 | 가능 여부 | 설명 |
|------|-----------|------|
| 잔고 조회 | ✅ | API로 실시간 조회 |
| 자동 매매 | ✅ | 봇 엔진 |
| 수동 청산 | ✅ | 앱 내 버튼 |
| USDT 입금 | ❌ | 바이낸스에서 직접 |
| 현물→선물 이체 | ❌ | 바이낸스에서 직접 |
| 출금 | ❌ | 지원 안 함 (보안) |

---

## 기술 스택 & 의존성

### 현재
- .NET 8.0 Windows (WPF)
- CommunityToolkit.Mvvm 8.4.0

### 추가 필요
| 패키지 | 용도 | Phase |
|--------|------|-------|
| `LiveChartsCore.SkiaSharpView.WPF` | 캔들스틱 차트 (GPU 가속) | 2 |
| `System.Text.Json` | JSON 파싱 (기본 포함) | 전체 |
| `System.Net.WebSockets.ClientWebSocket` | 실시간 스트림 (기본 포함) | 2 |

> WebSocket은 .NET 8 기본 제공 `ClientWebSocket` 사용. 별도 NuGet 불필요.

---

## 파일 구조 (목표)

```
BtcTradingBot/
├── App.xaml                          # 다크 테마 리소스 (완료)
├── App.xaml.cs                       # 앱 진입점
├── MainWindow.xaml                   # 메인 윈도우 레이아웃 (Phase 3)
├── MainWindow.xaml.cs
│
├── Models/
│   ├── BotConfig.cs                  # 설정 모델 (완료)
│   ├── Candle.cs                     # 캔들 OHLCV (완료)
│   ├── Position.cs                   # 포지션 데이터 (완료)
│   ├── SignalResult.cs               # 신호 결과 (완료)
│   ├── PriceTick.cs                  # WebSocket 틱 DTO (Phase 2)
│   └── CandleUpdate.cs              # 캔들 업데이트 이벤트 DTO (Phase 2)
│
├── ViewModels/
│   ├── MainViewModel.cs              # 메인 VM (완료, UI 연결 필요)
│   ├── ChartViewModel.cs             # 차트 VM — 100ms tick, Series (Phase 2)
│   └── SetupViewModel.cs             # 온보딩 VM (Phase 1)
│
├── Views/
│   ├── SetupWindow.xaml              # 온보딩/가이드 (Phase 1)
│   └── ChartControl.xaml             # 차트 UserControl (Phase 2)
│
├── Services/
│   ├── BinanceApi.cs                 # REST API 통신 (완료)
│   ├── BinanceWebSocketService.cs    # WebSocket 실시간 스트림 (Phase 2)
│   ├── PriceTickBuffer.cs            # volatile lastTick 버퍼 (Phase 2)
│   ├── CandleAggregator.cs           # 틱→OHLCV 집계기 (Phase 2)
│   ├── IndicatorState.cs             # EMA O(1) 업데이트 상태 (Phase 2)
│   ├── Indicators.cs                 # 기술 지표 일괄 계산 (완료)
│   ├── Strategy.cs                   # 매매 전략 (완료)
│   ├── TradingEngine.cs              # 자동매매 엔진 (완료)
│   ├── ConfigService.cs              # 설정 저장/로드 (Phase 1)
│   └── DiscordService.cs             # 디스코드 알림 (선택)
│
├── Collections/
│   └── RingBuffer.cs                 # 고정 길이 링버퍼 (Phase 2)
│
├── Converters/
│   └── BoolToColorConverter.cs       # 값 변환기 (완료)
│
└── Assets/
    └── icon.ico                      # 앱 아이콘
```

---

## 구현 순서

1. **Phase 1** — API 키 입력 & 설정 저장 (온보딩)
2. **Phase 3** — 메인 UI 레이아웃 & 거래 컨트롤 (차트 없이도 봇 실행 가능)
3. **Phase 2** — WebSocket + 100ms 스로틀 + 캔들 차트 통합
4. **Phase 4** — 입금 가이드 & 안내 문구

> Phase 3을 2보다 먼저 하는 이유: 차트 없이도 봇은 돌아감.
> 핵심 기능(봇 시작/중지)을 먼저 동작시키고, 차트는 비주얼 강화로 추가.

---

## Phase 2 구현 상세 (실시간 차트 아키텍처)

### 새 파일별 구현 지시

#### A) `Services/BinanceWebSocketService.cs`
```
- StartAsync(string symbol, params string[] streams)
- StopAsync()
- event Action<PriceTick> OnTick
- ClientWebSocket 기반, wss://fstream.binance.com/ws/{stream}
- 자동 재연결: exponential backoff (2s → 5s → 10s)
- CancellationToken 지원
- 수신 루프에서 로그 출력 금지 (성능)
```

#### B) `Services/PriceTickBuffer.cs`
```
- volatile PriceTick? _latest  (단순 최신값 모드)
- 또는 ConcurrentQueue<PriceTick> (큐 모드, 100ms마다 전부 비움)
- TryGetLatest(out PriceTick tick) → 최신값 반환 & 초기화
```

#### C) `Services/CandleAggregator.cs`
```
- 생성자: CandleAggregator(TimeFrame tf)
- enum TimeFrame { M1, M5, M15, H1, H4 }
- Update(PriceTick tick) → CandleUpdate
- CandleUpdate { Candle candle, bool isNew, bool isClosed }
- bucket 계산: timestamp를 tf 단위로 floor
- 같은 bucket → H/L/C/V 갱신
- 새 bucket → 이전 확정 + 새 캔들 생성
- LoadHistory(List<Candle> candles) → 링버퍼 초기화용
```

#### D) `Services/IndicatorState.cs`
```
- EmaState { double LastValue, double Alpha }
- EmaState Create(int period) → alpha = 2.0/(period+1)
- double Update(double close) → O(1) EMA 계산, LastValue 갱신
- 초기화: 첫 N개 캔들은 SMA seed → 이후 EMA 업데이트
- 3개 인스턴스: EMA7, EMA21, EMA50
```

#### E) `Collections/RingBuffer.cs`
```
- RingBuffer<T>(int capacity)
- Add(T item) → 가득 차면 가장 오래된 것 제거
- UpdateLast(T item) → 마지막 아이템 교체
- T this[int index] { get; }
- int Count { get; }
- IReadOnlyList<T> 또는 IEnumerable<T> 구현
- 캔들 최대 1500개
```

#### F) `ViewModels/ChartViewModel.cs`
```
- 100ms DispatcherTimer 기반 UI tick
- Tick마다:
  1. PriceTickBuffer에서 최신 tick 읽기
  2. CurrentPrice / PriceChangePercent 갱신
  3. CandleAggregator.Update(tick) → 캔들 업데이트
  4. isClosed면 IndicatorState.Update(close)
  5. 차트 시리즈 마지막 포인트 수정 또는 1개 추가
- Properties: CurrentPrice, PriceChange24h, Series collections
- Commands: ChangeTimeFrame(TimeFrame), ChangeSymbol(string)
```

#### G) `Views/ChartControl.xaml`
```
- LiveCharts2 CartesianChart
- CandlesticksSeries: OHLC
- LineSeries x3: EMA 7(파랑) / 21(주황) / 50(보라)
- ColumnSeries: 볼륨 (하단)
- Section: 현재가 수평 점선
- Section: 포지션 진입가 라인 (롱=초록, 숏=빨강)
- 타임프레임 전환 버튼: 15m / 1h / 4h
- 줌/팬: ZoomMode, PanMode 활성화
```

### 100ms UI Tick 흐름도
```
[100ms Timer Tick]
  │
  ├─ 1. tick = buffer.TryGetLatest()
  │     └─ null이면 skip (데이터 없음)
  │
  ├─ 2. CurrentPrice = tick.Price
  │     PriceChangePercent = (tick.Price - open24h) / open24h * 100
  │
  ├─ 3. candleUpdate = aggregator.Update(tick)
  │     ├─ isNew == false: 링버퍼 마지막 캔들 값 수정
  │     ├─ isNew == true: 링버퍼에 1개 추가
  │     └─ isClosed == true: EMA 업데이트 (O(1))
  │
  ├─ 4. 차트 시리즈 반영
  │     ├─ 캔들: last point 수정 or add
  │     ├─ EMA: last point 수정 or add
  │     └─ 볼륨: last bar 수정 or add
  │
  └─ 5. (선택) 포지션 라인 위치 업데이트
```

### 심볼/타임프레임 변경 플로우
```
1. WebSocket StopAsync() → 기존 스트림 해제
2. REST GetKlines(newSymbol, newTf, 1000) → 히스토리 로드
3. CandleAggregator 리셋 + LoadHistory()
4. IndicatorState 리셋 + SMA seed → EMA 초기화
5. RingBuffer 클리어 + 새 캔들 데이터 로드
6. Series 컬렉션 재바인딩
7. WebSocket StartAsync(newSymbol, newStreams) → 새 스트림 구독
8. 100ms tick 재개
```

---

## Phase 2 완료 기준 (체크리스트)
- [ ] 현재가 텍스트가 100ms 단위로 부드럽게 바뀐다 (틱이 많아도 UI 프리즈 없음)
- [ ] 캔들 차트에서 진행중 캔들만 업데이트되고, 타임프레임 경계에서 캔들이 확정된다
- [ ] EMA 7/21/50 라인이 표시되고, 전체 재계산 없이 O(1) 갱신된다
- [ ] 줌/팬 동작 시에도 렉이 없다 (캔들 수 1500 제한)
- [ ] 심볼/타임프레임 변경이 안정적으로 동작한다
- [ ] WebSocket 끊김 시 자동 재연결된다

---

## 디자인 원칙

1. **다크 모드 전용** — 트레이딩 앱 표준, 눈 피로 감소
2. **정보 밀도** — 한 화면에 필요한 정보 모두 표시 (탭 최소화)
3. **색상 의미** — 초록=수익/롱, 빨강=손실/숏, 파랑=액센트, 회색=비활성
4. **반응형** — 창 크기 조절 시 레이아웃 유지 (Grid 비율)
5. **한국어 우선** — 모든 UI 텍스트 한국어
6. **성능 우선** — 100ms 스로틀, 링버퍼, O(1) 지표 업데이트

---

## 제약/주의

- 출금/입금 자동화 기능은 구현하지 않는다
- API Secret 암호화 (DPAPI)는 별도 Phase에서 처리
- LiveCharts2 NuGet 패키지 설치가 필요하면 csproj에 추가
- WebSocket 수신 루프에서 Console.WriteLine/Debug 남발 금지
- UI 컬렉션 변경은 100ms당 최대 1~2회로 제한
