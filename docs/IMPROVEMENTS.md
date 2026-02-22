# BTC Trading Bot - 다관점 개선 기록

> 6가지 페르소나(유저, 개발자, 시니어, 트레이딩 전문가, 학습자, UI/UX)로 반복 개선한 기록.
> 각 라운드에서 **왜** 그런 결정을 했는지, **어떻게** 해결했는지 정리.

---

## Round 1 — 핵심 기능 추가 (7개)

### 1-1. 로그 색상 코딩 (UI/UX + 유저)
**문제:** 모든 로그가 동일한 회색 — ERROR/LONG/SHORT 구분 불가
**해결:** `IValueConverter` 패턴으로 텍스트 내용 기반 색상 분기

```csharp
// Converters/LogTextToColorConverter.cs
// 핵심: SolidColorBrush를 static readonly + Freeze()로 성능 최적화
private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));
static LogTextToColorConverter() { ErrorBrush.Freeze(); } // WPF 스레드 안전 + 성능
```

**배운 점:**
- WPF에서 `Freeze()`는 Freezable 객체를 읽기 전용으로 만들어 크로스 스레드 접근 허용 + GC 압력 감소
- `IValueConverter`는 XAML DataTemplate에서 바인딩 값을 변환하는 WPF 핵심 패턴
- App.xaml에 `<conv:LogTextToColorConverter x:Key="LogTextToColor"/>` 등록 필수

### 1-2. 거래 내역 % PnL 표시 (유저 + 전문가)
**문제:** USDT 금액만 표시, 가격 변동률 없음
**해결:** `TradeRecord`에 계산 속성 추가

```csharp
// Models/TradeRecord.cs
public double PnlPercent => EntryPrice > 0 && ExitPrice > 0
    ? (Side == "LONG"
        ? (ExitPrice - EntryPrice) / EntryPrice * 100  // 롱: 출구-입구
        : (EntryPrice - ExitPrice) / EntryPrice * 100)  // 숏: 입구-출구
    : 0;
```

**배운 점:**
- LONG은 가격 상승이 이익, SHORT은 가격 하락이 이익 — 방향별 수식 다름
- `init` 속성 + 계산 속성으로 immutable 모델 패턴 유지

### 1-3. 포지션 패널 SL/TP 표시 (유저 + 전문가)
**문제:** SL/TP 가격을 로그에서만 확인 가능
**해결:** `TradingEngine`에 `SlPrice`/`TpPrice` 공개 속성 → VM에서 읽어 UI 표시

**배운 점:**
- 엔진 내부 상태(`_paperSlPrice`)와 UI 표시용 상태(`SlPrice`)를 분리하면 캡슐화 유지

### 1-4. 설정값 검증 (개발자 + 시니어)
**문제:** Leverage=0 → division by zero, TradeUsdt=0 → 무의미 주문
**해결:** `ToggleBot()` 시작 전 검증 로직

```csharp
if (Leverage < 1 || Leverage > 125)
    ThemedDialog.Alert("설정 오류", "레버리지는 1~125 사이여야 합니다.", AlertType.Error);
```

**배운 점:**
- 검증은 가능한 **진입점에서 빨리** (Fail Fast). 엔진 내부까지 잘못된 값이 들어가면 디버깅 어려움
- 바이낸스 선물 레버리지 범위: 1~125x

### 1-5. 미실현 PnL 수수료 반영 (전문가)
**문제:** 미실현 PnL에 청산 수수료 미포함 → 실제보다 낙관적
**해결:** 테스트 모드에서 예상 청산 수수료 차감

```csharp
if (IsTest)
    unrealizedPnl -= cp * pos.Amount * FeeRate; // 0.05% taker fee
```

**배운 점:**
- 바이낸스 USDⓈ-M 선물 Taker 수수료: 0.05% (BNB 할인 미적용)
- 실전에서는 바이낸스가 자체 계산하므로 테스트 모드에서만 수동 차감

### 1-6. 최대 드로다운 (MDD) 추적 (전문가 + 유저)
**문제:** 리스크의 핵심 지표인 MDD 미추적
**해결:** 피크 잔고 대비 현재 잔고 하락률 추적

```csharp
private void UpdateDrawdown()
{
    if (CurrentBalance > PeakBalance) PeakBalance = CurrentBalance;
    if (PeakBalance > 0)
    {
        double dd = (PeakBalance - CurrentBalance) / PeakBalance * 100;
        if (dd > MaxDrawdownPct) MaxDrawdownPct = dd;
    }
}
```

