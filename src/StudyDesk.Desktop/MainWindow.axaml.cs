using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StudyDesk.Application;
using StudyDesk.Domain;
using StudyDesk.Infrastructure;

namespace StudyDesk.Desktop;

public partial class MainWindow : Window
{
    private readonly IStudyService _service;
    private readonly string _dataDirectory;
    private IReadOnlyList<StudyProject> _projects = [];
    private IReadOnlyList<ProviderConfiguration> _providers = [];
    private static readonly ProviderKind[] SupportedProviders = [ProviderKind.ClaudeCli, ProviderKind.OpenAiCompatible];
    private readonly Dictionary<Guid, string> _questionDrafts = [];
    private readonly Dictionary<Guid, string> _practiceDrafts = [];
    private Guid? _pendingPracticeFeedbackId;
    private readonly Dictionary<Guid, List<(bool FromUser, string Text)>> _tutorHistory = [];
    private readonly Dictionary<Guid, string> _tutorDrafts = [];
    private readonly Dictionary<Guid, bool> _codeAnswerModes = [];
    private readonly HashSet<Guid> _reviewGenerationInFlight = [];
    private readonly Dictionary<Guid, (string Question, string Answer)> _reviewEditDrafts = [];
    private StudyProject? _project;
    private StudyModule? _module;
    private string _page = "welcome";
    private bool _busy;
    private bool _trailHasUnsavedEdits;
    private bool _checkpointInFlight;
    private bool _closingAfterCheckpoint;
    private bool? _dashboardStacked;
    private Guid? _focusProjectId;
    private Guid? _focusModuleId;
    private readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private FocusSession? _focus;
    private int _focusLength = 25;
    private int _breakLength = 5;
    private DateTime? _focusStartedUi;
    private bool _onBreak;
    private CancellationTokenSource? _operationCts;
    private DateTime _operationStartedUtc;
    private readonly DispatcherTimer _busyTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>Token da operação em andamento; o botão Cancelar interrompe chamadas de IA.</summary>
    private CancellationToken Ct => _operationCts?.Token ?? CancellationToken.None;
    private IReadOnlyDictionary<DateOnly, int> _focusByDay = new Dictionary<DateOnly, int>();
    private IReadOnlyDictionary<DateOnly, int> _projectFocusByDay = new Dictionary<DateOnly, int>();
    private string _searchQuery = "";

    private static readonly IBrush Ink = Brush.Parse("#F2F4F7");
    private static readonly IBrush Muted = Brush.Parse("#A6ADB8");
    private static readonly IBrush Teal = Brush.Parse("#44D7A8");
    private static readonly IBrush Accent = Brush.Parse("#5C75FF");
    private static readonly IBrush PrimaryAccent = Brush.Parse("#435BD8");
    private static readonly IBrush Line = Brush.Parse("#292E36");
    private static readonly IBrush Surface = Brush.Parse("#15181D");

