using System.Text.RegularExpressions;

namespace StudyDesk.Desktop;

/// <summary>Separa o resumo livre do diagnóstico em partes; se o formato não bater, <see cref="IsEmpty"/> fica verdadeiro.</summary>
internal sealed class DiagnosticSummary
{
    public string? Level { get; private init; }
    public int? Score { get; private init; }
    public List<string> Observations { get; } = [];
    public List<string> Gaps { get; } = [];
    public List<string> Suggestions { get; } = [];
    public bool IsEmpty => Level is null && Score is null && Observations.Count + Gaps.Count + Suggestions.Count == 0;

    public static DiagnosticSummary Parse(string text)
    {
        text = text.Trim();
        var level = Regex.Match(text, @"N[ií]vel estimado:\s*(?<v>[^.]+)\.", RegexOptions.IgnoreCase);
        var score = Regex.Match(text, @"(?<n>\d{1,3})\s*de\s*100");
        var result = new DiagnosticSummary
        {
            Level = level.Success ? Capitalize(level.Groups["v"].Value.Trim()) : null,
            Score = score.Success && int.TryParse(score.Groups["n"].Value, out var n) && n is >= 0 and <= 100 ? n : null
        };

        var gapsAt = text.IndexOf("Lacunas principais:", StringComparison.OrdinalIgnoreCase);
        var suggestAt = text.IndexOf("Sugest", StringComparison.OrdinalIgnoreCase);
        var observationsEnd = new[] { gapsAt, suggestAt }.Where(i => i >= 0).DefaultIfEmpty(text.Length).Min();
        var observationsStart = level.Success ? level.Index + level.Length : 0;
        var observations = observationsStart < observationsEnd ? text[observationsStart..observationsEnd] : "";
        observations = Regex.Replace(observations, @"Estimativa geral:[^.]*\.", "", RegexOptions.IgnoreCase);
        result.Observations.AddRange(Sentences(observations));

        if (gapsAt >= 0)
        {
            var end = suggestAt > gapsAt ? suggestAt : text.Length;
            var gaps = text[(gapsAt + "Lacunas principais:".Length)..end];
            result.Gaps.AddRange(Regex.Split(gaps, @"\(\d+\)").Select(Clean).Where(s => s.Length > 0));
        }
        if (suggestAt >= 0)
        {
            var colon = text.IndexOf(':', suggestAt);
            if (colon >= 0) result.Suggestions.AddRange(text[(colon + 1)..].Split(';').Select(Clean).Where(s => s.Length > 0).Select(Capitalize));
        }
        return result;
    }

    private static IEnumerable<string> Sentences(string text)
        => Regex.Split(text.Trim(), @"(?<=[.!?])\s+(?=[A-ZÁÉÍÓÚÂÊÔÃÕÇ])").Select(Clean).Where(s => s.Length > 2);

    private static string Clean(string value) => value.Trim().Trim(';', ',', ' ').Trim();

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
