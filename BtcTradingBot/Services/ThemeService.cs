using System.Windows;
using System.Windows.Media;

namespace BtcTradingBot.Services;

public static class ThemeService
{
    // Dark theme (defaults)
    private static readonly Color DarkBg = Color.FromRgb(0x12, 0x12, 0x12);
    private static readonly Color DarkCard = Color.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly Color DarkInput = Color.FromRgb(0x2A, 0x2A, 0x2A);
    private static readonly Color DarkTextPrimary = Color.FromRgb(0xE4, 0xE4, 0xE7);
    private static readonly Color DarkTextSecondary = Color.FromRgb(0x9C, 0xA3, 0xAF);
    private static readonly Color DarkBorder = Color.FromRgb(0x33, 0x33, 0x33);

    // Light theme
    private static readonly Color LightBg = Color.FromRgb(0xF0, 0xF0, 0xF5);
    private static readonly Color LightCard = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color LightInput = Color.FromRgb(0xF0, 0xF0, 0xF0);
    private static readonly Color LightTextPrimary = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly Color LightTextSecondary = Color.FromRgb(0x6B, 0x72, 0x80);
    private static readonly Color LightBorder = Color.FromRgb(0xD1, 0xD5, 0xDB);

    public static void ApplyTheme(bool light)
    {
        var res = Application.Current.Resources;
        res["BgDarkBrush"] = new SolidColorBrush(light ? LightBg : DarkBg);
        res["BgCardBrush"] = new SolidColorBrush(light ? LightCard : DarkCard);
        res["BgInputBrush"] = new SolidColorBrush(light ? LightInput : DarkInput);
        res["TextPrimaryBrush"] = new SolidColorBrush(light ? LightTextPrimary : DarkTextPrimary);
        res["TextSecondaryBrush"] = new SolidColorBrush(light ? LightTextSecondary : DarkTextSecondary);
        res["BorderBrush"] = new SolidColorBrush(light ? LightBorder : DarkBorder);
    }
}
