using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CommandTray.Core;

public enum TimestampFormat { Seconds, Milliseconds, Iso8601 }

public sealed record TextDifference(string Lines, int Added, int Removed, bool FinalNewlineChanged)
{
    public bool IsSame => Added == 0 && Removed == 0 && !FinalNewlineChanged;
}

public static partial class LocalUtilities
{
    public const int DiffLineLimit = 2_000;
    public const int QrByteLimit = 2_000;
    private static readonly string[] IsoTimestampFormats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFzzz"];

    public static TextDifference Compare(string before, string after)
    {
        if (before.Length > TextTools.InputLimit || after.Length > TextTools.InputLimit)
            throw new ArgumentOutOfRangeException(nameof(before));
        var (left, leftNewline) = Lines(before);
        var (right, rightNewline) = Lines(after);
        if (left.Length > DiffLineLimit || right.Length > DiffLineLimit)
            throw new ArgumentOutOfRangeException(nameof(before));

        int stride = right.Length + 1;
        var matches = new ushort[(left.Length + 1) * stride];
        for (int x = left.Length - 1; x >= 0; x--)
        {
            for (int y = right.Length - 1; y >= 0; y--)
            {
                int index = x * stride + y;
                matches[index] = left[x] == right[y] ? (ushort)(1 + matches[index + stride + 1])
                    : Math.Max(matches[index + stride], matches[index + 1]);
            }
        }

        var result = new StringBuilder();
        int i = 0, j = 0, added = 0, removed = 0;
        while (i < left.Length || j < right.Length)
        {
            if (i < left.Length && j < right.Length && left[i] == right[j])
            {
                result.Append("  ").Append(left[i++]).Append('\n');
                j++;
            }
            else if (i < left.Length && (j == right.Length || matches[(i + 1) * stride + j] >= matches[i * stride + j + 1]))
            {
                result.Append("- ").Append(left[i++]).Append('\n');
                removed++;
            }
            else
            {
                result.Append("+ ").Append(right[j++]).Append('\n');
                added++;
            }
        }
        return new(result.ToString(), added, removed, leftNewline != rightNewline);
    }

    private static (string[] Lines, bool FinalNewline) Lines(string text)
    {
        var normalized = text.ReplaceLineEndings("\n");
        var hasFinalNewline = normalized.EndsWith('\n');
        return (normalized.Length == 0 ? [] : (hasFinalNewline ? normalized[..^1] : normalized).Split('\n'),
            hasFinalNewline);
    }

    public static DateTimeOffset ReadTimestamp(TimestampFormat format, string input)
    {
        input = input.Trim();
        if (format == TimestampFormat.Iso8601)
        {
            if (!IsoInput().IsMatch(input)) throw new FormatException();
            if (input.EndsWith('Z')) input = input[..^1] + "+00:00";
            if (!DateTimeOffset.TryParseExact(input, IsoTimestampFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var timestamp)) throw new FormatException();
            return timestamp;
        }
        if (!long.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            throw new FormatException();
        return format switch
        {
            TimestampFormat.Seconds => DateTimeOffset.FromUnixTimeSeconds(value),
            TimestampFormat.Milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(value),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    public static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public static async Task<string> FileSha256Async(Stream input, IProgress<long>? progress, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long total = 0;
            long lastUpdate = Stopwatch.GetTimestamp();
            for (int count; (count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0;)
            {
                hash.AppendData(buffer, 0, count);
                total += count;
                if (progress is not null && Stopwatch.GetElapsedTime(lastUpdate).TotalMilliseconds >= 50)
                {
                    progress.Report(total);
                    lastUpdate = Stopwatch.GetTimestamp();
                }
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(total);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,3})?(?:Z|[+-][0-9]{2}:[0-9]{2})$")]
    private static partial Regex IsoInput();
}
