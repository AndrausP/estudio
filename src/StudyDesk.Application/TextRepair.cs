using System.Text;
using System.Text.RegularExpressions;

namespace StudyDesk.Application;

/// <summary>Desfaz mojibake (UTF-8 lido como Windows-1252), ex.: "jÃºnior" → "júnior".</summary>
public static partial class TextRepair
{
    private static readonly Encoding Cp1252 = CreateCp1252();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static Encoding CreateCp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    // Byte líder UTF-8 (Â..ô) seguido de continuações (0x80–0xBF) como aparecem em cp1252.
    [GeneratedRegex("[\u00C2-\u00F4][\u0080-\u00BF\u0152\u0153\u0160\u0161\u0178\u017D\u017E\u0192\u02C6\u02DC\u2013\u2014\u2018-\u201E\u2020-\u2022\u2026\u2030\u2039\u203A\u20AC\u2122]{1,3}")]
    private static partial Regex MojibakeRun();

    public static string Fix(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return MojibakeRun().Replace(text, match =>
        {
            try { return StrictUtf8.GetString(Cp1252.GetBytes(match.Value)); }
            catch (Exception e) when (e is EncoderFallbackException or DecoderFallbackException or ArgumentException) { return match.Value; }
        });
    }
}