**배운 점:**
- MDD = (최고점 - 최저점) / 최고점 × 100. 전략 위험도의 핵심 지표
- 매 거래 종료 시 `UpdateDrawdown()` 호출

### 1-7. 수익 팩터 (Profit Factor) (전문가 + 유저)
**문제:** 승률만으로는 전략 품질 판단 불가
**해결:** `TotalGrossProfit / TotalGrossLoss` 추적

**배운 점:**
- Profit Factor = 총이익 / 총손실. PF > 1.0이면 수익 전략, > 2.0이면 우수
- 승률이 낮아도 PF가 높으면 (큰 수익, 작은 손실) 좋은 전략

---

## Round 2 — 안정성 강화 (8개)

### 2-1. Profit Factor 계산 버그 수정
**문제:** `GrossProfit=0, GrossLoss>0`일 때 "--" 표시 (0.00이어야 함)
**해결:** 삼항 연산자 → 명시적 if/else 체인

```csharp
// Before (버그): ternary 우선순위 문제
PF = GrossLoss > 0 ? $"{GP/GL:F2}" : GP > 0 ? "∞" : "--";
// After (수정): 명확한 분기
if (GrossLoss > 0) PF = $"{GP/GL:F2}";
else if (GP > 0) PF = "∞";
else PF = "--";
```

**배운 점:**
- 중첩 삼항 연산자는 가독성과 정확성 모두 해침. 3개 이상 분기면 if/else 사용

### 2-2. Dispatcher.Invoke → BeginInvoke (교착 방지)
**문제:** 백그라운드 스레드에서 `Invoke`는 UI 스레드를 동기 대기 → 잠재적 교착
**해결:** 모든 `Dispatcher.Invoke` → `BeginInvoke` (비동기 디스패치)

```csharp
// Before: UI 스레드 대기 (교착 가능)
Application.Current.Dispatcher.Invoke(() => { ... });
// After: 비동기 큐잉 (교착 불가)
Application.Current.Dispatcher.BeginInvoke(() => { ... });
```

**배운 점:**
- `Invoke` = 동기 (호출 스레드가 UI 완료까지 블로킹)
- `BeginInvoke` = 비동기 (큐에 넣고 즉시 리턴)
- UI 업데이트만 필요한 경우 항상 `BeginInvoke` 선호

### 2-3. API 재시도 + 지수 백오프
**문제:** 네트워크 오류 시 즉시 실패
**해결:** `WithRetry<T>` 제네릭 헬퍼 — 500ms → 1500ms → 4000ms

```csharp
private async Task<T> WithRetry<T>(Func<Task<T>> action)
{
    for (int i = 0; ; i++)
    {
        try { return await action().ConfigureAwait(false); }
        catch (HttpRequestException) when (i < RetryDelaysMs.Length)
        {
            await Task.Delay(RetryDelaysMs[i]).ConfigureAwait(false);
        }
    }
}
```

**핵심 설계:**
- 매 재시도마다 params dict를 **복사** → timestamp/signature 재생성
- `when` 가드로 최대 재시도 횟수 제한
- Binance API 에러(비즈니스 로직)는 재시도하지 않음 — `ThrowOnApiError`가 먼저 throw

**배운 점:**
- `ConfigureAwait(false)` = UI 스레드 컨텍스트 불필요 시 사용 → 성능 향상
- `when` 절은 catch 필터 — 조건 불일치 시 예외가 재throw됨

### 2-4. ManualClose 경합 방지 (SemaphoreSlim)
**문제:** 수동 청산과 엔진 자동 청산이 동시 실행 가능
**해결:** `SemaphoreSlim(1,1)` — 뮤텍스 역할

```csharp
private readonly SemaphoreSlim _closeLock = new(1, 1);

public async Task ManualClose()
{
    if (!await _closeLock.WaitAsync(5000)) { Log("ERR: 타임아웃"); return; }
    try { /* 청산 로직 */ }
    finally { _closeLock.Release(); }
}
```

**배운 점:**
- `SemaphoreSlim`은 async 환경에서의 뮤텍스. `lock`은 async 내부에서 사용 불가
- `WaitAsync(0)` = 즉시 시도, `WaitAsync(5000)` = 5초 타임아웃
- `finally`로 반드시 Release — 그렇지 않으면 영구 교착

