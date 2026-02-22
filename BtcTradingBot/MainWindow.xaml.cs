using System.Collections.Specialized;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BtcTradingBot.ViewModels;
using BtcTradingBot.Views;
using Forms = System.Windows.Forms;

namespace BtcTradingBot;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private MultiViewModel? _multiVm;
    private Forms.NotifyIcon? _trayIcon;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIcon();
        _vm = new MainViewModel();
        DataContext = _vm;

        // 로그 자동 스크롤 — BeginInvoke로 지연하여 레이아웃 충돌 방지
        _vm.LogEntries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (LogListBox.Items.Count > 0)
                        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                });
        };

        InitializeTrayIcon();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        KeyDown += OnKeyDown;
    }

    private void SetWindowIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(Brushes.DodgerBlue, 0, 0, 31, 31);
            using var font = new Font("Arial", 16f, System.Drawing.FontStyle.Bold);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("B", font, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
        }
        var hIcon = bmp.GetHicon();
        Icon = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "BTC Trading Bot",
            Visible = false,
        };

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
        };

        var openItem = menu.Items.Add("열기");
        openItem.ForeColor = System.Drawing.Color.FromArgb(0xE4, 0xE4, 0xE7);
        openItem.Click += (_, _) => ShowFromTray();

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = menu.Items.Add("종료");
        exitItem.ForeColor = System.Drawing.Color.FromArgb(0xEF, 0x44, 0x44);
        exitItem.Click += (_, _) => ForceClose();

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private static Icon CreateTrayIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillEllipse(Brushes.DodgerBlue, 0, 0, 15, 15);
        using var font = new Font("Arial", 8f, System.Drawing.FontStyle.Bold);
        g.DrawString("B", font, Brushes.White, 3, 2);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowFromTray()
    {
        if (_trayIcon != null) _trayIcon.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void ForceClose()
    {
        if (_multiVm == null && _vm.IsRunning && _vm.HasOpenPosition)
        {
            if (!ThemedDialog.Confirm("포지션 확인",
                "포지션이 열려 있습니다.\n청산 후 종료하시겠습니까?",
                "청산 후 종료", "취소"))
                return;

            try { await _vm.ClosePositionAsync(); }
            catch { /* StopEngine에서 정리 */ }
        }

        _forceClose = true;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Close();
    }

    private void MinimizeToTray()
    {
        if (_trayIcon != null) _trayIcon.Visible = true;
        Hide();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "☐";
        BorderThickness = WindowState == WindowState.Maximized
            ? new Thickness(8) : new Thickness(0);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // 봇 실행 중 최소화 → 트레이로 이동
        bool running = _multiVm?.IsRunning == true || _vm.IsRunning;
        if (WindowState == WindowState.Minimized && running)
        {
            WindowState = WindowState.Normal;
            MinimizeToTray();
            return;
        }

        MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "☐";
        BorderThickness = WindowState == WindowState.Maximized
            ? new Thickness(8) : new Thickness(0);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 항상 모드 선택 다이얼로그 표시
        var config = Services.ConfigService.Load();

        var dialog = new ModeSelectDialog(config.LastMode);
        if (dialog.ShowDialog() != true || dialog.SelectedMode == null)
        {
            _forceClose = true;
            Close();
            return;
        }

        string mode = dialog.SelectedMode;

        // 선택 기억 (다음 시작 시 하이라이트용)
        config.LastMode = mode;
        Services.ConfigService.Save(config);

        _vm.CurrentMode = mode!;

        if (mode == "Multi")
        {
            _multiVm = new MultiViewModel();
            _multiVm.LoadFromConfig(config);
            MultiContent.DataContext = _multiVm;
            try
            {
                await _multiVm.InitializeAsync();
            }
            catch (Exception ex)
            {
                _multiVm.LogEntries.Add($"[ERROR] 초기화 실패: {ex.Message}");
            }
        }
        else
        {
            try
            {
                _vm.LoadFromConfig(config);
                await _vm.InitializeChartAsync();
                await _vm.RefreshBalanceCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                _vm.LogEntries.Add($"[ERROR] 초기화 실패: {ex.Message}");
            }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
        {
            if (_multiVm != null)
                _multiVm.StopEngine();
            else
            {
                _vm.StopEngine();
                _vm.ChartVm.Dispose();
            }
            return;
        }

        bool isRunning = _multiVm?.IsRunning == true || _vm.IsRunning;
        if (isRunning)
        {
            e.Cancel = true;

            var dialog = new TrayConfirmDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                if (dialog.UserChoice == true)
                {
                    // 트레이로 이동
                    MinimizeToTray();
                }
                else
                {
                    // 종료 — Classic 모드: 포지션 보유 중이면 청산 확인
                    if (_multiVm == null && _vm.HasOpenPosition)
                    {
                        if (ThemedDialog.Confirm("포지션 확인",
                            "포지션이 열려 있습니다.\n청산 후 종료하시겠습니까?",
                            "청산 후 종료", "취소"))
                        {
                            _ = ClosePositionThenExit();
                        }
                        return; // e.Cancel 유지
                    }

                    _forceClose = true;
                    if (_multiVm != null)
                        _multiVm.StopEngine();
                    else
                    {
                        _vm.StopEngine();
                        _vm.ChartVm.Dispose();
                    }
                    if (_trayIcon != null)
                    {
                        _trayIcon.Visible = false;
                        _trayIcon.Dispose();
                    }
                    Close();
                }
            }
            // Cancel → e.Cancel = true 유지
        }
        else
        {
            if (_multiVm != null)
                _multiVm.ChartVm.Dispose();
            else
                _vm.ChartVm.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
    }

    private async Task ClosePositionThenExit()
    {
        try { await _vm.ClosePositionAsync(); }
        catch { /* StopEngine에서 정리 */ }

        _forceClose = true;
        _vm.StopEngine();
        _vm.ChartVm.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Close();
    }

    // ═══ 부드러운 스크롤 ═══
    private double _scrollTarget;
    private bool _scrollInitialized;

    private void SettingsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

        if (!_scrollInitialized)
        {
            _scrollTarget = SettingsScroll.VerticalOffset;
            _scrollInitialized = true;
        }

        _scrollTarget -= e.Delta * 0.4; // 스크롤 속도 조절
        _scrollTarget = Math.Clamp(_scrollTarget, 0, SettingsScroll.ScrollableHeight);

        var anim = new DoubleAnimation
        {
            To = _scrollTarget,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => _scrollTarget = SettingsScroll.VerticalOffset;

        SettingsScroll.BeginAnimation(ScrollViewerBehavior.VerticalOffsetProperty, anim);
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.L:
                if (_multiVm != null) _multiVm.LogEntries.Clear();
                else _vm.LogEntries.Clear();
                e.Handled = true;
                break;
            case Key.Q:
                if (_multiVm != null) { if (_multiVm.IsRunning) _multiVm.ToggleBotCommand.Execute(null); }
                else { if (_vm.IsRunning) _vm.ToggleBotCommand.Execute(null); }
                e.Handled = true;
                break;
        }
    }

    private void TestToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _vm.ToggleTestModeCommand.Execute(null);
    }

    private void NavScanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _vm.NavigateToScannerCommand.Execute(null);
    }

    private void NavTrading_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _vm.NavigateToTradingCommand.Execute(null);
    }

    public void ApplyConfig(Models.BotConfig config)
    {
        _vm.LoadFromConfig(config);
    }
}

/// <summary>WinForms 트레이 컨텍스트 메뉴 다크 테마 렌더러</summary>
internal class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    private static readonly System.Drawing.Color BgColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E);
    private static readonly System.Drawing.Color HoverColor = System.Drawing.Color.FromArgb(0x33, 0x33, 0x33);
    private static readonly System.Drawing.Color BorderColor = System.Drawing.Color.FromArgb(0x33, 0x33, 0x33);
    private static readonly System.Drawing.Color SepColor = System.Drawing.Color.FromArgb(0x33, 0x33, 0x33);

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(BgColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(BorderColor);
        var rect = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        e.Graphics.DrawRectangle(pen, rect);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        var rc = new Rectangle(System.Drawing.Point.Empty, e.Item.Size);
        var color = e.Item.Selected ? HoverColor : BgColor;
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, rc);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(SepColor);
        e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
    }
}
