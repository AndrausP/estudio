using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    // Renderer pequeno para o subconjunto de Markdown usado nas aulas geradas.
    // Conteúdo da IA permanece texto: não executamos código nem carregamos HTML.
    private static StackPanel MarkdownBlocks(string? markdown)
    {
        var result = Stack(12);
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var code = new List<string>();
        var inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            result.Children.Add(Label(CleanInline(string.Join(" ", paragraph)), 16, Ink));
            paragraph.Clear();
        }

        void FlushCode()
        {
            var content = string.Join("\n", code).TrimEnd();
            if (content.Length > 0)
            {
                var snippet = new TextBlock
                {
                    Text = content,
                    FontFamily = new FontFamily("Consolas, Cascadia Code, monospace"),
                    FontSize = 13,
                    Foreground = Brush.Parse("#DBF5E9"),
                    TextWrapping = TextWrapping.NoWrap
                };
                result.Children.Add(new Border
                {
                    Background = Brush.Parse("#101216"),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16),
                    Child = new ScrollViewer { Content = snippet, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
                });
            }
            code.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("```"))
            {
                FlushParagraph();
                if (inCode) FlushCode();
                inCode = !inCode;
                continue;
            }
            if (inCode) { code.Add(raw); continue; }
            if (line.Length == 0) { FlushParagraph(); continue; }
            if (line.StartsWith('#') && line.TrimStart('#').StartsWith(' '))
            {
                FlushParagraph();
                var level = line.TakeWhile(c => c == '#').Count();
                result.Children.Add(Label(CleanInline(line[(level + 1)..]), level == 1 ? 21 : level == 2 ? 18 : 16,
                    Ink, FontWeight.Bold));
                continue;
            }
            if (line is "---" or "***" or "___")
            {
                FlushParagraph();
                result.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 4) });
                continue;
            }
            if (line.StartsWith("> ") || line == ">")
            {
                FlushParagraph();
                result.Children.Add(new Border
                {
                    BorderBrush = Teal, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 4),
                    Child = Label(CleanInline(line.TrimStart('>').Trim()), 15, Muted)
                });
                continue;
            }
            if (line.StartsWith("- ") || line.StartsWith("* ") || IsNumberedItem(line))
            {
                FlushParagraph();
                var numbered = !(line.StartsWith("- ") || line.StartsWith("* "));
                var item = numbered ? line[(line.IndexOf('.') + 2)..] : line[2..];
                var bullet = numbered ? line[..line.IndexOf('.')] + "." : "•";
                var indent = raw.TakeWhile(char.IsWhiteSpace).Count() >= 2 ? 18 : 0;
                var label = Label($"{bullet}  " + CleanInline(item), 15, Ink);
                label.Margin = new Thickness(indent, 0, 0, 0);
                result.Children.Add(label);
                continue;
            }
            paragraph.Add(line);
        }
        FlushParagraph();
        if (inCode) FlushCode();
        if (result.Children.Count == 0) result.Children.Add(Label("Conteúdo indisponível nesta seção.", 14, Muted));
        return result;
    }

    private static bool IsNumberedItem(string value)
    {
        var marker = value.IndexOf(". ", StringComparison.Ordinal);
        return marker is > 0 and < 4 && int.TryParse(value[..marker], out _);
    }

    private static string CleanInline(string value)
    {
        var text = value.Replace("**", "").Replace("__", "").Replace("`", "");
        // Itálico simples (*texto*) e links [texto](url) viram texto puro.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\w*])\*(?!\s)([^*]+?)(?<!\s)\*(?![\w*])", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");
        return text.Trim();
    }

    private static Border RawAnswerBlock(string? answer)
    {
        var text = new TextBlock
        {
            Text = answer ?? "",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            Foreground = Ink,
            TextWrapping = TextWrapping.NoWrap
        };
        return new Border
        {
            Background = Brush.Parse("#101216"),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new ScrollViewer
            {
                Content = text,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };
    }
}