### 2-5. SL/TP 주문 실패 처리
**문제:** 시장가 진입 성공 후 SL/TP 주문 실패 시 무방비 포지션
**해결:** try/catch + ERR 로그

```csharp
try {
    await _api.StopMarket(...);
    await _api.TakeProfitMarket(...);
} catch (Exception ex) {
    Log("ERR: SL/TP 주문 실패 ({0}) — 수동 청산 필요!", ex.Message);
}
```

**배운 점:**
- 거래소 API 호출은 항상 실패 가능. 특히 주문은 가격 정밀도, 최소수량 등 제약이 많음
- 포지션 진입 후 보호 주문 실패 시 최소한 사용자에게 알려야 함

### 2-6. 잔고 소진 방어
**문제:** 잔고가 거래 불가능 수준까지 감소해도 계속 시도
**해결:** `CurrentBalance < TradeUsdt * 0.5` → 자동 중지

**배운 점:**
- 자동 매매 시스템에서 "킬 스위치"는 필수. 예상치 못한 연속 손실 대비

---

## Round 3 — 견고성 + UX (7개)

### 3-1. ChartViewModel 범위 체크
**문제:** `_candlePoints.Count == 0`일 때 `GetYRange` → `IndexOutOfRangeException`
**해결:** `if (count == 0) return (0, 0);` 가드 추가

**배운 점:**
- 컬렉션 접근 전 항상 크기 체크. 초기화 타이밍 이슈 방어

### 3-2. 429 Rate-limit 처리
**문제:** Binance 429 응답을 일반 네트워크 오류와 동일하게 처리
**해결:** `HttpRequestException.StatusCode == TooManyRequests` → 30초+ 대기

```csharp
int delay = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
    ? Math.Max(RetryDelaysMs[i], 30_000)  // 최소 30초
    : RetryDelaysMs[i];
```

**배운 점:**
- .NET 5+에서 `HttpRequestException.StatusCode` 속성 제공
- 바이낸스 Rate Limit: IP 기준 분당 1200 request. 초과 시 429 또는 418

### 3-3. 키보드 단축키 (Ctrl+L/Q)
**문제:** 로그 클리어, 봇 정지에 단축키 없음
**해결:** `KeyDown` 이벤트 핸들러

```csharp
private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
{
    if (Keyboard.Modifiers != ModifierKeys.Control) return;
    switch (e.Key) {
        case Key.L: _vm.LogEntries.Clear(); break;
        case Key.Q: if (_vm.IsRunning) _vm.ToggleBotCommand.Execute(null); break;
    }
}
```

**함정:** `KeyEventArgs`가 `System.Windows.Forms`와 `System.Windows.Input` 양쪽에 존재 → 명시적 네임스페이스 필요

### 3-4. 메인 루프 에러 에스컬레이션
**문제:** 오류 시 30초 고정 대기 → 무한 재시도
**해결:** 연속 오류 카운터 + 점진적 대기 + 10회 시 자동 중지

```csharp
consecErrors++;
int delay = Math.Min(30_000 * consecErrors, 180_000); // max 3분
if (consecErrors >= 10) { Log("[FATAL] ..."); break; }
// 성공 시: consecErrors = 0;
```

**배운 점:**
- 에러 핸들링의 3단계: 재시도 → 에스컬레이션 → 서킷 브레이커
- 성공 시 카운터 리셋이 핵심 (그래야 일시적 오류에서 회복)

### 3-5. Config 소수점 정밀도
**해결:** `ConfigService.Load()`에서 `Math.Round(config.TradeUsdt, 2)`

### 3-6. 라벨 ToolTip + 단위 명확화
**해결:** "수익팩터 (PF)", "MDD", "오늘 (USDT)" + ToolTip 설명 추가

### 3-7. 엔진 설명 강화
**해결:** 각 엔진에 "적합 시장" 정보 추가 — KYJ: 추세장, OU: 횡보장, KSJ: 범용

---

## Round 4 — 안전장치 + 리소스 관리 (7개)

### 4-1. TradingEngine IDisposable
**문제:** `_cts`, `_api`, `_closeLock` 리소스 누수
**해결:** `IDisposable` 구현

```csharp
public class TradingEngine : IDisposable
{
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _api?.Dispose();
        _closeLock.Dispose();
    }
}
```

