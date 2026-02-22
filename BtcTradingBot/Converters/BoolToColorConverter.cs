using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BtcTradingBot.Converters;

public class PnlToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d)
            return d >= 0
                ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
                : new SolidColorBrush(Color.FromRgb(255, 23, 68));
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToRunningTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "봇 중지" : "봇 시작";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
