using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    /// <summary>Mapa das últimas 12 semanas: cada quadrado é um dia, mais forte quanto mais estudo.</summary>
    private Border ActivityHeatmap(StudyProject project)
    {
        const int weeks = 12;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var events = StudyStats.ActivityCounts(project);
        // Começa na segunda-feira de 11 semanas atrás, para as colunas serem semanas completas.
        var offset = ((int)today.DayOfWeek + 6) % 7;
        var start = today.AddDays(-offset - (weeks - 1) * 7);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("18", weeks + 1))),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("18", 7)))
        };
        string[] dayNames = ["S", "T", "Q", "Q", "S", "S", "D"];
        for (var row = 0; row < 7; row++)
        {
            var name = Label(row % 2 == 0 ? dayNames[row] : "", 10, Muted);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(name, row);
            grid.Children.Add(name);
        }
        var activeDays = 0;
        var totalMinutes = 0;
        for (var week = 0; week < weeks; week++)
        for (var day = 0; day < 7; day++)
        {
            var date = start.AddDays(week * 7 + day);
            if (date > today) continue;
            var minutes = _projectFocusByDay.GetValueOrDefault(date) / 60;
            var score = events.GetValueOrDefault(date) + minutes / 15;
            if (score > 0) activeDays++;
            totalMinutes += minutes;
            var cell = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                Background = Brush.Parse(score switch { 0 => "#1E2229", <= 2 => "#1F4D40", <= 5 => "#2E8A6C", _ => "#44D7A8" }),
                BorderBrush = date == today ? Accent : null,
                BorderThickness = new Thickness(date == today ? 1 : 0)
            };
            ToolTip.SetTip(cell, $"{date:dd/MM/yyyy}: {events.GetValueOrDefault(date)} atividade(s), {minutes} min de foco");
            Grid.SetColumn(cell, week + 1);
            Grid.SetRow(cell, day);
            grid.Children.Add(cell);
        }
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        legend.Children.Add(Label("menos", 11, Muted));
        foreach (var color in new[] { "#1E2229", "#1F4D40", "#2E8A6C", "#44D7A8" })
            legend.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = Brush.Parse(color) });
        legend.Children.Add(Label("mais", 11, Muted));
        var streak = StudyStats.CurrentStreak(project, today);
        return Card(StackWith(
            Label("Mapa de constância", 19, Ink, FontWeight.Bold),
            Label($"{activeDays} dia(s) com estudo nas últimas {weeks} semanas · {totalMinutes} min de foco · sequência atual: {streak} dia(s)", 13, Muted),
            grid, legend));
    }

    /// <summary>Cartões mais errados e questões com pontos perdidos, com atalho para o tutor.</summary>
    private Control? WeakPointsCard(StudyProject project)
    {
        var cards = StudyStats.WeakCards(project, 4);
        var questions = StudyStats.WeakQuestions(project, 4);
        if (cards.Count == 0 && questions.Count == 0) return null;
        var body = Stack(10);
        body.Children.Add(Label("Pontos de atenção", 19, Ink, FontWeight.Bold));
        body.Children.Add(Label("Onde você mais errou recentemente. Peça ao tutor outra explicação antes de seguir.", 13, Muted));
        foreach (var (card, wrong, total) in cards)
            body.Children.Add(WeakRow($"Cartão · errou {wrong} de {total}", CleanInline(card.Question), card.ModuleId,
                $"Eu errei este cartão de revisão: \"{card.Question}\" (resposta esperada: \"{card.Answer}\"). Pode explicar de outro jeito, com um exemplo?"));
        foreach (var (module, question, awarded) in questions)
            body.Children.Add(WeakRow($"Prova · {module.Title} · {awarded}/{question.Weight} pts", CleanInline(question.Prompt), module.Id,
                $"Perdi pontos nesta questão da prova: \"{question.Prompt}\". O que eu precisava saber para acertar? Explique com um exemplo."));
        return Card(body, Brush.Parse("#1A1A14"));

        Control WeakRow(string title, string text, Guid moduleId, string tutorQuestion)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var info = StackWith(Label(title, 11, Brush.Parse("#FFC86B"), FontWeight.Bold), Label(text, 14, Ink));
            info.Spacing = 4;
            var ask = ActionButton("Perguntar ao tutor", () =>
            {
                _module = project.Modules.FirstOrDefault(m => m.Id == moduleId);
                _tutorDrafts[project.Id] = tutorQuestion;
                Navigate("tutor");
            }, false);
            ask.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ask, 1);
            row.Children.Add(info);
            row.Children.Add(ask);
            return row;
        }
    }
}