**배운 점:**
- `IDisposable` 패턴: unmanaged 리소스(HttpClient, CancellationTokenSource 등)를 가진 클래스는 반드시 구현
- 호출 순서: Cancel → Dispose (취소 먼저, 해제 후)
- `using` 문 또는 명시적 `Dispose()` 호출로 사용

### 4-2. 일일 손실 기준: 당일 시작 잔고
**문제:** `DailyPnl / CurrentBalance`에서 CurrentBalance가 실시간 변동 → 부정확한 %
**해결:** `_dailyStartBalance` 고정 기준 도입

```csharp
// 일일 리셋 시
_dailyStartBalance = CurrentBalance;
// 손실 % 계산 시
double dailyLossBase = _dailyStartBalance > 0 ? _dailyStartBalance : CurrentBalance;
```

**배운 점:**
- 비율 계산에서 분모가 변동하면 의미 없는 결과. 기준점(baseline) 고정이 핵심

### 4-3. 이벤트 구독자 보호
**문제:** `OnLog?.Invoke(...)` — 구독자가 예외를 던지면 엔진 크래시
**해결:** try-catch 래핑

```csharp
private void Log(string text) {
    var now = DateTime.Now.ToString("MM/dd HH:mm:ss");
    try { OnLog?.Invoke($"[{now}] {text}"); } catch { /* subscriber error */ }
}
```

**배운 점:**
- 이벤트 발행자는 구독자의 안정성을 보장할 수 없음 → 방어적 호출
- 프로덕션에서는 `catch`에서 로깅 (여기서는 로그 자체가 실패한 것이므로 무시)

### 4-4. Semaphore 타임아웃
**문제:** `WaitAsync(0)` — 즉시 실패하면 사용자에게 불명확
**해결:** `WaitAsync(5000)` — 5초 대기 후 타임아웃 메시지

### 4-5. HighestProfit 리셋 보장
**문제:** Close 메서드에서 `HighestProfit` 미리셋 → 다음 포지션에 잔여값 영향
**해결:** `ClosePaperPosition`과 `ClosePosition` 모두에서 `HighestProfit = 0`

### 4-6. 날짜 변경 구분선
**해결:** 일일 리셋 시 `Log("══════ {0:yyyy-MM-dd} ══════", now)`

### 4-7. 로그 FontWeight 컨버터
**해결:** `LogTextToWeightConverter` — FATAL/ERR=Bold, LONG/SHORT/CLOSE=SemiBold, 나머지=Normal

---

## Round 5 — 마무리 폴리시 (4개)

### 5-1. SL/TP 표시: $ → %
**문제:** 절대 가격(`$45293 / $46104`)은 위험/보상 직관적 파악 불가
**해결:** 진입가 대비 % 계산 표시

```csharp
double slPct = Math.Abs((_engine.SlPrice - pos.EntryPrice) / pos.EntryPrice * 100);
double tpPct = Math.Abs((_engine.TpPrice - pos.EntryPrice) / pos.EntryPrice * 100);
SlTpText = $"SL -{slPct:F2}% / TP +{tpPct:F2}%";
```

### 5-2. 고위험 잔고 경고
**문제:** TradeUsdt가 잔고의 대부분일 때 한 번의 손실로 치명적
**해결:** `TradeUsdt > bal * 0.5` → 확인 다이얼로그

### 5-3. 수동 청산 ToolTip
**해결:** "현재 포지션을 시장가로 즉시 청산하고 SL/TP 주문을 취소합니다."

### 5-4. 연승/연패 추적
**해결:** `ConsecWins` 속성 + WinRate에 `"58% (12t 3W)"` 형식

---

## Round 6 — 방어 코딩 + 지표 확장 (6개)

### 6-1. Precision 범위 방어 (시니어)
**문제:** `GetPrecision()` 반환값이 음수 또는 비정상이면 `Round()` 예외
**해결:** `Math.Clamp(await _api.GetPrecision(...), 0, 8)`

**배운 점:**
- 외부 API 반환값은 절대 신뢰하지 말 것. 범위 클램핑은 방어 코딩의 기본
- `decimal.Round(value, -1)`은 `ArgumentOutOfRangeException` 발생

### 6-2. StdDev NaN 방어 (시니어)
**문제:** `ouStd`가 0에 가까우면 Z-Score 계산에서 `Infinity` 또는 `NaN`
**해결:** `ouStd > 1e-10` 가드를 StrategyOU.cs와 StrategyKSJ.cs 양쪽에 적용

