using System.Globalization;

namespace CommandTray.Services;

public static class Localizer
{
    private static bool Chinese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    public static string T(string english, string chinese) => Chinese ? chinese : english;
}
