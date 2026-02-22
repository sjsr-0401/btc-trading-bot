using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BtcTradingBot.Converters;

public class LogTextToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush TestBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush LongBrush = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush ShortBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush CloseBrush = new(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(0x9C, 0xA3, 0xAF));

    static LogTextToColorConverter()
    {
        ErrorBrush.Freeze();
        TestBrush.Freeze();
        LongBrush.Freeze();
        ShortBrush.Freeze();
        CloseBrush.Freeze();
        DefaultBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text) return DefaultBrush;

        if (text.Contains("[FATAL]") || text.Contains("ERR:"))
            return ErrorBrush;

        if (text.Contains("[TEST]"))
            return TestBrush;

        if (text.Contains("CLOSE") || text.Contains("TRAIL"))
            return CloseBrush;

        if (text.Contains("LONG") || text.Contains("신호감지"))
            return LongBrush;

        if (text.Contains("SHORT"))
            return ShortBrush;

        return DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