```csharp
// Before
double zScore = ouStd > 0 ? (curPrice - ouMean) / ouStd : 0;
// After
double zScore = ouStd > 1e-10 ? (curPrice - ouMean) / ouStd : 0;
```

**배운 점:**
- 부동소수점에서 `> 0` 비교는 위험. 극소값(1e-300)도 통과 → 거대한 Z-Score
- epsilon 비교(`> 1e-10`)로 실질적 0을 걸러냄
- IEEE 754: `double` 최소 정규값은 ~2.2e-308, 비정규는 ~5e-324

### 6-3. 평균 수익/손실 지표 (전문가)
**문제:** 승률과 PF만으로는 개별 거래 크기 파악 불가
**해결:** `AvgWinLoss` 속성 — "+0.42 / -0.28" 형식

```csharp
double avgW = _engine.TotalWins > 0 ? _engine.TotalGrossProfit / _engine.TotalWins : 0;
double avgL = _engine.TotalLosses > 0 ? _engine.TotalGrossLoss / _engine.TotalLosses : 0;
AvgWinLoss = $"+{avgW:F2} / -{avgL:F2}";
```

**배운 점:**
- Expectancy = (승률 × 평균수익) - (패률 × 평균손실). 양수면 장기적 수익 기대
- UI에서는 USDT 금액으로 직관적 표시, 내부적으로 ratio(avgW/avgL)도 유용

### 6-4. 이벤트 보호 일관성 감사 (시니어)
**문제:** `OnLog`만 try-catch 보호 → `OnTradeComplete`, `OnStatsChanged`, `OnTradeMarker`는 미보호
**해결:** 6개 이벤트 호출 지점 모두에 try-catch 적용

```csharp
try { OnTradeComplete?.Invoke(record); } catch { }
try { OnStatsChanged?.Invoke(); } catch { }
try { OnTradeMarker?.Invoke(marker); } catch { }
```

**배운 점:**
- 방어 코딩은 일관성이 핵심. 하나만 보호하면 나머지가 뚫림
- 코드 감사(audit) 시 `grep "OnXxx?.Invoke"` 패턴으로 전수 검사

### 6-5. 포지션 보유 시간 표시 (유저)
**문제:** 포지션을 얼마나 오래 보유 중인지 알 수 없음
**해결:** `PositionOpenTime` 속성(DateTime?) + UI에 경과 시간 표시

```csharp
// TradingEngine: 진입 시 설정, 청산 시 null
PositionOpenTime = DateTime.Now;   // Open
PositionOpenTime = null;           // Close

// MainViewModel: OnPositionUpdate에서 계산
var elapsed = DateTime.Now - _engine.PositionOpenTime.Value;
HoldDuration = elapsed.TotalHours >= 1
    ? elapsed.ToString(@"h\:mm\:ss")   // 1:23:45
    : elapsed.ToString(@"mm\:ss");      // 23:45
```

**배운 점:**
- `DateTime?` (Nullable) 패턴: 값이 없을 수 있는 상태를 안전하게 표현
- TimeSpan 포맷: `@"h\:mm\:ss"` — `@` 문자열에서 `\:` 로 콜론 이스케이프
- 보유 시간은 트레일링 스탑 판단 보조 지표로도 활용 가능

### 6-6. (스킵된 페르소나)
- **UI/UX 디자이너** — Round 5에서 감소 수익 도달, 이번 라운드부터 스킵

---

## Round 7 — 견고성 마무리 (5개)

### 7-1. cp > 0 가드 (트레일링 스탑) (시니어)
**문제:** 가격 피드가 일시적으로 0 반환 시 트레일링 스탑에서 비정상 % 계산 → 잘못된 청산
**해결:** 트레일링 스탑 진입 조건에 `cp > 0` 추가

```csharp
// Before
if (pos.Type != "N" && pos.EntryPrice > 0)
// After
if (pos.Type != "N" && pos.EntryPrice > 0 && cp > 0)
```

**배운 점:**
- 외부 데이터(가격 피드)는 항상 유효성 검증. 0, NaN, Infinity 모두 방어
- 나눗셈 분모가 될 수 있는 값은 반드시 > 0 체크

### 7-2. 종료 시 포지션 정리 로깅 (시니어)
**문제:** 봇 종료 시 포지션 자동 정리가 로그 없이 실행 → 사후 디버깅 불가
**해결:** `[SHUTDOWN]` 태그로 정리 로그 추가

