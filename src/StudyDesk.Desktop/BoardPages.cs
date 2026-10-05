using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    /// <summary>Estado da lousa livre de cada projeto enquanto o app está aberto.</summary>
    private sealed class BoardSession
    {
        public WhiteboardScene? Scene { get; set; }
        public List<BoardStroke> Strokes { get; set; } = [];
    }

    private readonly Dictionary<Guid, BoardSession> _boards = [];

    private void ShowBoard()
    {
        if (_project is null) { Navigate("home"); return; }
        Header("Lousa", _module is null ? _project.Title : $"{_project.Title} · {_module.Title}");
        var session = _boards.TryGetValue(_project.Id, out var existing) ? existing : _boards[_project.Id] = new BoardSession();
        var board = new WhiteboardControl { Scene = session.Scene, Strokes = session.Strokes, DrawingEnabled = true, HorizontalAlignment = HorizontalAlignment.Left };
        var root = Stack(16);
        root.Children.Add(Label("Desenhe à mão, ou peça à IA um diagrama, fluxo ou exemplo passo a passo. A IA usa o material e as preferências do seu projeto.", 14, Muted));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (name, hex) in WhiteboardControl.Palette)
        {
            var swatch = new Button
            {
                Width = 30, Height = 30, Background = Brush.Parse(hex), CornerRadius = new CornerRadius(15),
                BorderBrush = Brush.Parse("#3A5248"), BorderThickness = new Thickness(2)
            };
            Avalonia.Automation.AutomationProperties.SetName(swatch, $"Cor {name}");
            swatch.Click += (_, _) => { board.PenColor = hex; board.EraserMode = false; };
            swatches.Children.Add(swatch);
        }
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        tools.Children.Add(swatches);
        tools.Children.Add(ActionButton("Fina", () => { board.PenThickness = 2; board.EraserMode = false; }, false));
        tools.Children.Add(ActionButton("Média", () => { board.PenThickness = 4; board.EraserMode = false; }, false));
        tools.Children.Add(ActionButton("Grossa", () => { board.PenThickness = 8; board.EraserMode = false; }, false));
        tools.Children.Add(ActionButton("Borracha", () => board.EraserMode = true, false));
        tools.Children.Add(ActionButton("Desfazer", board.Undo, false));
        tools.Children.Add(ActionButton("Limpar traços", board.ClearStrokes, false));
        tools.Children.Add(ActionButton("Limpar tudo", () => { session.Scene = null; board.Scene = null; board.ClearStrokes(); }, false));
        tools.Children.Add(ActionButton("Salvar imagem", () => SaveBoardImage(board), false));
        root.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = tools });
        root.Children.Add(board);

        var request = Field(null, "Ex.: mostre como funciona o ciclo de uma requisição HTTP, com um exemplo", true);
        request.MinHeight = 70;
        var caption = Label(session.Scene is { Caption.Length: > 0 } scene ? scene.Caption : "", 14, Muted);
        root.Children.Add(Card(StackWith(
            Label("Pedir um desenho à IA", 17, Ink, FontWeight.SemiBold),
            request,
            Actions(ActionButton("✦  Desenhar com IA", async () =>
            {
                var text = Required(request, "o que desenhar");
                Notice("A IA está desenhando na lousa…");
                await RunAsync(async () =>
                {
                    var result = await _service.GenerateBoardAsync(_project.Id, _module?.Id, text, Ct);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível desenhar.", true); return; }
                    session.Scene = result.Value;
                    board.Scene = result.Value;
                    caption.Text = result.Value!.Caption;
                    Notice(string.IsNullOrWhiteSpace(result.Value.Title) ? "Pronto. Desenhe por cima se quiser." : $"Pronto: {result.Value.Title}. Desenhe por cima se quiser.");
                });
            })),
            caption)));
        Display(root);
    }

    private void SaveBoardImage(WhiteboardControl board)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Estudio");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"lousa-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using (var stream = File.Create(path)) board.SavePng(stream);
            Notice($"Lousa salva em {path}");
            OpenFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice("Não foi possível salvar a imagem da lousa.", true);
        }
    }

    /// <summary>Cenas da lousa geradas para a aula, com navegação entre elas e atalho para desenhar por cima.</summary>
    private Border LessonBoardCard(Lesson lesson)
    {
        var scenes = lesson.Boards;
        var index = 0;
        var board = new WhiteboardControl { Scene = scenes[0], HorizontalAlignment = HorizontalAlignment.Left };
        var title = Label("", 17, Ink, FontWeight.SemiBold);
        var caption = Label("", 14, Muted);
        var position = Label("", 12, Teal, FontWeight.SemiBold);
        void Show()
        {
            board.Scene = scenes[index];
            title.Text = scenes[index].Title;
            caption.Text = scenes[index].Caption;
            position.Text = $"Cena {index + 1} de {scenes.Count}";
        }
        var previous = ActionButton("←", () => { index = (index - 1 + scenes.Count) % scenes.Count; Show(); }, false);
        var next = ActionButton("→", () => { index = (index + 1) % scenes.Count; Show(); }, false);
        previous.IsEnabled = next.IsEnabled = scenes.Count > 1;
        Show();
        return Card(StackWith(
            Label("LOUSA DA AULA", 12, Teal, FontWeight.Bold),
            title, board, caption,
            Actions(previous, position, next,
                ActionButton("Abrir na lousa para desenhar", () =>
                {
                    if (_project is null) return;
                    var session = _boards.TryGetValue(_project.Id, out var existing) ? existing : _boards[_project.Id] = new BoardSession();
                    session.Scene = scenes[index];
                    Navigate("board");
                }, false))), Brush.Parse("#131B18"));
    }
}
