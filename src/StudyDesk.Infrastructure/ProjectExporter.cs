using System.Text;
using StudyDesk.Domain;

namespace StudyDesk.Infrastructure;

/// <summary>Gera um caderno Markdown com aulas, cartões e histórico de provas do projeto.</summary>
public static class ProjectExporter
{
    public static string ToMarkdown(StudyProject project)
    {
        var md = new StringBuilder();
        md.AppendLine($"# {project.Title}").AppendLine();
        md.AppendLine($"> {project.Goal}").AppendLine();
        var done = project.Modules.Count(m => m.Status == ModuleStatus.Completed);
        md.AppendLine($"- Módulos concluídos: {done}/{project.Modules.Count}");
        md.AppendLine($"- Tempo de foco: {project.FocusMinutes} min");
        md.AppendLine($"- Exportado em: {DateTime.Now:dd/MM/yyyy HH:mm}").AppendLine();
        if (!string.IsNullOrWhiteSpace(project.Diagnostic?.Summary))
            md.AppendLine("## Diagnóstico").AppendLine().AppendLine(project.Diagnostic!.Summary.Trim()).AppendLine();

        foreach (var module in project.Modules.OrderBy(m => m.Order))
        {
            md.AppendLine($"## Módulo {module.Order + 1:00} · {module.Title}").AppendLine();
            if (!string.IsNullOrWhiteSpace(module.Objective)) md.AppendLine($"*Objetivo:* {module.Objective.Trim()}").AppendLine();
            if (module.Lesson is { } lesson)
            {
                Section("O que você vai aprender", lesson.Objectives);
                Section("Explicação", lesson.Explanation);
                Section("Exemplos", lesson.Examples);
                Section("Resumo", lesson.Summary);
            }
            else md.AppendLine("_Aula ainda não gerada._").AppendLine();

            var cards = module.ReviewCards.Where(c => !c.IsArchived).ToList();
            if (cards.Count > 0)
            {
                md.AppendLine("### Cartões de revisão").AppendLine();
                foreach (var card in cards)
                    md.AppendLine($"- **{OneLine(card.Question)}**  ").AppendLine($"  {OneLine(card.Answer)}");
                md.AppendLine();
            }
            if (module.Attempts.Count > 0)
            {
                md.AppendLine("### Provas").AppendLine();
                foreach (var attempt in module.Attempts.OrderBy(a => a.CreatedAt))
                    md.AppendLine($"- {attempt.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {attempt.Score}% · {(attempt.Passed ? "aprovado" : "revisar")}");
                md.AppendLine();
            }
        }
        return md.ToString();

        void Section(string title, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            md.AppendLine($"### {title}").AppendLine().AppendLine(content.Trim()).AppendLine();
        }
    }

    /// <summary>Nome de arquivo seguro no Windows a partir do título do projeto.</summary>
    public static string SafeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var name = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "projeto" : name.Length > 80 ? name[..80] : name;
    }

    private static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
}
