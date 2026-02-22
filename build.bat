@echo off
echo === BTC Trading Bot Build ===
echo.
echo 1. Installing dependencies...
pip install -r requirements.txt
pip install pyinstaller
echo.
echo 2. Building .exe...
pyinstaller --onefile --windowed --name "BTCTradingBot" --icon=assets/icon.ico main.py
echo.
echo Done! Output: dist\BTCTradingBot.exe
pause
