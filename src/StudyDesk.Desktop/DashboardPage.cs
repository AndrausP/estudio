using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private Control DashboardContent()
    {
        if (_project is null) return Card(Label("Escolha um projeto para continuar.", 15, Muted));
        var current = _project.Modules.OrderBy(m => m.Order)
            .FirstOrDefault(m => m.Status is ModuleStatus.Available or ModuleStatus.InProgress);
        var main = Stack(18);
        main.Children.Add(DashboardHero(_project, current));
        main.Children.Add(DashboardStats(_project, current));
        main.Children.Add(DashboardTrail(_project.Modules.OrderBy(m => m.Order).ToArray()));
        main.Children.Add(DashboardPlan(current));
        var tutor = TutorPanel(current?.Id);
        var side = Stack(18);
        side.Children.Add(DailyGoalCard());
        side.Children.Add(DashboardReviewCard(_project));
        side.Children.Add(FocusQuickCard(_project));
        side.Children.Add(tutor);
        _dashboardStacked = ClientSize.Width < 1280;
        if (_dashboardStacked == true)
        {
            var vertical = Stack(18);
            vertical.Children.Add(main);
            vertical.Children.Add(side);
            return vertical;
        }
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,320") };
        Grid.SetColumn(main, 0);
        layout.Children.Add(main);
        side.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumn(side, 1);
        layout.Children.Add(side);
        return layout;
    }

    private Border DashboardHero(StudyProject project, StudyModule? current)
    {
        var body = Stack(16);
        body.Children.Add(Label("SEU CAMPUS PESSOAL", 12, Teal, FontWeight.Bold));
        body.Children.Add(Label("Sua jornada de aprendizado, em um só lugar.", 28, Ink, FontWeight.Bold));
        body.Children.Add(Label(project.Goal, 17, Muted));
        body.Children.Add(Label(current is null ? "Trilha concluída" : $"Próximo módulo: {current.Title}", 13, Teal, FontWeight.SemiBold));
        body.Children.Add(Actions(ActionButton(current is null ? "Revisar trilha →" : "Continuar estudos →", () =>
        {
            if (current is null) Navigate("trail");
            else { _module = current; Navigate("lesson"); }
        }), ActionButton("⚙  Projeto", () => Navigate("project"), false)));
        var hero = Card(body, Brush.Parse("#11141A"));
        hero.BorderBrush = Accent;
        return hero;
    }

    private Border DailyGoalCard()
    {
        var goal = ReadAppSettings().DailyGoalMinutes;
        var today = _focusByDay.GetValueOrDefault(DateOnly.FromDateTime(DateTime.Now)) / 60;
        var done = today >= goal;
        return Card(StackWith(
            Label("◎  Meta de hoje", 19, Ink, FontWeight.Bold),
            Label($"{Math.Min(today, 999)} de {goal} min de foco", 17, done ? Teal : Ink, FontWeight.Bold),
            new ProgressBar { Minimum = 0, Maximum = Math.Max(goal, 1), Value = Math.Min(today, goal), Height = 8 },
            Label(done ? "Meta cumprida. Ótimo trabalho!" : "Some todos os projetos. Ajuste a meta na tela de Foco.", 13, Muted)));
    }

    private Border FocusQuickCard(StudyProject project)
        => Card(StackWith(
            Label("◷  Foco", 19, Ink, FontWeight.Bold),
            Label($"{project.FocusMinutes} minutos efetivos registrados", 15, Teal, FontWeight.SemiBold),
            Label("Configure um ciclo Pomodoro para o próximo bloco de estudo.", 13, Muted),
            ActionButton("Abrir temporizador →", () => Navigate("focus"), false)));

    private Border DashboardReviewCard(StudyProject project)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var cards = project.Modules.SelectMany(m => m.ReviewCards ?? []).Where(c => !c.IsArchived).ToArray();
        var due = cards.Count(c => c.IsDue(today));
        var next = cards.Where(c => !c.IsDue(today)).Select(c => (DateOnly?)c.DueDateLocal).Min();
        return Card(StackWith(
            Label("◇  Revisão de hoje", 19, Ink, FontWeight.Bold),
            Label(due > 0 ? $"{due} cartões para lembrar" : "Tudo em dia", 17, due > 0 ? Teal : Ink, FontWeight.Bold),
            Label(due > 0 ? "Recupere os conceitos antes de conferir as respostas."
                : cards.Length == 0 ? "Conclua uma aula para criar seus primeiros cartões."
                : next is null ? "Seus cartões estão em dia." : $"Próxima revisão em {next:dd/MM/yyyy}.", 13, Muted),
            ActionButton(due > 0 ? "Revisar agora →" : "Ver meus cartões →", () => Navigate("review"), false)));
    }

    private Border DashboardTrail(IReadOnlyList<StudyModule> modules)
    {
        var body = Stack(14);
        body.Children.Add(Actions(Label("Sua trilha", 19, Ink, FontWeight.Bold),
            ActionButton("Ver mapa completo →", () => Navigate("trail"), false)));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var module in modules)
        {
            var color = module.Status switch
            {
                ModuleStatus.Completed => Teal,
                ModuleStatus.InProgress or ModuleStatus.Available => Accent,
                _ => Muted
            };
            var tile = Stack(8);
            var numberLabel = Label(module.Status == ModuleStatus.Completed ? "✓" : (module.Order + 1).ToString(), 17, color, FontWeight.Bold);
            numberLabel.HorizontalAlignment = HorizontalAlignment.Center;
            numberLabel.VerticalAlignment = VerticalAlignment.Center;
            tile.Children.Add(new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(12),
                Background = module.Status == ModuleStatus.Completed ? Brush.Parse("#173D31") :
                    module.Status == ModuleStatus.Locked ? Brush.Parse("#24272D") : Brush.Parse("#26325B"),
                Child = numberLabel,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            tile.Children.Add(Label(module.Title, 14, Ink, FontWeight.SemiBold));
            tile.Children.Add(Label(StatusName(module.Status), 12, color));
            if (module.Attempts.Count > 0) tile.Children.Add(Label($"Melhor nota {module.BestScore}%", 11, Muted));
            if (module.Status != ModuleStatus.Locked)
                tile.Children.Add(ActionButton("Abrir →", () => { _module = module; Navigate("lesson"); }, false));
            row.Children.Add(new Border
            {
                Width = 165, MinHeight = 162, Padding = new Thickness(14),
                Background = Brush.Parse("#101216"), BorderBrush = color,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = tile
            });
        }
        body.Children.Add(new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        });
        return Card(body);
    }

    private Control DashboardStats(StudyProject project, StudyModule? current)
    {
        var done = project.Modules.Count(m => m.Status == ModuleStatus.Completed);
        var attempts = project.Modules.Sum(m => m.Attempts.Count);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        var streak = StudyStats.CurrentStreak(project, DateOnly.FromDateTime(DateTime.Now));
        var studiedToday = StudyStats.ActivityDays(project).Contains(DateOnly.FromDateTime(DateTime.Now));
        var first = StatCard("MÓDULOS CONCLUÍDOS", $"{done} / {project.Modules.Count}", "Aprovação a partir de 70%", Teal);
        var second = StatCard("TEMPO DE FOCO", $"{project.FocusMinutes} min", "Tempo efetivo registrado", Accent);
        var third = StatCard("AVALIAÇÕES", attempts.ToString(),
            current?.Attempts.Count > 0 ? $"Melhor nota no módulo: {current.BestScore}%" : "Tentativas livres", Brush.Parse("#B8A1FF"));
        var fourth = StatCard("SEQUÊNCIA", streak == 1 ? "1 dia" : $"{streak} dias",
            studiedToday ? "Você já estudou hoje" : streak > 0 ? "Estude hoje para manter" : "Comece hoje sua sequência", Brush.Parse("#FFC86B"));
        first.Margin = new Thickness(0, 0, 10, 0);
        second.Margin = new Thickness(0, 0, 10, 0);
        third.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(first, 0); Grid.SetColumn(second, 1); Grid.SetColumn(third, 2); Grid.SetColumn(fourth, 3);
        grid.Children.Add(first); grid.Children.Add(second); grid.Children.Add(third); grid.Children.Add(fourth);
        return grid;
    }

    private static Border StatCard(string title, string value, string detail, IBrush accent)
        => Card(StackWith(Label(title, 10, accent, FontWeight.Bold), Label(value, 24, Ink, FontWeight.Bold),
            Label(detail, 11, Muted)));

    private Border DashboardPlan(StudyModule? module)
    {
        if (module is null)
            return Card(StackWith(Label("Trilha concluída!", 21, Teal, FontWeight.Bold),
                Label("Todas as provas foram aprovadas. Você pode reabrir aulas e consultar seu histórico.", 14, Muted),
                ActionButton("Revisar a trilha", () => Navigate("trail"))));
        var body = Stack(14);
        body.Children.Add(Label("Próximo plano de estudo", 19, Ink, FontWeight.Bold));
        body.Children.Add(Label(module.Title, 16, Teal, FontWeight.SemiBold));
        body.Children.Add(Label(module.Objective, 14, Muted));
        body.Children.Add(PlanStep("01", module.Lesson is null ? "Preparar e ler a aula" :
            module.Lesson.ResumePosition < 4 ? "Continuar a leitura da aula" : "Revisar a aula",
            () => { _module = module; Navigate("lesson"); }));
        body.Children.Add(PlanStep("02", module.Exercises.Count == 0 ? "Gerar e resolver exercícios" :
            module.Exercises.Any(e => e.Submissions.Count > 0) ? "Revisar feedback e praticar" : "Resolver exercícios",
            () => { _module = module; Navigate("practice"); }));
        body.Children.Add(PlanStep("03", module.Attempts.Count > 0 ? "Rever a prova e tentar novamente" : "Fazer a prova do módulo",
            () => { _module = module; Navigate("exam"); }));
        return Card(body);
    }

    private static Control PlanStep(string number, string label, Action action)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto") };
        var index = Label(number, 13, Teal, FontWeight.Bold);
        var title = Label(label, 14, Ink);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(0, 0, 8, 0);
        var button = ActionButton("Abrir →", action, false);
        Grid.SetColumn(index, 0); Grid.SetColumn(title, 1); Grid.SetColumn(button, 2);
        row.Children.Add(index); row.Children.Add(title); row.Children.Add(button);
        return row;
    }

    private void ShowTutorPage()
    {
        if (_project is null) { Navigate("welcome"); return; }
        Header("Tutor IA", "Uma pergunta pode destravar seu estudo");
        var current = _module ?? _project.Modules.OrderBy(m => m.Order)
            .FirstOrDefault(m => m.Status is ModuleStatus.Available or ModuleStatus.InProgress);
        var root = Stack(16);
        root.Children.Add(Label("Pergunte sobre o objetivo ou o módulo atual. O tutor conhece o contexto do seu projeto.", 15, Muted));
        root.Children.Add(TutorPanel(current?.Id));
        Display(root);
    }

    private Border TutorPanel(Guid? moduleId)
    {
        if (_project is null) return Card(Label("Escolha um projeto para falar com o tutor.", 14, Muted));
        var projectId = _project.Id;
        var module = _project.Modules.FirstOrDefault(m => m.Id == moduleId);
        var panel = Stack(14);
        panel.Children.Add(Label("✦  Tutor IA", 19, Ink, FontWeight.Bold));
        panel.Children.Add(Label(module is null ? "Contexto: objetivo do projeto" : $"Contexto: {module.Title}", 12, Teal));
        var messages = Stack(12);
        if (!_tutorHistory.TryGetValue(projectId, out var history))
            _tutorHistory[projectId] = history = [];
        if (history.Count == 0)
            messages.Children.Add(Label("O que você quer entender melhor? Posso explicar um conceito, dar outro exemplo ou ajudar a revisar para a prova.", 14, Muted));
        else foreach (var item in history) messages.Children.Add(TutorBubble(item.FromUser, item.Text));
        panel.Children.Add(new ScrollViewer { Content = messages, MaxHeight = 430,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var input = Field(_tutorDrafts.GetValueOrDefault(projectId), "Pergunte algo ao tutor…", true);
        input.TextChanged += (_, _) => _tutorDrafts[projectId] = input.Text ?? "";
        panel.Children.Add(FormField("Sua pergunta", input));
        panel.Children.Add(Actions(ActionButton("Enviar pergunta →", async () =>
        {
            await RunAsync(async () =>
            {
                var question = Required(input, "sua pergunta");
                Notice("O tutor está preparando uma explicação…");
                var turns = history.Select(x => new TutorTurn(x.FromUser, x.Text)).ToList();
                var result = await _service.AskTutorAsync(projectId, moduleId, question, turns, Ct);
                if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Value))
                { Notice(result.Error ?? "O tutor não conseguiu responder. Tente novamente.", true); return; }
                history.Add((true, question));
                history.Add((false, result.Value));
                messages.Children.Clear();
                foreach (var item in history) messages.Children.Add(TutorBubble(item.FromUser, item.Text));
                input.Text = "";
                Notice("Resposta do tutor disponível abaixo.");
            });
        }), ActionButton("Limpar conversa", () =>
        {
            history.Clear();
            messages.Children.Clear();
            messages.Children.Add(Label("Conversa reiniciada. Pergunte o que quiser.", 14, Muted));
        }, false)));
        return Card(panel);
    }

    private static Border TutorBubble(bool fromUser, string text)
    {
        Control content = fromUser ? Label(text, 14, Ink) : MarkdownBlocks(text);
        return new Border
        {
            Child = content, Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            Background = Brush.Parse(fromUser ? "#293769" : "#22262D"),
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            MaxWidth = 400
        };
    }
}
