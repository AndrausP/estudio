using System.Globalization;
using System.Text;
using StudyDesk.Domain;

namespace StudyDesk.Infrastructure;

public enum SearchHitKind { Module, Lesson, Exercise, Card }

public sealed record SearchHit(SearchHitKind Kind, Guid ModuleId, string ModuleTitle, string Section, string Snippet);

/// <summary>Busca local, sem IA, em módulos, aulas, exercícios e cartões; ignora acentos e maiúsculas.</summary>
public static class ProjectSearch
{
    public static IReadOnlyList<SearchHit> Search(StudyProject project, string query, int take = 40)
    {
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];
        var hits = new List<(SearchHit Hit, int Score)>();
        foreach (var module in project.Modules.OrderBy(m => m.Order))
        {
            Try(SearchHitKind.Module, module, "Módulo", module.Title + "\n" + module.Objective, 3);
            if (module.Lesson is { } lesson)
            {
                Try(SearchHitKind.Lesson, module, "O que você vai aprender", lesson.Objectives, 2);
                Try(SearchHitKind.Lesson, module, "Entenda o assunto", lesson.Explanation, 2);
                Try(SearchHitKind.Lesson, module, "Exemplos", lesson.Examples, 2);
                Try(SearchHitKind.Lesson, module, "Resumo", lesson.Summary, 2);
            }
            foreach (var exercise in module.Exercises) Try(SearchHitKind.Exercise, module, "Exercício", exercise.Prompt, 1);
            foreach (var card in module.ReviewCards.Where(c => !c.IsArchived)) Try(SearchHitKind.Card, module, "Cartão", card.Question + "\n" + card.Answer, 1);
        }
        return hits.OrderByDescending(x => x.Score).Select(x => x.Hit).Take(take).ToList();

        void Try(SearchHitKind kind, StudyModule module, string section, string text, int weight)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var normalized = Normalize(text);
            if (!terms.All(normalized.Contains)) return;
            var occurrences = terms.Sum(t => Count(normalized, t));
            hits.Add((new SearchHit(kind, module.Id, module.Title, section, Snippet(text, normalized, terms[0])), occurrences * weight));
        }
    }

    /// <summary>Minúsculas, sem acentos e com espaços simples; preserva o comprimento para recortar trechos.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            var baseChar = decomposed.FirstOrDefault(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark);
            var value = baseChar == default ? c : baseChar;
            builder.Append(char.IsWhiteSpace(value) ? ' ' : char.ToLowerInvariant(value));
        }
        return builder.ToString();
    }

    private static int Count(string text, string term)
    {
        var count = 0;
        for (var i = text.IndexOf(term, StringComparison.Ordinal); i >= 0; i = text.IndexOf(term, i + term.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Snippet(string original, string normalized, string term)
    {
        var index = Math.Max(0, normalized.IndexOf(term, StringComparison.Ordinal));
        var start = Math.Max(0, index - 70);
        var length = Math.Min(original.Length - start, 200);
        var snippet = original.Substring(start, length).Replace("\r", " ").Replace('\n', ' ').Replace("**", "").Replace("`", "");
        snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"(^|\s)(#{1,6}|>|-{3,}|\*{3,})(?=\s)", " ");
        snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"\s{2,}", " ").Trim();
        return (start > 0 ? "…" : "") + snippet + (start + length < original.Length ? "…" : "");
    }
}