```csharp
Log("[SHUTDOWN] 페이퍼 포지션 정리 {0} @ {1:F2}", _paperPosition.Type, cp);
Log("[SHUTDOWN] 실전 포지션 정리 {0} @ {1:F2}", pos.Type, cp);
```

**배운 점:**
- 감사 추적(audit trail)에서 종료 시점은 특히 중요. "왜 포지션이 없어졌지?" 질문에 답변 가능
- 태그 컨벤션(`[SHUTDOWN]`, `[TEST]`, `[FATAL]`)으로 로그 필터링 용이

### 7-3. unrealizedPnl NaN/Infinity 방어 (시니어+개발자)
**문제:** 비정상 가격이나 수량으로 `unrealizedPnl`이 NaN/Infinity → UI 표시 깨짐
**해결:** OnPositionUpdate 호출 직전에 방어 코드 삽입

```csharp
if (double.IsNaN(unrealizedPnl) || double.IsInfinity(unrealizedPnl))
    unrealizedPnl = 0;
```

**배운 점:**
- `double.IsNaN()`과 `double.IsInfinity()`는 부동소수점 방어의 기본 도구
- NaN은 `==` 비교로 잡을 수 없음: `NaN == NaN`은 `false` (IEEE 754)

### 7-4. 매직 넘버 → 명명 상수 (개발자)
**문제:** `90 * 60`, `30 * 60`, `20 * 60`, `30_000`, `180_000` 등이 코드 곳곳에 하드코딩
**해결:** 클래스 상수로 추출

```csharp
private const long KyjCooldownSec = 90 * 60;   // 90분
private const long KsjCooldownSec = 30 * 60;   // 30분
private const long OuCooldownSec  = 20 * 60;   // 20분
private const int ErrorRetryBaseMs = 30_000;    // 기본 30초
private const int ErrorRetryMaxMs  = 180_000;   // 최대 3분
```

**배운 점:**
- 매직 넘버는 코드의 **의도**를 숨김. 상수명이 곧 문서
- 같은 값(`30_000`)이 여러 곳에 쓰이면 변경 시 누락 위험 → 상수 하나로 관리
- C# `const`는 컴파일 타임에 인라인 → 런타임 오버헤드 없음

### 7-5. JSON null 안전 (GetPosition/GetBalance) (개발자)
**문제:** `GetString()`이 JSON null 값에서 null 반환 가능 → `P(null)` 크래시
**해결:** null 병합 연산자(`??`) 추가

```csharp
// Before
var amt = P(p.GetProperty("positionAmt").GetString());
// After
var amt = P(p.GetProperty("positionAmt").GetString() ?? "0");
```

**배운 점:**
- `System.Text.Json`의 `GetString()`은 JSON null → C# null 반환
- `??` (null 병합 연산자)는 null 방어의 가장 간결한 방법
- 외부 JSON 파싱은 모든 필드가 null일 수 있다고 가정해야 함

---

## Round 8 — 인프라 안정성 (2개)

### 8-1. WebSocket 메시지 조각화 방어 (시니어)
**문제:** `ReceiveAsync`로 받은 데이터가 완전한 메시지가 아닐 수 있음. `EndOfMessage=false`면 조각 메시지 → JSON 파싱 실패
**해결:** `MemoryStream`으로 조각을 누적, `EndOfMessage=true`일 때만 파싱

```csharp
// Before: 조각 메시지도 바로 파싱 시도
var result = await _ws.ReceiveAsync(buf, ct);
using var json = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, result.Count));

// After: 완전한 메시지만 파싱
using var ms = new MemoryStream();
// ...
ms.Write(buf, 0, result.Count);
if (!result.EndOfMessage) continue; // 조각 → 누적 대기
try
{
    using var json = JsonDocument.Parse(
        Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    // ...
}
finally { ms.SetLength(0); } // 파싱 후 버퍼 초기화
```

**배운 점:**
- WebSocket은 TCP 기반 → 하나의 논리 메시지가 여러 프레임으로 분할 가능
- `WebSocketReceiveResult.EndOfMessage` = 이 프레임이 메시지의 마지막인지 표시
- `MemoryStream.SetLength(0)` → 내부 버퍼 유지하면서 위치만 리셋 (GC 압력 없음)
- Binance aggTrade는 보통 작지만, 시장 급변 시 프레임 분할 가능

