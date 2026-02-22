using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BtcTradingBot.ViewModels;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace BtcTradingBot.Views;

public partial class ChartControl : UserControl
{
    private readonly Axis _candleXAxis;
    private readonly Axis _volumeXAxis;
    private readonly Axis _candleYAxis;
    private int _visibleCount; // 0 = 전체 표시(자동)
    private int _lastTotal;
    private int _panOffset; // 오른쪽(최신) 기준 패닝 오프셋 (0=최신 고정, 음수=오른쪽 여백)

    // 드래그 패닝
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartMin;
    private double _dragStartMax;

    // 드래그/줌 프레임 스로틀 (~30fps)
    private long _lastRenderTick;
    private const long ThrottleIntervalTicks = 330_000; // 33ms in ticks (100ns units)

    public ChartControl()
    {
        InitializeComponent();

        // ★ 핵심: 애니메이션 완전 비활성화 — 렌더링 부하 대폭 감소
        CandleChart.AnimationsSpeed = TimeSpan.Zero;
        CandleChart.EasingFunction = null;
        VolumeChart.AnimationsSpeed = TimeSpan.Zero;
        VolumeChart.EasingFunction = null;

        _candleXAxis = new Axis
        {
            ShowSeparatorLines = false,
            TextSize = 10,
            LabelsPaint = new SolidColorPaint(new SKColor(156, 163, 175)),
            UnitWidth = 1,
            MinStep = 1,
            Labeler = FormatXLabel,
        };
        _volumeXAxis = new Axis
        {
            ShowSeparatorLines = false, TextSize = 0,
            UnitWidth = 1, MinStep = 1,
        };
        _candleYAxis = new Axis
        {
            Position = LiveChartsCore.Measure.AxisPosition.End,
            ShowSeparatorLines = true,
            SeparatorsPaint = new SolidColorPaint(new SKColor(51, 51, 51)) { StrokeThickness = 0.5f },
            LabelsPaint = new SolidColorPaint(new SKColor(156, 163, 175)),
            TextSize = 11,
        };

        CandleChart.XAxes = [_candleXAxis];
        CandleChart.YAxes = [_candleYAxis];

        VolumeChart.XAxes = [_volumeXAxis];
        VolumeChart.YAxes = [new Axis { ShowSeparatorLines = false, TextSize = 0 }];

        // 바이낸스 스타일 줌: 스크롤 = X축 줌
        CandleChart.PreviewMouseWheel += OnChartZoom;
        VolumeChart.PreviewMouseWheel += OnChartZoom;

        // 드래그 패닝
        CandleChart.MouseLeftButtonDown += OnDragStart;
        CandleChart.MouseLeftButtonUp += OnDragEnd;
        CandleChart.MouseMove += OnDragMove;
        VolumeChart.MouseLeftButtonDown += OnDragStart;
        VolumeChart.MouseLeftButtonUp += OnDragEnd;
        VolumeChart.MouseMove += OnDragMove;

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ChartViewModel vm)
                vm.CandleValues.CollectionChanged += OnCandlesChanged;
        };
    }

    private string FormatXLabel(double value)
    {
        if (DataContext is not ChartViewModel vm) return "";
        int idx = (int)Math.Round(value);
        return vm.FormatTimestamp(idx);
    }

    private void OnChartZoom(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;
        int total = vm.CandleValues.Count;
        if (total < 15) return;

        if (_visibleCount <= 0) _visibleCount = total;

        // 현재 뷰포트
        double curMin = _candleXAxis.MinLimit ?? -0.5;
        double curMax = _candleXAxis.MaxLimit ?? (total + 0.5);

        // 마우스 위치를 데이터 좌표로 변환
        var element = sender as FrameworkElement;
        double chartWidth = element?.ActualWidth ?? 800;
        var mousePos = e.GetPosition(element);
        double mouseRatio = Math.Clamp(mousePos.X / chartWidth, 0, 1);
        double mouseValue = curMin + (curMax - curMin) * mouseRatio;

        // 줌 인/아웃
        double factor = e.Delta > 0 ? 0.85 : 1.18;
        int newVisible = Math.Clamp((int)(_visibleCount * factor), 15, total);
        _visibleCount = newVisible;

        if (_visibleCount >= total)
        {
            // 전체 보기로 복귀
            _visibleCount = 0;
            _panOffset = 0;
            _candleXAxis.MinLimit = null;
            _candleXAxis.MaxLimit = null;
            _volumeXAxis.MinLimit = null;
            _volumeXAxis.MaxLimit = null;
            ResetYAxis();
        }
        else
        {
            // 마우스 커서 위치 기준 줌 (커서 아래 데이터가 고정)
            double newMin = mouseValue - newVisible * mouseRatio;
            double newMax = newMin + newVisible;

            // 경계 클램프 (오른쪽 여백: 뷰포트의 50%까지 허용)
            double rightLimit = total + 0.5 + newVisible * 0.5;
            if (newMin < -0.5) { newMin = -0.5; newMax = newMin + newVisible; }
            if (newMax > rightLimit) { newMax = rightLimit; newMin = newMax - newVisible; }

            ApplyAxes(newMin, newMax);
            _panOffset = (int)(total + 0.5 - newMax);
            UpdateYAxis(vm, newMin, newMax);
        }

        e.Handled = true;
    }

    // === 드래그 패닝 ===

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;
        int total = vm.CandleValues.Count;
        if (total < 15) return;

        if (_visibleCount <= 0) _visibleCount = total;

        _isDragging = true;
        _dragStart = e.GetPosition(sender as IInputElement);
        _dragStartMin = _candleXAxis.MinLimit ?? -0.5;
        _dragStartMax = _candleXAxis.MaxLimit ?? (total + 0.5);
        (sender as UIElement)?.CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || DataContext is not ChartViewModel vm) return;

        // ★ 스로틀: ~30fps (33ms) 이내 중복 렌더 차단
        long now = DateTime.UtcNow.Ticks;
        if (now - _lastRenderTick < ThrottleIntervalTicks) return;
        _lastRenderTick = now;

        var current = e.GetPosition(sender as IInputElement);
        double chartWidth = (sender as FrameworkElement)?.ActualWidth ?? 800;
        double pixelDelta = current.X - _dragStart.X;
        double candleDelta = pixelDelta * _visibleCount / chartWidth;

        int total = vm.CandleValues.Count;
        double newMin = _dragStartMin - candleDelta;
        double newMax = _dragStartMax - candleDelta;

        // 왼쪽 경계 클램프
        if (newMin < -0.5)
        {
            newMin = -0.5;
            newMax = newMin + _visibleCount;
        }
        // 오른쪽 경계 클램프 (뷰포트의 50%까지 여백 허용)
        double rightLimit = total + 0.5 + _visibleCount * 0.5;
        if (newMax > rightLimit)
        {
            newMax = rightLimit;
            newMin = newMax - _visibleCount;
        }

        ApplyAxes(newMin, newMax);
        _panOffset = (int)(total + 0.5 - newMax);
        UpdateYAxis(vm, newMin, newMax);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        (sender as UIElement)?.ReleaseMouseCapture();
    }

    /// <summary>X축 한 번에 적용 (4개 프로퍼티 → 2회 렌더가 아니라 일괄)</summary>
    private void ApplyAxes(double min, double max)
    {
        _candleXAxis.MinLimit = min;
        _candleXAxis.MaxLimit = max;
        _volumeXAxis.MinLimit = min;
        _volumeXAxis.MaxLimit = max;
    }

    private void OnCandlesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;

        // 데이터 전체 교체 시 (심볼/타임프레임 변경) → 뷰포트 완전 초기화
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _visibleCount = 0;
            _panOffset = 0;
            _lastTotal = vm.CandleValues.Count;
            _candleXAxis.MinLimit = null;
            _candleXAxis.MaxLimit = null;
            _volumeXAxis.MinLimit = null;
            _volumeXAxis.MaxLimit = null;
            ResetYAxis();
            return;
        }

        int total = vm.CandleValues.Count;
        if (total == _lastTotal) return;
        _lastTotal = total;

        // panOffset == 0 이면 최신 따라감, > 0 이면 과거 보고 있으므로 뷰 유지
        if (_visibleCount > 0 && _visibleCount < total && _panOffset == 0)
            ApplyViewport(total);
        else if (_visibleCount > 0 && _visibleCount < total)
        {
            // 과거 보고 있어도 Y축은 갱신
            double min = _candleXAxis.MinLimit ?? -0.5;
            double max = _candleXAxis.MaxLimit ?? (total + 0.5);
            UpdateYAxis(vm, min, max);
        }
    }

    private void ApplyViewport(int total)
    {
        if (_visibleCount <= 0 || _visibleCount >= total)
        {
            _visibleCount = 0;
            _panOffset = 0;
            _candleXAxis.MinLimit = null;
            _candleXAxis.MaxLimit = null;
            _volumeXAxis.MinLimit = null;
            _volumeXAxis.MaxLimit = null;
            ResetYAxis();
        }
        else
        {
            double max = total - _panOffset + 0.5;
            double min = max - _visibleCount;
            double rightLimit = total + 0.5 + _visibleCount * 0.5;

            if (min < -0.5) { min = -0.5; max = min + _visibleCount; }
            if (max > rightLimit) { max = rightLimit; min = max - _visibleCount; }

            ApplyAxes(min, max);

            if (DataContext is ChartViewModel vm)
                UpdateYAxis(vm, min, max);
        }
    }

    // === Y축 수동 제어 (★ RingBuffer 직접 접근으로 최적화) ===

    private void UpdateYAxis(ChartViewModel vm, double xMin, double xMax)
    {
        int start = Math.Max(0, (int)Math.Ceiling(xMin));
        int end = (int)Math.Floor(xMax);

        var (yMin, yMax) = vm.GetYRange(start, end);
        if (yMin >= yMax) return;

        double padding = (yMax - yMin) * 0.05;
        if (padding < 1) padding = 1;
        _candleYAxis.MinLimit = yMin - padding;
        _candleYAxis.MaxLimit = yMax + padding;
    }

    private void ResetYAxis()
    {
        _candleYAxis.MinLimit = null;
        _candleYAxis.MaxLimit = null;
    }

    private async void TimeFrame_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_candleXAxis == null) return;
        _visibleCount = 0;
        _panOffset = 0;
        _candleXAxis.MinLimit = null;
        _candleXAxis.MaxLimit = null;
        _volumeXAxis.MinLimit = null;
        _volumeXAxis.MaxLimit = null;
        ResetYAxis();

        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item
            && item.Tag is string tf && DataContext is ChartViewModel vm)
        {
            await vm.ChangeTimeFrameCommand.ExecuteAsync(tf);
        }
    }
}

/// <summary>
/// 호가 수량 → 바 너비 변환기. MultiBinding: Qty, MaxQty, ContainerWidth
/// </summary>
public class QtyToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 ||
            values[0] is not double qty ||
            values[1] is not double maxQty ||
            values[2] is not double containerWidth ||
            maxQty <= 0)
            return 0.0;

        return Math.Max(2, qty / maxQty * containerWidth * 0.9);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
