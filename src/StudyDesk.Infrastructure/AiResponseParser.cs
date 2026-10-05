using System.Text.Json;
using StudyDesk.Domain;

namespace StudyDesk.Infrastructure;

internal static class AiResponseParser
{
    /// <summary>Isola o primeiro objeto ou array JSON balanceado, ignorando cercas de código e texto ao redor.</summary>
    internal static string ExtractJson(string raw)
    {
        var text = raw.Trim();
        var start = text.IndexOfAny(['{', '[']);
        if (start < 0) return text;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']' && --depth == 0) return text[start..(i + 1)];
        }
        return text[start..];
    }

    internal static T? Parse<T>(string raw, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(StudyDesk.Application.TextRepair.Fix(ExtractJson(raw)));
        var root = document.RootElement;
        if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>) && root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array && property.Name.ToLowerInvariant() is "questions" or "items" or "modules" or "exercises" or "cards")
                {
                    root = property.Value;
                    break;
                }
            }
        }
        if (typeof(T) == typeof(List<ExamQuestion>) && root.ValueKind == JsonValueKind.Array)
            return (T)(object)ParseQuestions(root, options);
        if (typeof(T) == typeof(Lesson) && root.ValueKind == JsonValueKind.Object)
            return (T)(object)new Lesson
            {
                Objectives = ReadText(root, "Objectives"),
                Explanation = ReadText(root, "Explanation"),
                Examples = ReadText(root, "Examples"),
                Summary = ReadText(root, "Summary"),
                Boards = ReadBoards(root, options)
            };
        return root.Deserialize<T>(options);
    }

    private static List<ExamQuestion> ParseQuestions(JsonElement array, JsonSerializerOptions options)
    {
        var questions = new List<ExamQuestion>();
        foreach (var element in array.EnumerateArray())
        {
            var question = new ExamQuestion
            {
                Kind = Read(element, "Kind").Deserialize<QuestionKind>(options),
                Prompt = Read(element, "Prompt").GetString() ?? "",
                CorrectAnswer = Read(element, "CorrectAnswer").ToString(),
                Rubric = Read(element, "Rubric").GetString() ?? "",
                Weight = Read(element, "Weight").GetInt32()
            };
            var sourceOptions = Read(element, "Options");
            if (sourceOptions.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in sourceOptions.EnumerateArray())
                {
                    if (option.ValueKind == JsonValueKind.String) { question.Options.Add(option.GetString() ?? ""); continue; }
                    if (option.ValueKind != JsonValueKind.Object) throw new JsonException("Alternativa inválida.");
                    var label = Read(option, "label").ToString();
                    var value = Read(option, "value").ToString();
                    question.Options.Add(label);
                    if (question.CorrectAnswer == value || question.CorrectAnswer == label) question.CorrectAnswer = label;
                }
            }
            question.Prompt = question.Prompt.Trim();
            question.Options = question.Options.Select(x => x.Trim()).ToList();
            question.CorrectAnswer = NormalizeCorrectAnswer(question.CorrectAnswer, question.Options);
            questions.Add(question);
        }
        return questions;
    }

    /// <summary>Converte gabaritos como "B", "b)", "2" ou "B) texto" no texto exato da alternativa.</summary>
    internal static string NormalizeCorrectAnswer(string correct, IReadOnlyList<string> options)
    {
        var value = (correct ?? "").Trim();
        if (options.Count == 0 || options.Contains(value)) return value;
        var exact = options.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var stripped = StripMarker(value);
        exact = options.FirstOrDefault(x => string.Equals(StripMarker(x), stripped, StringComparison.OrdinalIgnoreCase));
        if (exact is not null && stripped.Length > 1) return exact;
        var marker = value.TrimEnd(')', '.', ':', ' ').Trim();
        if (marker.Length == 1 && char.IsLetter(marker[0]))
        {
            var index = char.ToUpperInvariant(marker[0]) - 'A';
            if (index >= 0 && index < options.Count) return options[index];
        }
        if (int.TryParse(marker, out var number))
        {
            // Aceita índice baseado em 1 (mais comum em texto) e em 0 quando inequívoco.
            if (number >= 1 && number <= options.Count) return options[number - 1];
            if (number == 0) return options[0];
        }
        if (value.Length > 2 && char.IsLetter(value[0]) && value[1] is ')' or '.' or ':')
        {
            var index = char.ToUpperInvariant(value[0]) - 'A';
            if (index >= 0 && index < options.Count) return options[index];
        }
        return value;

        static string StripMarker(string text)
        {
            var t = text.Trim();
            if (t.Length > 2 && char.IsLetterOrDigit(t[0]) && t[1] is ')' or '.' or ':' or '-') return t[2..].Trim();
            if (t.Length > 3 && t[0] == '(' && t[2] == ')') return t[3..].Trim();
            return t;
        }
    }

    /// <summary>Lê as cenas da lousa sem derrubar a aula: cena malformada é descartada.</summary>
    private static List<WhiteboardScene> ReadBoards(JsonElement lesson, JsonSerializerOptions options)
    {
        var boards = new List<WhiteboardScene>();
        foreach (var property in lesson.EnumerateObject())
        {
            if (!property.Name.Equals("Boards", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var element in property.Value.EnumerateArray())
            {
                try { if (element.Deserialize<WhiteboardScene>(options) is { } scene) boards.Add(scene); }
                catch (JsonException) { }
            }
        }
        return boards;
    }

    private static JsonElement Read(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new JsonException($"Campo obrigatório ausente: {name}.");
    }

    private static string ReadText(JsonElement element, string name)
    {
        var value = Read(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Array => string.Join("\n\n", value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : throw new JsonException($"Campo {name} contém item inválido."))),
            _ => throw new JsonException($"Campo {name} precisa ser texto ou lista de textos.")
        };
    }
}
