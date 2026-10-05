using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

/// <summary>Traço livre desenhado pelo aluno; pontos em fração (0 a 1) da largura e da altura.</summary>
internal sealed class BoardStroke(string color, double thickness)
{
    public string Color { get; } = color;
    public double Thickness { get; } = thickness;
    public List<Point> Points { get; } = [];
}

/// <summary>Lousa: mostra uma cena (formas posicionadas em % do quadro) e, se habilitado, deixa o aluno desenhar por cima.</summary>
internal sealed class WhiteboardControl : Control
{
    private const double AspectRatio = 0.58;
    private static readonly Dictionary<string, string> Chalk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "#F2F4F7", ["yellow"] = "#FFE27A", ["green"] = "#7CE3A1", ["blue"] = "#7AB8FF",
        ["red"] = "#FF8A80", ["orange"] = "#FFB066", ["purple"] = "#C59BFF"
    };

    private WhiteboardScene? _scene;
    private BoardStroke? _current;

    public List<BoardStroke> Strokes { get; set; } = [];
    public bool DrawingEnabled { get; set; }
    public bool EraserMode { get; set; }
    public string PenColor { get; set; } = "#F2F4F7";
    public double PenThickness { get; set; } = 3;

    public WhiteboardScene? Scene
    {
        get => _scene;
        set { _scene = value; InvalidateVisual(); }
    }

    public static IReadOnlyList<(string Name, string Hex)> Palette => Chalk.Select(x => (x.Key, x.Value)).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 900 : Math.Min(availableSize.Width, 1000);
        return new Size(width, Math.Max(240, width * AspectRatio));
    }

    public void Undo()
    {
        if (Strokes.Count == 0) return;
        Strokes.RemoveAt(Strokes.Count - 1);
        InvalidateVisual();
    }

    public void ClearStrokes()
    {
        Strokes.Clear();
        InvalidateVisual();
    }

    /// <summary>Renderiza a lousa em PNG.</summary>
    public void SavePng(Stream stream)
    {
        var size = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(this);
        bitmap.Save(stream);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var frame = new Rect(0, 0, width, height);
        context.DrawRectangle(Brush.Parse("#10231D"), new Pen(Brush.Parse("#3A5248"), 2), frame, 14, 14);

        var grid = new Pen(new SolidColorBrush(Color.Parse("#FFFFFF"), 0.04), 1);
        for (var i = 1; i < 10; i++)
        {
            context.DrawLine(grid, new Point(width * i / 10, 0), new Point(width * i / 10, height));
            context.DrawLine(grid, new Point(0, height * i / 10), new Point(width, height * i / 10));
        }

        if (_scene is not null)
        {
            var fontSize = Math.Clamp(width / 56, 11, 19);
            foreach (var item in _scene.Items) DrawItem(context, item, width, height, fontSize);
        }
        else if (Strokes.Count == 0 && !DrawingEnabled)
            DrawCenteredText(context, "Lousa vazia", new Rect(0, 0, width, height), Brush.Parse("#6F8A7F"), 16);

        foreach (var stroke in Strokes) DrawStroke(context, stroke, width, height);
        if (_current is not null) DrawStroke(context, _current, width, height);
    }

    private static IBrush ChalkBrush(string? name) => Brush.Parse(!string.IsNullOrWhiteSpace(name) && Chalk.TryGetValue(name, out var hex) ? hex : Chalk["white"]);

    private static void DrawItem(DrawingContext context, BoardItem item, double width, double height, double fontSize)
    {
        var color = ChalkBrush(item.Color);
        var pen = new Pen(color, 2.2, lineCap: PenLineCap.Round);
        var rect = new Rect(item.X / 100 * width, item.Y / 100 * height, Math.Max(24, item.W / 100 * width), Math.Max(20, item.H / 100 * height));
        switch (item.Type)
        {
            case "box":
                context.DrawRectangle(new SolidColorBrush(((ISolidColorBrush)color).Color, 0.10), pen, rect, 8, 8);
                DrawCenteredText(context, item.Text, rect.Deflate(6), color, fontSize);
                break;
            case "circle":
                context.DrawEllipse(new SolidColorBrush(((ISolidColorBrush)color).Color, 0.10), pen, rect.Center, rect.Width / 2, rect.Height / 2);
                DrawCenteredText(context, item.Text, rect.Deflate(Math.Min(rect.Width, rect.Height) * 0.15), color, fontSize);
                break;
            case "note":
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#FFE27A"), 0.92), null, rect, 4, 4);
                DrawCenteredText(context, item.Text, rect.Deflate(6), Brush.Parse("#2B2A1E"), fontSize);
                break;
            case "text":
                DrawText(context, item.Text, new Point(rect.X, rect.Y), color, fontSize * 1.1, Math.Max(40, width - rect.X - 8));
                break;
            case "line":
                context.DrawLine(pen, new Point(item.X / 100 * width, item.Y / 100 * height), new Point(item.X2 / 100 * width, item.Y2 / 100 * height));
                break;
            case "arrow":
                DrawArrow(context, pen, color, new Point(item.X / 100 * width, item.Y / 100 * height), new Point(item.X2 / 100 * width, item.Y2 / 100 * height));
                break;
        }
    }

    private static void DrawArrow(DrawingContext context, Pen pen, IBrush brush, Point from, Point to)
    {
        context.DrawLine(pen, from, to);
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double head = 11;
        var left = new Point(to.X - head * Math.Cos(angle - 0.45), to.Y - head * Math.Sin(angle - 0.45));
        var right = new Point(to.X - head * Math.Cos(angle + 0.45), to.Y - head * Math.Sin(angle + 0.45));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(to, true);
            g.LineTo(left);
            g.LineTo(right);
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawStroke(DrawingContext context, BoardStroke stroke, double width, double height)
    {
        if (stroke.Points.Count == 0) return;
        var pen = new Pen(Brush.Parse(stroke.Color), stroke.Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (stroke.Points.Count == 1)
        {
            var p = new Point(stroke.Points[0].X * width, stroke.Points[0].Y * height);
            context.DrawEllipse(pen.Brush, null, p, stroke.Thickness / 2, stroke.Thickness / 2);
            return;
        }
        for (var i = 1; i < stroke.Points.Count; i++)
            context.DrawLine(pen, new Point(stroke.Points[i - 1].X * width, stroke.Points[i - 1].Y * height),
                new Point(stroke.Points[i].X * width, stroke.Points[i].Y * height));
    }

    private static FormattedText Format(string text, IBrush brush, double size, double maxWidth, double maxHeight = double.PositiveInfinity, TextAlignment alignment = TextAlignment.Left)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush)
        {
            MaxTextWidth = Math.Max(10, maxWidth), MaxTextHeight = maxHeight, TextAlignment = alignment, Trimming = TextTrimming.CharacterEllipsis
        };

    private static void DrawText(DrawingContext context, string text, Point origin, IBrush brush, double size, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        context.DrawText(Format(text, brush, size, maxWidth), origin);
    }

    private static void DrawCenteredText(DrawingContext context, string text, Rect area, IBrush brush, double size)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var formatted = Format(text, brush, size, area.Width, area.Height, TextAlignment.Center);
        context.DrawText(formatted, new Point(area.X, area.Y + Math.Max(0, (area.Height - formatted.Height) / 2)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!DrawingEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = Normalize(e.GetPosition(this));
        if (EraserMode) { EraseAt(point); e.Pointer.Capture(this); return; }
        _current = new BoardStroke(PenColor, PenThickness);
        _current.Points.Add(point);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!DrawingEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = Normalize(e.GetPosition(this));
        if (EraserMode) { EraseAt(point); return; }
        if (_current is null) return;
        _current.Points.Add(point);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_current is not null) { Strokes.Add(_current); _current = null; InvalidateVisual(); }
        e.Pointer.Capture(null);
    }

    private Point Normalize(Point p) => new(Math.Clamp(p.X / Math.Max(1, Bounds.Width), 0, 1), Math.Clamp(p.Y / Math.Max(1, Bounds.Height), 0, 1));

    private void EraseAt(Point p)
    {
        var removed = Strokes.RemoveAll(stroke => stroke.Points.Any(q => Math.Abs(q.X - p.X) < 0.025 && Math.Abs(q.Y - p.Y) < 0.04));
        if (removed > 0) InvalidateVisual();
    }
}
