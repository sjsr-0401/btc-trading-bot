# 📈 BTC Trading Bot — Binance Futures Automated Trading System

A real-time cryptocurrency trading desktop application built with **C# / .NET 8 / WPF**, featuring custom technical indicators, WebSocket-driven live charts, and a comprehensive risk management system.

> ⚠️ This is a personal project for learning and strategy validation. Use at your own risk.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![WPF](https://img.shields.io/badge/WPF-MVVM-blue)
![License](https://img.shields.io/badge/license-MIT-green)

---

## 📸 Screenshots

<!-- TODO: Add screenshots -->
> _Screenshots coming soon_

---

## ✨ Features

### Trading Engine
- **5 technical indicators implemented from scratch** — EMA, RSI, MACD, ATR, ADX (no external library dependencies)
- Multi-timeframe analysis (5m / 15m / 1h) with cross-validation entry signals
- Multi-symbol simultaneous monitoring scanner
- Paper trading mode for risk-free strategy validation

### Real-Time Visualization
- Binance Futures WebSocket streaming → **100ms throttle buffer** → LiveCharts2 (SkiaSharp) rendering
- 60fps candlestick charts with smooth updates
- Dark theme UI

### Risk Management
- Dynamic position sizing based on account balance
- Trailing stop-loss with configurable parameters
- Maximum loss limit per trade / per session
- Real-time P&L tracking

### API Integration
- Full Binance Futures REST + WebSocket API coverage
- HMAC-SHA256 request authentication
- Auto-reconnection on WebSocket disconnection

---

## 🛠 Tech Stack

| Layer | Technology |
|-------|-----------|
| **Language** | C# 12 |
| **Framework** | .NET 8, WPF |
| **Architecture** | MVVM (CommunityToolkit.Mvvm) |
| **Charts** | LiveCharts2 (SkiaSharp backend) |
| **API** | Binance Futures REST + WebSocket |
| **Auth** | HMAC-SHA256 |

---

## 🏗 Architecture

4-layer MVVM architecture with clear separation of concerns:

```
BtcTradingBot/
├── Models/          # Data models (candles, orders, positions, indicators)
├── Services/        # Business logic (API client, WebSocket, trading engine)
├── ViewModels/      # Presentation logic (MVVM bindings, commands)
├── Views/           # XAML UI (charts, dashboards, settings)
├── Collections/     # Observable collections & data structures
└── Converters/      # WPF value converters
```

**Data Flow:**
```
Binance WebSocket → Services (throttle + parse) → ViewModels (transform) → Views (render)
                                                 ↓
                                          Trading Engine
                                    (indicators → signals → orders)
```

---

## 🚀 Getting Started

### Prerequisites
- .NET 8 SDK
- Binance account (for API keys, paper trading works without real funds)

### Build & Run
```bash
git clone https://github.com/sjsr-0401/btc-trading-bot.git
cd btc-trading-bot
dotnet restore
dotnet run --project BtcTradingBot
```

### Configuration
1. Copy your Binance API key and secret into the settings panel
2. Select trading mode: **Paper** (recommended) or **Live**
3. Choose symbols to monitor and configure risk parameters

---

## 🔑 Key Implementation Details

### Technical Indicators (from scratch)
All 5 indicators are implemented at the algorithm level without external TA libraries:

- **EMA** — Exponential Moving Average with configurable period
- **RSI** — Relative Strength Index (Wilder's smoothing)
- **MACD** — Moving Average Convergence Divergence (signal + histogram)
- **ATR** — Average True Range for volatility measurement
- **ADX** — Average Directional Index for trend strength

### WebSocket Architecture
```
Market Stream → Deserialize → Throttle Buffer (100ms) → UI Update (Dispatcher)
                                    ↓
                            Indicator Calculation
                                    ↓
                            Signal Evaluation
                                    ↓
                            Order Execution (if conditions met)
```

### Risk Management Pipeline
1. **Pre-trade**: Position size = f(account balance, ATR, max risk %)
2. **In-trade**: Trailing stop adjusts with price movement
3. **Exit**: Max loss limit triggers forced liquidation

---

## 📊 Project Scale

- **50+** C# source files
- **14** model classes
- **14** service classes
- **4-layer** MVVM architecture
- Also includes Python prototype (`core/`, `gui/`) used during initial development

---

## 📄 License

MIT License — see [LICENSE](LICENSE) for details.

---

Built by [Kim Seongjine](https://github.com/sjsr-0401) — Industrial equipment software engineer exploring algorithmic trading.
