using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

/// <summary>
/// USDM 리스크 관리: 포지션 사이징 + 3중 캡 + 일일 손실 제한 + 킬스위치
/// Python binance_usdm_bot/risk.py 이식
/// </summary>
public class RiskManager
{
    // 설정값
    public int Leverage { get; set; } = 3;
    public double PerTradeRisk { get; set; } = 0.0125;        // 1.25%
    public int MaxPositions { get; set; } = 4;
    public double MaxMarginFraction { get; set; } = 0.35;     // 포지션당 최대 마진 비율
    public double MaxTotalMarginFraction { get; set; } = 0.85;
    public double DailyLossLimit { get; set; } = 0.04;        // 4%
    public bool CloseOnDailyStop { get; set; } = true;
    public double KillSwitchMaxDrawdown { get; set; } = 0.30; // 30%
    public double MinNotionalUsdt { get; set; } = 10.0;
    public double TakerFee { get; set; } = 0.0004;
    public double Slippage { get; set; } = 0.00015;

    // 상태
    public double Equity { get; set; }
    public double PeakEquity { get; set; }
    public double DayStartEquity { get; set; }
    public double DailyPnl { get; set; }
    public DateTime LastDayReset { get; set; } = DateTime.UtcNow.Date;
    public bool IsDailyHalted { get; private set; }
    public bool IsKillSwitchTriggered { get; private set; }

    /// <summary>현재 사용 중인 총 마진</summary>
    public double TotalMarginUsed(Dictionary<string, PositionState> positions)
        => positions.Values.Where(p => p.IsOpen).Sum(p => p.MarginUsed);

    /// <summary>일일 리셋 (UTC 기준)</summary>
    public void CheckDayReset()
    {
        var today = DateTime.UtcNow.Date;
        if (today > LastDayReset)
        {
            DayStartEquity = Equity;
            DailyPnl = 0;
            IsDailyHalted = false;
            LastDayReset = today;
        }
    }

    /// <summary>킬스위치 체크: peak 대비 drawdown</summary>
    public bool CheckKillSwitch()
    {
        if (Equity > PeakEquity)
            PeakEquity = Equity;

        if (PeakEquity > 0 && (PeakEquity - Equity) / PeakEquity >= KillSwitchMaxDrawdown)
        {
            IsKillSwitchTriggered = true;
            return true;
        }
        return false;
    }

    /// <summary>일일 손실 제한 체크</summary>
    public bool CheckDailyLimit()
    {
        if (DayStartEquity > 0 && DailyPnl < 0 &&
            Math.Abs(DailyPnl) / DayStartEquity >= DailyLossLimit)
        {
            IsDailyHalted = true;
            return true;
        }
        return false;
    }

    /// <summary>신규 진입 허용 여부</summary>
    public bool CanOpenPosition(Dictionary<string, PositionState> positions)
    {
        if (IsKillSwitchTriggered) return false;
        if (IsDailyHalted) return false;
        int openCount = positions.Values.Count(p => p.IsOpen);
        return openCount < MaxPositions;
    }

    /// <summary>
    /// 리스크 기반 포지션 사이징 (3중 캡)
    /// 1) 리스크 기반: equity × per_trade_risk / stop_distance
    /// 2) 포지션당 마진 캡: equity × max_margin_fraction
    /// 3) 총 마진 캡: equity × max_total_margin_fraction - 현재 사용 마진
    /// </summary>
    public (double qty, double margin) CalcPositionSize(
        double price, double stopDistance, int precision,
        Dictionary<string, PositionState> positions)
    {
        if (price <= 0 || stopDistance <= 0 || Equity <= 0)
            return (0, 0);

        // 비용 보정: 진입+청산 수수료 + 슬리피지
        double costPerUnit = price * (TakerFee * 2 + Slippage);

        // 1) 리스크 기반 수량
        double riskAmount = Equity * PerTradeRisk;
        double effectiveStop = stopDistance + costPerUnit;
        double qtyRisk = riskAmount / effectiveStop;

        // 2) 포지션당 마진 캡
        double maxMarginPerPos = Equity * MaxMarginFraction;
        double qtyMargin = maxMarginPerPos * Leverage / price;

        // 3) 총 마진 캡
        double usedMargin = TotalMarginUsed(positions);
        double availableMargin = Math.Max(0, Equity * MaxTotalMarginFraction - usedMargin);
        double qtyTotal = availableMargin * Leverage / price;

        // 최소값 선택
        double qty = Math.Min(qtyRisk, Math.Min(qtyMargin, qtyTotal));
        qty = Math.Round(qty, precision);

        // 최소 명목가 체크
        if (qty * price < MinNotionalUsdt)
            return (0, 0);

        double margin = qty * price / Leverage;
        return (qty, margin);
    }

    /// <summary>거래 완료 후 PnL 기록 (DailyPnl만 갱신 — Equity는 호출부에서 이미 반영)</summary>
    public void RecordPnl(double pnl)
    {
        DailyPnl += pnl;
        CheckDailyLimit();
    }
}
