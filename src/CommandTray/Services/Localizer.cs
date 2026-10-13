using System.Globalization;
using System.Text.RegularExpressions;

namespace CommandTray.Services;

public static class Localizer
{
    private static string _language = "auto";
    public static event EventHandler? LanguageChanged;
    public static string Language => _language;
    private static bool Chinese => _language == "zh" ||
        (_language == "auto" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh");
    public static string T(string english, string chinese) => Chinese ? chinese : english;

    public static void Configure(string language)
    {
        _language = language is "zh" or "en" ? language : "auto";
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    // Bind translations to each control's lifetime, allowing live switching
    // without retaining disposed windows through a static event.
    public static void Bind(Control control, string english, string chinese)
    {
        void Update(object? sender, EventArgs e) => control.Text = T(english, chinese);
        Update(null, EventArgs.Empty);
        LanguageChanged += Update;
        control.Disposed += (_, _) => LanguageChanged -= Update;
    }

    // Keep backend/API errors stable in English; translate the app's own
    // messages at the presentation boundary, including existing snapshots.
    internal static string ChineseError(string message)
    {
        var exact = message switch
        {
            "Configuration file is empty." => "配置文件为空。",
            "Imported configuration is empty." => "导入的配置为空。",
            "Every service must have a unique non-empty ID." => "每个服务必须有唯一且非空的 ID。",
            "Service name is required." => "请填写服务名称。",
            "Command is required." => "请填写启动命令。",
            "The command process did not start." => "启动命令未能启动。",
            "Stop command did not start." => "停止命令未能启动。",
            "Restart limit reached after 5 consecutive failures." => "连续失败 5 次，已达到自动重启次数上限。",
            _ => null
        };
        if (exact is not null) return exact;
        (string Pattern, string Replacement)[] formats =
        [
            (@"^Exited with code (.+)\.$", "进程已退出，退出码：$1。"),
            (@"^Restarting in (.+)s$", "$1 秒后自动重启"),
            (@"^Working directory does not exist: (.+)$", "工作目录不存在：$1"),
            (@"^Command is required for '(.+)'\.$", "请填写“$1”的启动命令。"),
            (@"^Stop timeout for '(.+)' must be between 1 and 300 seconds\.$", "“$1”的停止超时必须在 1–300 秒之间。"),
            (@"^Open URL for '(.+)' must be HTTP or HTTPS\.$", "“$1”的网页地址必须使用 HTTP 或 HTTPS。"),
            (@"^Environment variable names for '(.+)' must be non-empty and unique\.$", "“$1”的环境变量名称不能为空或重复。")
        ];
        foreach (var (pattern, replacement) in formats)
        {
            var match = Regex.Match(message, pattern);
            if (match.Success) return match.Result(replacement);
        }
        return message;
    }
}