### 8-2. ChartViewModel Dispose 안전 패턴 (개발자)
**문제:** `async void Dispose()` — 비동기 예외가 프로세스 크래시 유발 가능 + 호출자가 완료 대기 불가
**해결:** 동기 `Dispose()`로 변경, `ClientWebSocket.Dispose()`가 정리 담당

```csharp
// Before: async void (위험)
public async void Dispose()
{
    _uiTimer.Stop();
    try { await StopWebSocket(); } catch { }
    _restApi?.Dispose();
}

// After: 동기 Dispose (안전)
public void Dispose()
{
    _uiTimer.Stop();
    _ws?.Dispose();         // ClientWebSocket.Dispose()가 연결 종료
    _ws = null;
    _tickBuffer.Clear();
    _restApi?.Dispose();
}
```

**배운 점:**
- `async void`는 이벤트 핸들러에서만 사용. Dispose 등 일반 메서드에서는 금지
- `async void` 예외는 `try-catch`로도 완전히 잡히지 않을 수 있음 (SynchronizationContext 이슈)
- `ClientWebSocket.Dispose()`는 내부적으로 연결을 종료하므로 graceful close 불필요 (앱 종료 시)

---

## 수정 파일 전체 요약

| 파일 | R1 | R2 | R3 | R4 | R5 | R6 | R7 | R8 |
|------|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| `Services/TradingEngine.cs` | O | O | O | O | - | O | O | - |
| `ViewModels/MainViewModel.cs` | O | O | - | O | O | O | - | - |
| `MainWindow.xaml` | O | O | O | - | O | O | - | - |
| `Models/TradeRecord.cs` | O | - | - | - | - | - | - | - |
| `Services/BinanceApi.cs` | - | O | O | - | - | - | O | - |
| `MainWindow.xaml.cs` | - | - | O | - | - | - | - | - |
| `Services/ConfigService.cs` | - | - | O | - | - | - | - | - |
| `ViewModels/ChartViewModel.cs` | - | - | O | - | - | - | - | O |
| `Converters/LogTextToColorConverter.cs` | 신규 | - | - | - | - | - | - | - |
| `Converters/LogTextToWeightConverter.cs` | - | - | - | 신규 | - | - | - | - |
| `App.xaml` | O | - | - | O | - | - | - | - |
| `Services/StrategyOU.cs` | - | - | - | - | - | O | - | - |
| `Services/StrategyKSJ.cs` | - | - | - | - | - | O | - | - |
| `Services/BinanceWebSocketService.cs` | - | - | - | - | - | - | - | O |

## 핵심 패턴 정리

| 패턴 | 용도 | 파일 예시 |
|------|------|-----------|
| `IValueConverter` | XAML 바인딩 값 변환 | LogTextToColorConverter |
| `Freeze()` | WPF Freezable 성능 최적화 | LogTextToColorConverter |
| `BeginInvoke` | 비동기 UI 디스패치 (교착 방지) | MainViewModel |
| `SemaphoreSlim` | async 뮤텍스 | TradingEngine.ManualClose |
| `WithRetry<T>` | 제네릭 재시도 패턴 | BinanceApi |
| `IDisposable` | 리소스 해제 | TradingEngine |
| `[ObservableProperty]` | CommunityToolkit.Mvvm 자동 INPC | MainViewModel |
| Fail Fast | 진입점 검증 | MainViewModel.ToggleBot |
| 이벤트 try-catch | 구독자 에러 격리 | TradingEngine.Log |
| 지수 백오프 | 점진적 재시도 간격 | BinanceApi, TradingEngine |
| `??` Null 병합 | JSON null 방어 | BinanceApi.GetPosition |
| 명명 상수 | 매직 넘버 제거 | TradingEngine cooldowns |
| `NaN`/`Infinity` 가드 | 부동소수점 방어 | TradingEngine.unrealizedPnl |
| `EndOfMessage` 체크 | WebSocket 조각 메시지 처리 | BinanceWebSocketService |
| 동기 Dispose | `async void` 제거 | ChartViewModel |

---

## Round 9 — 코인 스캐너 대시보드 + 페르소나 검증 (10개)

### 9-1. ScannerViewModel 경쟁 조건 수정 (시니어)
**문제:** Timer.Elapsed 콜백이 ThreadPool에서 실행되어 RunScanAsync 동시 호출 가능
**해결:** `SemaphoreSlim(1,1)`로 동시 실행 방지, `WaitAsync(0)`으로 비차단 체크

