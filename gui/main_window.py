"""메인 윈도우 GUI"""
from PyQt6.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QLineEdit, QPushButton, QTextEdit,
    QGroupBox, QFormLayout, QSpinBox, QDoubleSpinBox,
    QStatusBar, QMessageBox, QTabWidget
)
from PyQt6.QtCore import Qt, QTimer
from PyQt6.QtGui import QFont, QColor

from core.engine import TradingEngine


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.engine = None
        self.setWindowTitle("BTC Trading Bot v7.0")
        self.setMinimumSize(900, 650)
        self._build_ui()

    def _build_ui(self):
        central = QWidget()
        self.setCentralWidget(central)
        layout = QVBoxLayout(central)

        # 탭
        tabs = QTabWidget()
        layout.addWidget(tabs)

        # === 탭 1: 설정 ===
        settings_tab = QWidget()
        settings_layout = QVBoxLayout(settings_tab)

        # API 설정
        api_group = QGroupBox("API 설정")
        api_form = QFormLayout()
        self.api_key_input = QLineEdit()
        self.api_key_input.setPlaceholderText("Binance Futures API Key")
        self.api_key_input.setEchoMode(QLineEdit.EchoMode.Password)
        api_form.addRow("API Key:", self.api_key_input)
        self.api_secret_input = QLineEdit()
        self.api_secret_input.setPlaceholderText("Binance Futures API Secret")
        self.api_secret_input.setEchoMode(QLineEdit.EchoMode.Password)
        api_form.addRow("API Secret:", self.api_secret_input)
        api_group.setLayout(api_form)
        settings_layout.addWidget(api_group)

        # 트레이딩 설정
        trade_group = QGroupBox("트레이딩 설정")
        trade_form = QFormLayout()
        self.symbol_input = QLineEdit("BTCUSDT")
        trade_form.addRow("심볼:", self.symbol_input)
        self.leverage_input = QSpinBox()
        self.leverage_input.setRange(1, 20)
        self.leverage_input.setValue(4)
        trade_form.addRow("레버리지:", self.leverage_input)
        self.trade_usdt_input = QDoubleSpinBox()
        self.trade_usdt_input.setRange(5, 10000)
        self.trade_usdt_input.setValue(35)
        self.trade_usdt_input.setSuffix(" USDT")
        trade_form.addRow("1회 투자금:", self.trade_usdt_input)
        self.interval_input = QSpinBox()
        self.interval_input.setRange(10, 300)
        self.interval_input.setValue(60)
        self.interval_input.setSuffix(" 초")
        trade_form.addRow("체크 간격:", self.interval_input)
        trade_group.setLayout(trade_form)
        settings_layout.addWidget(trade_group)

        # 리스크 설정
        risk_group = QGroupBox("리스크 관리")
        risk_form = QFormLayout()
        self.max_daily_input = QSpinBox()
        self.max_daily_input.setRange(1, 20)
        self.max_daily_input.setValue(4)
        risk_form.addRow("일일 최대 거래:", self.max_daily_input)
        self.max_loss_input = QDoubleSpinBox()
        self.max_loss_input.setRange(0.5, 20)
        self.max_loss_input.setValue(3.0)
        self.max_loss_input.setSuffix(" %")
        risk_form.addRow("일일 최대 손실:", self.max_loss_input)
        self.max_consec_input = QSpinBox()
        self.max_consec_input.setRange(1, 10)
        self.max_consec_input.setValue(3)
        risk_form.addRow("연속 손실 한도:", self.max_consec_input)
        self.cooldown_input = QSpinBox()
        self.cooldown_input.setRange(5, 300)
        self.cooldown_input.setValue(75)
        self.cooldown_input.setSuffix(" 분")
        risk_form.addRow("쿨다운:", self.cooldown_input)
        risk_group.setLayout(risk_form)
        settings_layout.addWidget(risk_group)

        # 디스코드
        discord_group = QGroupBox("알림 (선택)")
        discord_form = QFormLayout()
        self.discord_input = QLineEdit()
        self.discord_input.setPlaceholderText("Discord Webhook URL (비워두면 사용 안 함)")
        discord_form.addRow("Discord:", self.discord_input)
        discord_group.setLayout(discord_form)
        settings_layout.addWidget(discord_group)

        settings_layout.addStretch()
        tabs.addTab(settings_tab, "설정")

        # === 탭 2: 대시보드 ===
        dash_tab = QWidget()
        dash_layout = QVBoxLayout(dash_tab)

        # 상태 패널
        status_group = QGroupBox("현재 상태")
        status_grid = QHBoxLayout()

        self.lbl_price = self._make_stat_label("--")
        self.lbl_position = self._make_stat_label("대기중")
        self.lbl_pnl = self._make_stat_label("0.00")
        self.lbl_balance = self._make_stat_label("--")
        self.lbl_winrate = self._make_stat_label("0%")
        self.lbl_daily = self._make_stat_label("0t / 0.00")

        for title, lbl in [
            ("현재가", self.lbl_price),
            ("포지션", self.lbl_position),
            ("총 PnL", self.lbl_pnl),
            ("잔고", self.lbl_balance),
            ("승률", self.lbl_winrate),
            ("오늘", self.lbl_daily),
        ]:
            box = QVBoxLayout()
            header = QLabel(title)
            header.setAlignment(Qt.AlignmentFlag.AlignCenter)
            header.setStyleSheet("color: #888; font-size: 11px;")
            box.addWidget(header)
            box.addWidget(lbl)
            status_grid.addLayout(box)

        status_group.setLayout(status_grid)
        dash_layout.addWidget(status_group)

        # 로그
        self.log_area = QTextEdit()
        self.log_area.setReadOnly(True)
        self.log_area.setFont(QFont("Consolas", 9))
        self.log_area.setStyleSheet("background-color: #1e1e1e; color: #d4d4d4;")
        dash_layout.addWidget(self.log_area, stretch=1)

        tabs.addTab(dash_tab, "대시보드")

        # === 하단 버튼 ===
        btn_layout = QHBoxLayout()
        self.btn_start = QPushButton("봇 시작")
        self.btn_start.setStyleSheet(
            "QPushButton { background-color: #0d6efd; color: white; "
            "padding: 10px 30px; font-size: 14px; font-weight: bold; border-radius: 5px; }"
            "QPushButton:hover { background-color: #0b5ed7; }"
        )
        self.btn_start.clicked.connect(self.toggle_bot)
        btn_layout.addStretch()
        btn_layout.addWidget(self.btn_start)

        self.btn_close_pos = QPushButton("포지션 청산")
        self.btn_close_pos.setStyleSheet(
            "QPushButton { background-color: #dc3545; color: white; "
            "padding: 10px 20px; font-size: 13px; border-radius: 5px; }"
            "QPushButton:hover { background-color: #bb2d3b; }"
        )
        self.btn_close_pos.clicked.connect(self.manual_close)
        self.btn_close_pos.setEnabled(False)
        btn_layout.addWidget(self.btn_close_pos)
        btn_layout.addStretch()

        layout.addLayout(btn_layout)

        # 상태바
        self.statusBar().showMessage("준비됨")

    def _make_stat_label(self, text):
        lbl = QLabel(text)
        lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lbl.setFont(QFont("Consolas", 16, QFont.Weight.Bold))
        return lbl

    def toggle_bot(self):
        if self.engine and self.engine.isRunning():
            self.engine.stop()
            self.engine.wait(5000)
            self.btn_start.setText("봇 시작")
            self.btn_start.setStyleSheet(
                "QPushButton { background-color: #0d6efd; color: white; "
                "padding: 10px 30px; font-size: 14px; font-weight: bold; border-radius: 5px; }"
            )
            self.btn_close_pos.setEnabled(False)
            self.statusBar().showMessage("중지됨")
            return

        api_key = self.api_key_input.text().strip()
        api_secret = self.api_secret_input.text().strip()
        if not api_key or not api_secret:
            QMessageBox.warning(self, "입력 오류", "API Key와 Secret을 입력하세요.")
            return

        config = {
            "api_key": api_key,
            "api_secret": api_secret,
            "symbol": self.symbol_input.text().strip() or "BTCUSDT",
            "leverage": self.leverage_input.value(),
            "trade_usdt": self.trade_usdt_input.value(),
            "check_interval": self.interval_input.value(),
            "max_daily_trades": self.max_daily_input.value(),
            "max_daily_loss_pct": self.max_loss_input.value(),
            "max_consec_losses": self.max_consec_input.value(),
            "cooldown_minutes": self.cooldown_input.value(),
            "discord_webhook": self.discord_input.text().strip(),
        }

        self.engine = TradingEngine(config)
        self.engine.log_signal.connect(self.on_log)
        self.engine.status_signal.connect(self.on_status)
        self.engine.trade_signal.connect(self.on_trade)
        self.engine.position_signal.connect(self.on_position)
        self.engine.start()

        self.btn_start.setText("봇 중지")
        self.btn_start.setStyleSheet(
            "QPushButton { background-color: #6c757d; color: white; "
            "padding: 10px 30px; font-size: 14px; font-weight: bold; border-radius: 5px; }"
        )
        self.btn_close_pos.setEnabled(True)
        self.statusBar().showMessage("실행중...")

    def manual_close(self):
        if not self.engine or not self.engine.api:
            return
        reply = QMessageBox.question(
            self, "확인", "현재 포지션을 청산하시겠습니까?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        )
        if reply == QMessageBox.StandardButton.Yes:
            symbol = self.symbol_input.text().strip() or "BTCUSDT"
            pt, pa, pe, pp = self.engine.api.get_position(symbol)
            if pt != "N":
                cp = self.engine.api.get_price(symbol)
                self.engine._close_position(symbol, pt, pa, pe, cp)
            else:
                self.on_log("포지션 없음")

    def on_log(self, text: str):
        self.log_area.append(text)
        # 자동 스크롤
        sb = self.log_area.verticalScrollBar()
        sb.setValue(sb.maximum())

    def on_status(self, data: dict):
        self.lbl_price.setText(f"${data['price']:,.2f}")
        self.lbl_winrate.setText(f"{data['winrate']:.0f}% ({data['total']}t)")
        self.lbl_pnl.setText(f"{data['pnl']:+.2f}")
        if data["pnl"] >= 0:
            self.lbl_pnl.setStyleSheet("color: #00c853;")
        else:
            self.lbl_pnl.setStyleSheet("color: #ff1744;")
        self.lbl_balance.setText(f"${data['balance']:,.2f}")
        self.lbl_daily.setText(f"{data['daily_trades']}t / {data['daily_pnl']:+.2f}")

    def on_trade(self, data: dict):
        action = data["action"]
        if action == "OPEN":
            d = "LONG" if data["direction"] == "L" else "SHORT"
            self.on_log(f">>> {d} 진입 @ {data['price']:.2f}")
        elif action == "CLOSE":
            self.on_log(f">>> 청산 PnL: {data['pnl']:+.2f}")

    def on_position(self, data: dict):
        pt = data["type"]
        if pt == "N":
            self.lbl_position.setText("대기중")
            self.lbl_position.setStyleSheet("color: #888;")
        elif pt == "L":
            pct = ((data["price"] - data["entry"]) / data["entry"] * 100) if data["entry"] > 0 else 0
            self.lbl_position.setText(f"LONG {pct:+.2f}%")
            self.lbl_position.setStyleSheet("color: #00c853;" if pct >= 0 else "color: #ff1744;")
        else:
            pct = ((data["entry"] - data["price"]) / data["entry"] * 100) if data["entry"] > 0 else 0
            self.lbl_position.setText(f"SHORT {pct:+.2f}%")
            self.lbl_position.setStyleSheet("color: #00c853;" if pct >= 0 else "color: #ff1744;")

    def closeEvent(self, event):
        if self.engine and self.engine.isRunning():
            self.engine.stop()
            self.engine.wait(5000)
        event.accept()
