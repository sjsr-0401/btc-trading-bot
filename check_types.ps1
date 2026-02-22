$asm = [System.Reflection.Assembly]::LoadFrom('c:\Users\admin\Documents\git\btc-trading-bot\BtcTradingBot\bin\Release\net8.0-windows\LiveChartsCore.dll')
try {
    $types = $asm.GetExportedTypes()
} catch {
    $types = $_.Exception.Types
}
$types | Where-Object { $_ -ne $null -and $_.Name -like '*Financial*' } | ForEach-Object { $_.FullName }
Write-Host "---"
$types | Where-Object { $_ -ne $null -and $_.Namespace -eq 'LiveChartsCore.Defaults' } | ForEach-Object { $_.FullName }