```csharp
private readonly SemaphoreSlim _scanMutex = new(1, 1);
if (!await _scanMutex.WaitAsync(0)) return; // 이미 스캔 중이면 스킵
try { /* scan */ }
finally { _scanMutex.Release(); }
```

**배운 점:** `System.Timers.Timer`는 ThreadPool 스레드에서 Elapsed를 호출하므로 항상 동시성 보호 필요

### 9-2. CancellationTokenSource 수명 관리 (개발자)
**문제:** CTS를 새로 만들 때 이전 CTS를 Dispose하지 않아 리소스 누수
**해결:** 새 CTS 생성 전 이전 CTS를 명시적 Dispose

```csharp
var oldCts = _cts;
_cts = new CancellationTokenSource();
oldCts?.Dispose(); // 이전 CTS 정리
```

### 9-3. 봇 실행 중 스캐너 일시정지 (트레이딩 전문가)
**문제:** 스캐너(30 API calls/60s) + 트레이딩 엔진 동시 실행 시 API rate limit 위험
**해결:** `IsBotRunning` 플래그로 봇 실행 중 자동 스캔 스킵

```csharp
// Timer callback
if (IsBotRunning) return; // 봇 실행 중이면 스캔 건너뛰기
```

### 9-4. 24h 변동률 컬럼 추가 (유저)
**문제:** 스캐너에서 코인의 단기 모멘텀 확인 불가
**해결:** Binance 24h ticker API 호출 + CoinScanResult에 PriceChange24h 필드 추가

### 9-5. 스캔 진행률 표시 (유저)
**문제:** "스캔 중..." 표시만으로 어떤 코인까지 스캔했는지 알 수 없음
**해결:** progress 콜백으로 `"3/10 스캔 중..."` 형태 실시간 표시

### 9-6. 빈 결과 안내 메시지 (UI/UX)
**문제:** 스캔 결과 0건일 때 빈 화면만 표시
**해결:** MultiDataTrigger로 Count=0 && !IsScanning 시 안내 메시지 표시

### 9-7. 헤더 툴팁 범례 추가 (유저)
**문제:** RSI, ADX, Vol 등 컬럼의 의미를 모르는 유저에게 불친절
**해결:** 헤더 텍스트에 ToolTip으로 각 지표 설명 추가 + 하단 범례 바

### 9-8. EMA 분석 정밀도 향상 (트레이딩 전문가)
**문제:** lookback:8 (2시간)은 너무 짧아 신호가 지터링, 100봉으로 EMA(50) warm-up 부족
**해결:** lookback:16 (4시간)으로 확장, 15분봉 150개로 증가

### 9-9. 스캐너 설정 UI 추가 (유저)
**문제:** 스캔 간격·코인 수가 하드코딩되어 사용자 커스터마이징 불가
**해결:** BotConfig에 ScanIntervalSec/ScanCoinCount 추가, 설정 패널에 "스캐너 설정" 섹션 추가

```csharp
// MainViewModel — 설정 변경 시 ScannerVm에 즉시 전달
partial void OnScanIntervalSecChanged(int value) =>
    ScannerVm.UpdateSettings(value, ScanCoinCount);
```

### 9-10. Dispose 패턴 강화 (시니어)
**문제:** ScannerViewModel의 Timer, CTS, SemaphoreSlim 누수 가능
**해결:** IDisposable 구현으로 모든 리소스 명시적 정리, ObjectDisposedException 방어

```csharp
public void Dispose()
{
    _autoTimer?.Dispose();
    try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    _cts?.Dispose();
    _scanMutex.Dispose();
}
```

| 패턴 | 목적 | 적용 위치 |
|------|------|-----------|
| `SemaphoreSlim(1,1)` | 비동기 뮤텍스 (동시 실행 방지) | ScannerViewModel |
| CTS 수명 관리 | 이전 토큰소스 Dispose | ScannerViewModel.RunScanAsync |
| `IsBotRunning` 플래그 | API rate limit 보호 | ScannerViewModel.StartAutoTimer |
| Progress 콜백 | 실시간 진행률 UI 갱신 | ScannerService → ScannerViewModel |
| MultiDataTrigger | 복합 조건부 Visibility | ScannerControl.xaml |
| partial void On...Changed | CommunityToolkit 속성 변경 훅 | MainViewModel |
