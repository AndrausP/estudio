using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StudyDesk.Application;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private static readonly IBrush Danger = Brush.Parse("#F2766B");

    /// <summary>Resultado do diagnóstico antes de gerar a trilha: nível, nota, pontos fortes, lacunas e sugestões.</summary>
    private Control DiagnosticResultView(string summary, Action generate)
    {
        var parsed = DiagnosticSummary.Parse(TextRepair.Fix(summary));
        var root = Stack(16);
        root.MaxWidth = 980;
        root.HorizontalAlignment = HorizontalAlignment.Left;

        var top = Stack(10);
        top.Children.Add(Label("DIAGNÓSTICO CONCLUÍDO", 12, Teal, FontWeight.Bold));
        if (parsed.Level is not null) top.Children.Add(Label(parsed.Level, 24, Ink, FontWeight.Bold));
        if (parsed.Score is { } score)
        {
            top.Children.Add(Label($"Estimativa geral: {score} de 100", 14, Muted));
            top.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = score, Height = 8, Foreground = Teal, Background = Line, CornerRadius = new CornerRadius(4) });
        }
        root.Children.Add(Card(top));

        AddSection(root, "O que você mostrou", parsed.Observations, Teal, "✓");
        AddSection(root, "Lacunas principais", parsed.Gaps, Danger, "!");
        AddSection(root, "Sugestões para evoluir", parsed.Suggestions, Accent, "→");
        if (parsed.IsEmpty) root.Children.Add(Card(Label(summary, 14, Muted)));

        root.Children.Add(Card(StackWith(
            Label("Próximo passo", 18, Ink, FontWeight.Bold),
            Label("A IA vai montar módulos alinhados ao seu objetivo e a este diagnóstico. Você poderá revisar e editar tudo antes de começar.", 14, Muted),
            Actions(ActionButton("Gerar trilha", generate), DeleteTrailButton()))));
        return root;
    }

    private Button DeleteTrailButton()
    {
        var button = new Button
        {
            Content = "Excluir trilha", Padding = new Thickness(18, 11), CornerRadius = new CornerRadius(8),
            Background = Surface, BorderBrush = Danger, BorderThickness = new Thickness(1),
            Foreground = Danger, FontWeight = FontWeight.SemiBold
        };
        Avalonia.Automation.AutomationProperties.SetName(button, "Excluir esta trilha de estudo");
        button.Click += async (_, _) => { if (_project is not null) await DeleteProjectAsync(_project); };
        return button;
    }

    private static void AddSection(StackPanel root, string title, IReadOnlyList<string> items, IBrush color, string marker)
    {
        if (items.Count == 0) return;
        var body = Stack(10);
        body.Children.Add(Label(title, 17, Ink, FontWeight.SemiBold));
        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
            var badge = new TextBlock { Text = marker, Foreground = color, FontWeight = FontWeight.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Top };
            var text = Label(item, 14, Brush.Parse("#D8DDE6"));
            Grid.SetColumn(text, 1);
            row.Children.Add(badge);
            row.Children.Add(text);
            body.Children.Add(row);
        }
        root.Children.Add(Card(body));
    }
}
