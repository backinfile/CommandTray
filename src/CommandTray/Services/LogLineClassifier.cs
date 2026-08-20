using System.Text.RegularExpressions;

namespace CommandTray.Services;

internal enum LogLineSeverity
{
    Normal,
    Warning,
    Error
}

internal static partial class LogLineClassifier
{
    public static LogLineSeverity Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        // Check errors first so a line that mentions both severities receives
        // the more urgent treatment.
        if (ErrorPattern().IsMatch(line)) return LogLineSeverity.Error;
        if (WarningPattern().IsMatch(line)) return LogLineSeverity.Warning;
        return LogLineSeverity.Normal;
    }

    [GeneratedRegex(@"(?:\[(?:ERR|ERROR)\]|\bERROR\b|错误)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"(?:\[(?:WRN|WARN|WARNING)\]|\b(?:WARN|WARNING)\b|警告)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarningPattern();
}
