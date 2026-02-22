using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BtcTradingBot.Converters;

public class LogTextToWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text) return FontWeights.Normal;

        if (text.Contains("[FATAL]") || text.Contains("ERR:"))
            return FontWeights.Bold;

        if (text.Contains("LONG") || text.Contains("SHORT") || text.Contains("CLOSE"))
            return FontWeights.SemiBold;

        if (text.Contains("══════"))
            return FontWeights.Bold;

        return FontWeights.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