    public MainWindow()
    {
        InitializeComponent();
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Estudio");
        _dataDirectory = dataDirectory;
        _service = StudyService.CreateDefault(dataDirectory);
        NewProjectButton.Click += (_, _) => Navigate("new");
        ProviderButton.Click += (_, _) => Navigate("providers");
        HomeButton.Click += (_, _) => Navigate("home");
        TrailButton.Click += (_, _) => Navigate("trail");
        ReviewButton.Click += (_, _) => Navigate("review");
        ProgressButton.Click += (_, _) => Navigate("progress");
        TutorButton.Click += (_, _) => Navigate("tutor");
        FocusButton.Click += (_, _) => Navigate("focus");
        foreach (var (button, shortcut) in new[] { (HomeButton, "Ctrl+1"), (TrailButton, "Ctrl+2"), (ReviewButton, "Ctrl+3"),
            (ProgressButton, "Ctrl+4"), (TutorButton, "Ctrl+5"), (FocusButton, "Ctrl+6") })
            ToolTip.SetTip(button, shortcut);
        CancelOperationButton.Click += (_, _) =>
        {
            _operationCts?.Cancel();
            CancelOperationButton.IsEnabled = false;
            NoticeText.Text = "Cancelando…";
        };
        CloseNoticeButton.Click += (_, _) => ClearNotice();
        _busyTimer.Tick += (_, _) => UpdateBusyElapsed();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_project is null) { Notice("Abra um projeto para buscar.", true); return; }
            _searchQuery = SearchBox.Text?.Trim() ?? "";
            Navigate("search");
        };
        _focusTimer.Tick += async (_, _) =>
        {
            await CheckpointFocusTickAsync();
            if (_page == "focus") RenderFocusClock();
            else if (!_busy && _focus?.IsRunning == true && _focusClock is not null && FocusElapsedSeconds() >= _focusLength * 60)
            {
                await PauseFocusAsync();
                CallAttention("Ciclo de foco concluído! Faça uma pausa curta.");
            }
        };
        _focusTimer.Start();
        Opened += async (_, _) => await LoadAsync();
        SizeChanged += (_, _) =>
        {
            if (_page == "home" && _project?.Stage == ProjectStage.Studying && !_busy &&
                _dashboardStacked != (ClientSize.Width < 1280)) ShowProjectHome();
        };
        Closing += (_, e) =>
        {
            if (_closingAfterCheckpoint || _focus?.IsRunning != true || _focusProjectId is null) return;
            e.Cancel = true;
            _ = CloseAfterCheckpointAsync();
        };
    }

    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            _providers = (await _service.ListProvidersAsync()).Where(p => SupportedProviders.Contains(p.Kind)).ToArray();
            _projects = await _service.ListProjectsAsync();
            if (_projects.Count > 0)
            {
                var lastId = ReadLastProjectId();
                _project = _projects.FirstOrDefault(p => p.Id == lastId) ??
                    _projects.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt).First();
            }
            await RefreshFocusStatsAsync();
            RenderSidebar();
            Navigate(_projects.Count == 0 ? "welcome" : "home");
        });
    }

    private string LastProjectPath => Path.Combine(_dataDirectory, "last-project.txt");
    private string AppSettingsPath => Path.Combine(_dataDirectory, "app-settings.json");

    private sealed class AppSettings { public int DailyGoalMinutes { get; set; } = 30; }

    private AppSettings ReadAppSettings()
    {
        try
        {
            return File.Exists(AppSettingsPath)
                ? System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    private void SaveAppSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            File.WriteAllText(AppSettingsPath, System.Text.Json.JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) { Notice("Não foi possível salvar a preferência: " + ex.Message, true); }
    }

    private Guid ReadLastProjectId()
    {
        try { return Guid.TryParse(File.ReadAllText(LastProjectPath), out var id) ? id : Guid.Empty; }
        catch { return Guid.Empty; }
    }

    private void RememberProject(Guid projectId)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            File.WriteAllText(LastProjectPath, projectId.ToString("D"));
        }
        catch { /* A navegação continua mesmo se a preferência não puder ser gravada. */ }
    }

    private async Task RefreshProjectAsync()
    {
        if (_project is null) return;
        var result = await _service.GetProjectAsync(_project.Id);
        if (!result.IsSuccess || result.Value is null) { Notice(result.Error ?? "Projeto indisponível.", true); return; }
        _project = result.Value;
        _module = _module is null ? null : _project.Modules.FirstOrDefault(m => m.Id == _module.Id);
        _projects = await _service.ListProjectsAsync();
        await RefreshFocusStatsAsync();
        RenderSidebar();
    }

    /// <summary>Atualiza os minutos de foco por dia usados na meta diária e no mapa de atividade.</summary>
    private async Task RefreshFocusStatsAsync()
    {
        try
        {
            _focusByDay = await _service.FocusSecondsByDayAsync();
            _projectFocusByDay = _project is null ? new Dictionary<DateOnly, int>() : await _service.FocusSecondsByDayAsync(_project.Id);
        }
        catch (Exception) { /* estatística opcional: não bloqueia a navegação */ }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _operationCts = new CancellationTokenSource();
        _operationStartedUtc = DateTime.UtcNow;
        SetNavigationEnabled(false);
        if (!NoticeBorder.IsVisible) Notice("Aguarde, estamos preparando o próximo passo…");
        BusyIndicator.IsVisible = true;
        CloseNoticeButton.IsVisible = false;
        CancelOperationButton.IsEnabled = true;
        _busyTimer.Start();
        PageContent.IsEnabled = false;
        PageContent.Opacity = 0.65;
        try { await action(); }
        catch (OperationCanceledException) when (_operationCts.IsCancellationRequested)
        { Notice("Operação cancelada. Nada do seu progresso foi perdido; tente de novo quando quiser."); }
        catch (Exception ex) { Notice("Não foi possível concluir: " + ex.Message, true); }
        finally
        {
            _busy = false;
            _busyTimer.Stop();
            _operationCts.Dispose();
            _operationCts = null;
            BusyIndicator.IsVisible = false;
            BusyElapsed.IsVisible = false;
            CancelOperationButton.IsVisible = false;
            CloseNoticeButton.IsVisible = NoticeBorder.IsVisible;
            PageContent.IsEnabled = true;
            PageContent.Opacity = 1;
            SetNavigationEnabled(true);
        }
    }

    /// <summary>Mostra o tempo de espera e libera o cancelamento quando a IA demora.</summary>
    private void UpdateBusyElapsed()
    {
        if (!_busy) return;
        var seconds = (int)(DateTime.UtcNow - _operationStartedUtc).TotalSeconds;
        if (seconds < 2) return;
        BusyElapsed.IsVisible = true;
        BusyElapsed.Text = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}min {seconds % 60:00}s";
        CancelOperationButton.IsVisible = _operationCts is not null;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_busy && e.KeyModifiers == KeyModifiers.Control)
        {
            var route = e.Key switch
            {
                Key.D1 or Key.NumPad1 => "home", Key.D2 or Key.NumPad2 => "trail", Key.D3 or Key.NumPad3 => "review",
                Key.D4 or Key.NumPad4 => "progress", Key.D5 or Key.NumPad5 => "tutor", Key.D6 or Key.NumPad6 => "focus", _ => null
            };
            if (e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
            if (route is not null && _project is not null) { Navigate(route); e.Handled = true; return; }
        }
        if (_busy || _page != "review" || e.Source is TextBox || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Space or Key.R when _reviewReveal is not null && !_reviewRevealed:
                _reviewReveal(); e.Handled = true; break;
            case Key.D1 or Key.NumPad1 when _reviewRevealed && _reviewGrade is not null:
                _ = _reviewGrade(ReviewGrade.Wrong); e.Handled = true; break;
            case Key.D2 or Key.NumPad2 when _reviewRevealed && _reviewGrade is not null:
                _ = _reviewGrade(ReviewGrade.Correct); e.Handled = true; break;
            case Key.D3 or Key.NumPad3 when _reviewRevealed && _reviewGrade is not null:
                _ = _reviewGrade(ReviewGrade.Easy); e.Handled = true; break;
        }
    }

    private void SetNavigationEnabled(bool enabled)
    {
        NewProjectButton.IsEnabled = enabled;
        ProviderButton.IsEnabled = enabled;
        ProjectList.IsEnabled = enabled;
        HomeButton.IsEnabled = enabled && _project is not null;
        TrailButton.IsEnabled = enabled && _project is not null;
        ReviewButton.IsEnabled = enabled && _project is not null;
        ProgressButton.IsEnabled = enabled && _project is not null;
        TutorButton.IsEnabled = enabled && _project is not null;
        FocusButton.IsEnabled = enabled && _project is not null;
    }

    private void Notice(string message, bool error = false)
    {
        NoticeText.Text = message;
        NoticeText.Foreground = Brush.Parse(error ? "#FFB2C0" : "#A5F4E3");
        NoticeBorder.Background = Brush.Parse(error ? "#4B2634" : "#153C3E");
        NoticeBorder.IsVisible = true;
        CloseNoticeButton.IsVisible = !_busy;
    }

    private void ClearNotice() => NoticeBorder.IsVisible = false;

    private void RenderSidebar()
    {
        ProjectList.Children.Clear();
        SetNavigationEnabled(!_busy);
        if (_projects.Count == 0)
        {
            ProjectList.Children.Add(Label("Nenhum projeto ainda.", 12, Muted));
            return;
        }
        foreach (var project in _projects)
        {
            var current = _project?.Id == project.Id;
            var button = new Button
            {
                Content = project.Title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brush.Parse(current ? "#252E48" : "#0D0F12"),
                Foreground = Brush.Parse(current ? "#E1E6FF" : "#CDD2DC"),
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(8)
            };
            button.Click += async (_, _) => await OpenProjectAsync(project.Id);
            var remove = new Button
            {
                Content = "✕",
                Background = Brush.Parse("#0D0F12"),
                Foreground = Brush.Parse("#CDD2DC"),
                Padding = new Thickness(10, 10),
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            ToolTip.SetTip(remove, "Excluir trilha");
            Avalonia.Automation.AutomationProperties.SetName(remove, $"Excluir trilha {project.Title}");
            remove.IsEnabled = !_busy;
            remove.Click += async (_, _) => await DeleteProjectAsync(project);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
            Grid.SetColumn(remove, 1);
            row.Children.Add(button);
            row.Children.Add(remove);
            ProjectList.Children.Add(row);
        }
    }

    private async Task OpenProjectAsync(Guid id)
    {
        if (_busy) return;
        if (!await ConfirmDiscardTrailAsync()) return;
        await RunAsync(async () =>
        {
            if (_focus?.IsRunning == true && _focusProjectId is { } previousId && previousId != id)
            {
                await _service.PauseFocusAsync(previousId, _focusModuleId);
                _focus = null;
            }
            var result = await _service.GetProjectAsync(id);
            if (!result.IsSuccess || result.Value is null) { Notice(result.Error ?? "Projeto indisponível.", true); return; }
            _project = result.Value;
            RememberProject(id);
            _module = null;
            await RefreshFocusStatsAsync();
            RenderSidebar();
            Navigate("home");
        });
    }

    private void Navigate(string page)
    {
        if (_page == "trail" && page != "trail" && _trailHasUnsavedEdits)
        {
            _ = ConfirmAndNavigateAsync(page);
            return;
        }
        _page = page;
        _reviewReveal = null;
        _reviewGrade = null;
        _reviewRevealed = false;
        if (page != "review") _extraPractice = false;
        ClearNotice();
        UpdateNavigationSelection();
        switch (page)
        {
            case "welcome": ShowWelcome(); break;
            case "new": ShowNewProject(); break;
            case "providers": ShowProviders(); break;
            case "home": ShowProjectHome(); break;
            case "diagnostic": ShowDiagnostic(); break;
            case "trail": ShowTrail(); break;
            case "review": ShowReviewPage(); break;
            case "lesson": ShowLesson(); break;
            case "practice": ShowPractice(); break;
            case "exam": ShowExam(); break;
            case "progress": ShowProgress(); _ = RefreshProgressViewAsync(); break;
            case "focus": ShowFocus(); break;
            case "tutor": ShowTutorPage(); break;
            case "project": ShowProjectSettings(); break;
            case "search": ShowSearch(); break;
        }
    }

    private void UpdateNavigationSelection()
    {
        foreach (var (button, route) in new[]
        {
            (HomeButton, "home"), (TrailButton, "trail"), (ReviewButton, "review"), (ProgressButton, "progress"),
            (TutorButton, "tutor"), (FocusButton, "focus")
        })
        {
            var active = _page == route;
            button.Background = Brush.Parse(active ? "#252E48" : "#0D0F12");
            button.Foreground = Brush.Parse(active ? "#F2F4F7" : "#B8BECA");
            button.BorderBrush = active ? Accent : Brushes.Transparent;
            button.BorderThickness = new Thickness(active ? 1 : 0);
            button.Padding = new Thickness(12, 9);
        }
    }

    private async Task ConfirmAndNavigateAsync(string page)
    {
        if (!await ConfirmDiscardTrailAsync()) return;
        Navigate(page);
    }

    private async Task RefreshProgressViewAsync()
    {
        await RefreshProjectAsync();
        if (_page == "progress") ShowProgress();
    }

    private async Task<bool> ConfirmDiscardTrailAsync()
    {
        if (_page != "trail" || !_trailHasUnsavedEdits) return true;
        var dialog = new Window { Title = "Alterações não salvas", Width = 440, Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        dialog.Content = Card(StackWith(
            Label("Descartar alterações da trilha?", 19, Ink, FontWeight.Bold),
            Label("Os ajustes ainda não foram salvos. Você pode voltar e salvar a trilha antes de sair.", 14, Muted),
            Actions(ActionButton("Continuar editando", () => dialog.Close(false), false),
                ActionButton("Descartar e sair", () => dialog.Close(true)))));
        var discard = await dialog.ShowDialog<bool>(this);
        if (discard) _trailHasUnsavedEdits = false;
        return discard;
    }

    private void Header(string eyebrow, string title)
    {
        PageEyebrow.Text = eyebrow.ToUpperInvariant();
        PageTitle.Text = title;
    }

    private void Display(Control content) => PageContent.Content = content;

    private static TextBlock Label(string text, double size = 14, IBrush? color = null, FontWeight weight = FontWeight.Normal)
        => new() { Text = TextRepair.Fix(text), FontSize = size, Foreground = color ?? Ink, FontWeight = weight, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Stack(double spacing = 16) => new() { Spacing = spacing };

    private static Border Card(Control content, IBrush? background = null)
        => new() { Child = content, Padding = new Thickness(24), Background = background ?? Surface,
            BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };

    private static Button ActionButton(string text, Action action, bool primary = true)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(18, 11), CornerRadius = new CornerRadius(8),
            Background = primary ? PrimaryAccent : Surface,
            BorderBrush = primary ? PrimaryAccent : Line,
            BorderThickness = new Thickness(1),
            Foreground = Brush.Parse(primary ? "#FFFFFF" : "#DCE2ED"),
            FontWeight = FontWeight.SemiBold
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel Actions(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private static TextBox Field(string? value = null, string? watermark = null, bool multiline = false)
        => new() { Text = value ?? "", PlaceholderText = watermark, MinHeight = multiline ? 100 : 40,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Padding = new Thickness(12, 9) };

    private (Control Panel, TextBox Input) AnswerEditor(Guid id, string? draft, Action<string> saveDraft, string label)
    {
        var input = Field(draft, "Escreva sua resposta aqui", true);
        input.TextChanged += (_, _) => saveDraft(input.Text ?? "");
        var code = _codeAnswerModes.GetValueOrDefault(id);
        var textMode = new RadioButton { Content = "Resposta em texto", GroupName = $"answer-mode-{id}", IsChecked = !code };
        var codeMode = new RadioButton { Content = "Código", GroupName = $"answer-mode-{id}", IsChecked = code };
        var note = Label("O código será avaliado como texto. O aplicativo não executa sua resposta.", 12, Muted);
        var selectedMode = Label("", 12, Teal, FontWeight.SemiBold);
        textMode.IsCheckedChanged += (_, _) => { if (textMode.IsChecked == true) SetMode(false); };
        codeMode.IsCheckedChanged += (_, _) => { if (codeMode.IsChecked == true) SetMode(true); };
        AutomationProperties.SetName(textMode, $"{label}: resposta em texto");
        AutomationProperties.SetName(codeMode, $"{label}: código");
        var panel = Stack(8);
        panel.Children.Add(Actions(textMode, codeMode));
        panel.Children.Add(selectedMode);
        panel.Children.Add(FormField(label, input));
        panel.Children.Add(note);
        SetMode(code);
        return (panel, input);

        void SetMode(bool code)
        {
            _codeAnswerModes[id] = code;
            input.AcceptsReturn = true;
            input.AcceptsTab = code;
            input.TextWrapping = code ? TextWrapping.NoWrap : TextWrapping.Wrap;
            input.MinHeight = code ? 220 : 110;
            input.FontSize = 14;
            input.FontFamily = new FontFamily(code ? "Consolas" : "Segoe UI");
            input.PlaceholderText = code ? "Cole ou escreva seu código. Indentação e quebras serão preservadas." : "Explique com suas palavras…";
            ScrollViewer.SetHorizontalScrollBarVisibility(input, code
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            selectedMode.Text = code ? "Modo selecionado: Código" : "Modo selecionado: Resposta em texto";
            note.IsVisible = code;
        }
    }

    private static StackPanel FormField(string title, Control input, string? help = null)
    {
        var panel = Stack(6);
        AutomationProperties.SetName(input, title);
        panel.Children.Add(Label(title, 13, Ink, FontWeight.SemiBold));
        panel.Children.Add(input);
        if (!string.IsNullOrWhiteSpace(help)) panel.Children.Add(Label(help, 12, Muted));
        return panel;
    }

    private static string Required(TextBox field, string title)
    {
        var value = field.Text?.Trim() ?? "";
        if (value.Length == 0)
        {
            field.BorderBrush = Brush.Parse("#FF7F97");
            field.BorderThickness = new Thickness(2);
            field.PlaceholderText = $"Obrigatório: {title}";
            field.Focus();
            throw new InvalidOperationException($"Preencha {title}.");
        }
        field.BorderBrush = Line;
        field.BorderThickness = new Thickness(1);
        return value;
    }

    private static string RequiredRaw(TextBox field, string title)
    {
        var value = field.Text ?? "";
        if (string.IsNullOrWhiteSpace(value))
        {
            field.BorderBrush = Brush.Parse("#FF7F97");
            field.BorderThickness = new Thickness(2);
            field.Focus();
            throw new InvalidOperationException($"Preencha {title}.");
        }
        field.BorderBrush = Line;
        field.BorderThickness = new Thickness(1);
        return value;
    }

    private static string StatusName(ModuleStatus status) => status switch
    {
        ModuleStatus.Completed => "Concluído",
        ModuleStatus.InProgress => "Em andamento",
        ModuleStatus.Available => "Disponível",
        _ => "Bloqueado"
    };

    private static string ProviderName(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAiCompatible => "API compatível com OpenAI",
        ProviderKind.ClaudeCli => "Claude Code CLI",
        _ => "Provedor indisponível"
    };

    private void ShowWelcome()
    {
        Header("Seu campus pessoal", "Aprender qualquer assunto, com método");
        var root = Stack(22);
        root.Children.Add(Label("Crie um projeto, faça um diagnóstico e receba uma trilha de estudo construída para o que você já sabe. Cada módulo traz aula, prática e prova.", 17, Muted));
        root.Children.Add(Card(StackWith(
            Label("Comece em três passos", 20, Ink, FontWeight.Bold),
            Label("01  Conecte o Claude Code CLI ou uma API compatível com OpenAI.", 15),
            Label("02  Defina seu objetivo e responda ao diagnóstico obrigatório.", 15),
            Label("03  Revise a trilha, estude e avance ao atingir 70% na prova.", 15),
            Actions(ActionButton("Configurar IA", () => Navigate("providers")), ActionButton("Criar projeto", () => Navigate("new"), false)))));
        Display(root);
    }

    private static StackPanel StackWith(params Control[] children)
    {
        var stack = Stack(16);
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }
}
