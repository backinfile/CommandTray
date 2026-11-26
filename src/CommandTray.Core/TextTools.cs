using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommandTray.Core;

public enum TextOperation
{
    FormatJson, CompactJson, EncodeBase64, DecodeBase64, EncodeUrl, DecodeUrl, UniqueLines, Sha256
}

public static partial class TextTools
{
    public const int InputLimit = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string ConvertText(TextOperation operation, string input)
    {
        if (input.Length > InputLimit) throw new ArgumentOutOfRangeException(nameof(input));
        return operation switch
        {
            TextOperation.FormatJson => FormatJson(input, true),
            TextOperation.CompactJson => FormatJson(input, false),
            TextOperation.EncodeBase64 => Convert.ToBase64String(Utf8.GetBytes(input)),
            TextOperation.DecodeBase64 => Utf8.GetString(Convert.FromBase64String(input)),
            TextOperation.EncodeUrl => Uri.EscapeDataString(input),
            TextOperation.DecodeUrl => DecodeUrl(input),
            TextOperation.UniqueLines => UniqueLines(input),
            TextOperation.Sha256 => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(input))),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static string FormatJson(string input, bool indent)
    {
        using var parsed = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 256 });
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = indent,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            parsed.RootElement.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static string DecodeUrl(string input)
    {
        if (InvalidEscape().IsMatch(input)) throw new FormatException();
        return EscapeBytes().Replace(input, match =>
        {
            var bytes = new byte[match.Length / 3];
            for (int index = 0; index < bytes.Length; index++)
                bytes[index] = System.Convert.ToByte(match.Value.Substring(index * 3 + 1, 2), 16);
            return Utf8.GetString(bytes);
        });
    }

    private static string UniqueLines(string input)
    {
        var normalized = input.ReplaceLineEndings("\n");
        var finalLineBreak = normalized.EndsWith('\n');
        var content = finalLineBreak ? normalized[..^1] : normalized;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return string.Join('\n', content.Split('\n').Where(seen.Add)) + (finalLineBreak ? "\n" : "");
    }

    [GeneratedRegex("%(?![0-9a-fA-F]{2})")]
    private static partial Regex InvalidEscape();

    [GeneratedRegex("(?:%[0-9a-fA-F]{2})+")]
    private static partial Regex EscapeBytes();
}
