using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private Action? _reviewReveal;
    private Func<ReviewGrade, Task>? _reviewGrade;
    private bool _reviewRevealed;

    private async Task CompleteLessonAndPrepareReviewAsync(Guid projectId, Guid moduleId)
    {
        var completed = false;
        await RunAsync(async () =>
        {
            var result = await _service.CompleteLessonAsync(projectId, moduleId);
            if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível concluir a aula.", true); return; }
            completed = true;
            await RefreshProjectAsync();
            if (_page == "lesson") Navigate("lesson");
            Notice("Aula concluída. Você já pode praticar; a IA está preparando cartões de revisão.");
        });
        if (completed) _ = GenerateLessonCardsInBackgroundAsync(projectId, moduleId);
    }

    private void ShowReviewPage()
    {
        if (_project is null) { Navigate("welcome"); return; }
        Header("Memória ativa", "Revisão espaçada");
        var project = _project;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var cards = project.Modules.SelectMany(m => m.ReviewCards ?? []).Where(c => !c.IsArchived).ToArray();
        var archived = project.Modules.SelectMany(m => m.ReviewCards ?? []).Where(c => c.IsArchived).ToArray();
        var due = cards.Where(c => c.IsDue(today)).OrderBy(c => c.DueDateLocal).ThenBy(c => c.CreatedAt).ToArray();
        var root = Stack(18);
        root.Children.Add(Label("Responda de memória, revele a resposta e diga como foi. O próximo encontro com cada conceito se ajusta ao seu resultado.", 15, Muted));
        var accuracy = StudyStats.ReviewAccuracy(project, DateTimeOffset.UtcNow.AddDays(-7));
        root.Children.Add(Card(StackWith(
            Label("CARTÕES PARA HOJE", 11, Teal, FontWeight.Bold),
            Label(due.Length.ToString(), 34, Ink, FontWeight.Bold),
            Label($"{cards.Length} cartões ativos neste projeto" +
                (accuracy is { } value ? $" · {value}% de acerto nos últimos 7 dias" : ""), 13, Muted))));

        if (due.Length > 0) root.Children.Add(ReviewQuestion(project, due[0], due.Length));
        else if (_extraPractice && cards.Length > 0) root.Children.Add(ExtraPracticeCard(project, cards));
        else
        {
            var next = cards.Length == 0 ? (DateOnly?)null : cards.Min(c => c.DueDateLocal);
            var actions = Actions(ActionButton("Abrir trilha →", () => Navigate("trail"), false));
            if (cards.Length > 0)
                actions.Children.Add(ActionButton("Treinar mesmo assim", () => { _extraPractice = true; _extraIndex = 0; Navigate("review"); }, false));
            root.Children.Add(Card(StackWith(
                Label(cards.Length == 0 ? "Sua revisão começa com uma aula" : "Tudo revisado por hoje", 21, Ink, FontWeight.Bold),
                Label(cards.Length == 0
                    ? "Conclua uma aula para criar cartões de revisão. Você também pode tentar novamente a geração de uma aula já lida."
                    : $"Próxima revisão em {next:dd/MM/yyyy}. Quer treinar antes? O treino extra não altera o agendamento.", 14, Muted),
                actions)));
        }
        if (WeakPointsCard(project) is { } weak) root.Children.Add(weak);

        var pending = project.Modules.Where(m => m.Lesson?.CompletedAtUtc is not null && !m.ReviewCardsGenerationCompleted).ToArray();
        if (pending.Length > 0)
        {
            var panel = Stack(12);
            panel.Children.Add(Label("Aulas sem cartões", 18, Ink, FontWeight.Bold));
            panel.Children.Add(Label("A leitura foi salva. Se a IA não conseguiu criar os cartões, tente novamente quando a conexão estiver disponível.", 13, Muted));
            foreach (var module in pending)
            {
                var moduleId = module.Id;
                var generating = _reviewGenerationInFlight.Contains(moduleId);
                var retry = ActionButton(generating ? "Gerando…" : "Gerar cartões", async () =>
                {
                    await GenerateLessonCardsAsync(project.Id, moduleId);
                }, false);
                retry.IsEnabled = !generating;
                panel.Children.Add(Actions(Label(module.Title, 14, Ink), retry));
            }
            root.Children.Add(Card(panel));
        }

        if (cards.Length > 0) root.Children.Add(new Expander
        {
            Header = $"Gerenciar {cards.Length} cartões",
            IsExpanded = false,
            Content = ReviewManagement(project, cards)
        });
        if (archived.Length > 0) root.Children.Add(new Expander
        {
            Header = $"Arquivo · {archived.Length} cartões",
            IsExpanded = false,
            Content = Card(StackWith(archived.Select(c => (Control)Card(StackWith(
                Label(c.Question, 14, Ink, FontWeight.SemiBold),
                MarkdownBlocks(c.Answer),
                Label($"{c.Attempts.Count} revisões registradas", 12, Muted)), Brush.Parse("#101216"))).ToArray()))
        });
        Display(root);
    }

    private bool _extraPractice;
    private int _extraIndex;

    /// <summary>Treino livre com cartões ainda não vencidos: revela e avança, sem mudar datas nem histórico.</summary>
    private Border ExtraPracticeCard(StudyProject project, IReadOnlyList<ReviewCard> cards)
    {
        var ordered = cards.OrderBy(c => StudyStats.WeakCards(project, int.MaxValue).Any(w => w.Card.Id == c.Id) ? 0 : 1)
            .ThenBy(c => c.DueDateLocal).ToList();
        var card = ordered[_extraIndex % ordered.Count];
        var module = project.Modules.FirstOrDefault(m => m.Id == card.ModuleId);
        var answer = Card(StackWith(Label("RESPOSTA", 11, Teal, FontWeight.Bold), MarkdownBlocks(card.Answer)), Brush.Parse("#1B2026"));
        answer.IsVisible = false;
        var next = ActionButton("Próximo cartão →", () => { _extraIndex++; Navigate("review"); });
        next.IsVisible = false;
        Button reveal = null!;
        void Reveal() { answer.IsVisible = true; next.IsVisible = true; reveal.IsVisible = false; _reviewRevealed = true; }
        reveal = ActionButton("Revelar resposta", Reveal);
        _reviewReveal = Reveal;
        _reviewRevealed = false;
        return Card(StackWith(
            Label($"TREINO EXTRA · cartão {_extraIndex % ordered.Count + 1} de {ordered.Count} · {module?.Title ?? "Módulo"}", 12, Brush.Parse("#FFC86B"), FontWeight.SemiBold),
            Label(CleanInline(card.Question), 21, Ink, FontWeight.Bold),
            reveal, answer,
            Actions(next, ActionButton("Encerrar treino", () => { _extraPractice = false; Navigate("review"); }, false)),
            Label("O treino extra não conta como revisão: as datas dos cartões continuam as mesmas.", 12, Muted)));
    }

    private Border ReviewQuestion(StudyProject project, ReviewCard card, int remaining)
    {
        var module = project.Modules.FirstOrDefault(m => m.Id == card.ModuleId);
        var panel = Stack(16);
        panel.Children.Add(Label($"{remaining} para hoje · {module?.Title ?? "Módulo"}", 12, Teal, FontWeight.SemiBold));
        panel.Children.Add(Label(CleanInline(card.Question), 21, Ink, FontWeight.Bold));
        panel.Children.Add(Label("Tente responder antes de revelar.", 13, Muted));
        var answer = Card(StackWith(Label("RESPOSTA", 11, Teal, FontWeight.Bold), MarkdownBlocks(card.Answer)), Brush.Parse("#1B2026"));
        answer.IsVisible = false;
        answer.Focusable = true;
        AutomationProperties.SetName(answer, "Resposta do cartão de revisão");
        var ratings = Actions(
            ReviewGradeButton("Errei · revisar amanhã", ReviewGrade.Wrong),
            ReviewGradeButton("Acertei", ReviewGrade.Correct),
            ReviewGradeButton("Fácil", ReviewGrade.Easy));
        ratings.IsVisible = false;
        Button revealButton = null!;
        void Reveal()
        {
            answer.IsVisible = true;
            ratings.IsVisible = true;
            revealButton.IsVisible = false;
            _reviewRevealed = true;
            answer.BringIntoView();
            answer.Focus();
        }
        revealButton = ActionButton("Revelar resposta", Reveal);
        AutomationProperties.SetName(revealButton, "Revelar resposta do cartão");
        _reviewReveal = Reveal;
        _reviewGrade = Grade;
        _reviewRevealed = false;
        panel.Children.Add(revealButton);
        panel.Children.Add(answer);
        panel.Children.Add(ratings);
        panel.Children.Add(Label("Atalhos: Espaço revela a resposta · 1 Errei · 2 Acertei · 3 Fácil", 12, Muted));
        return Card(panel);

        async Task Grade(ReviewGrade grade)
        {
            await RunAsync(async () =>
            {
                var result = await _service.ReviewCardAsync(project.Id, card.Id, grade);
                if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível registrar a revisão.", true); return; }
                await RefreshProjectAsync();
                Navigate("review");
                var next = result.Value!.DueDateLocal;
                Notice($"Revisão salva. Este cartão volta em {next:dd/MM}. Continue com o próximo.");
            });
        }

        Button ReviewGradeButton(string text, ReviewGrade grade)
        {
            var button = ActionButton(text, async () => await Grade(grade), grade == ReviewGrade.Correct);
            AutomationProperties.SetName(button, $"Avaliar resposta: {text}");
            return button;
        }
    }

    private Border ReviewManagement(StudyProject project, IReadOnlyList<ReviewCard> cards)
    {
        var body = Stack(12);
        body.Children.Add(Label("Seus cartões", 18, Ink, FontWeight.Bold));
        body.Children.Add(Label("Ajuste uma pergunta ou resposta para ficar mais clara. Arquivar remove o cartão das próximas revisões e preserva o histórico.", 13, Muted));
        foreach (var card in cards.OrderBy(c => c.DueDateLocal))
        {
            var draft = _reviewEditDrafts.GetValueOrDefault(card.Id, (card.Question, card.Answer));
            var question = Field(draft.Question, "Pergunta", true);
            var answer = Field(draft.Answer, "Resposta", true);
            question.TextChanged += (_, _) => _reviewEditDrafts[card.Id] = (question.Text ?? "", answer.Text ?? "");
            answer.TextChanged += (_, _) => _reviewEditDrafts[card.Id] = (question.Text ?? "", answer.Text ?? "");
            var module = project.Modules.FirstOrDefault(m => m.Id == card.ModuleId);
            var item = Stack(10);
            item.Children.Add(Label(module?.Title ?? "Módulo", 11, Teal, FontWeight.Bold));
            item.Children.Add(FormField("Pergunta", question));
            item.Children.Add(FormField("Resposta", answer));
            item.Children.Add(Label($"Próxima revisão: {card.DueDateLocal:dd/MM/yyyy} · Revisões: {card.Attempts.Count}", 12, Muted));
            item.Children.Add(Actions(
                ActionButton("Salvar alterações", async () =>
                {
                    await RunAsync(async () =>
                    {
                        var result = await _service.EditReviewCardAsync(project.Id, card.Id,
                            RequiredRaw(question, "a pergunta"), RequiredRaw(answer, "a resposta"));
                        if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível salvar o cartão.", true); return; }
                        _reviewEditDrafts.Remove(card.Id);
                        await RefreshProjectAsync(); Navigate("review"); Notice("Cartão atualizado.");
                    });
                }, false),
                ActionButton("Arquivar", async () =>
                {
                    if (!await ConfirmArchiveReviewCardAsync(card.Question)) return;
                    await RunAsync(async () =>
                    {
                        var result = await _service.ArchiveReviewCardAsync(project.Id, card.Id);
                        if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível arquivar o cartão.", true); return; }
                        _reviewEditDrafts.Remove(card.Id);
                        await RefreshProjectAsync(); Navigate("review"); Notice("Cartão arquivado.");
                    });
                }, false)));
            body.Children.Add(Card(item, Brush.Parse("#101216")));
        }
        return Card(body);
    }

    private async Task GenerateLessonCardsAsync(Guid projectId, Guid moduleId)
    {
        if (!_reviewGenerationInFlight.Add(moduleId))
        {
            Notice("A IA já está preparando os cartões desta aula.");
            return;
        }
        try { await RunAsync(async () =>
        {
                Notice("Criando cartões da aula…");
                var result = await _service.GenerateReviewCardsAsync(projectId, moduleId, Ct);
                if (result.IsSuccess)
                {
                    await RefreshProjectAsync();
                    Navigate(_page == "lesson" ? "lesson" : "review");
                }
                Notice(result.IsSuccess
                    ? "Cartões prontos. A primeira revisão está disponível hoje."
                    : (result.Error ?? "A IA não conseguiu criar os cartões. Sua aula continua salva; tente novamente."),
                    !result.IsSuccess);
        }); }
        finally { _reviewGenerationInFlight.Remove(moduleId); }
    }

    private async Task GenerateLessonCardsInBackgroundAsync(Guid projectId, Guid moduleId)
    {
        if (!_reviewGenerationInFlight.Add(moduleId)) return;
        try
        {
            var result = await _service.GenerateReviewCardsAsync(projectId, moduleId);
            if (_project?.Id != projectId) return;
            var latest = await _service.GetProjectAsync(projectId);
            if (_project?.Id != projectId) return;
            if (latest.IsSuccess && latest.Value is not null)
            {
                _project = latest.Value;
                _module = _module is null ? null : _project.Modules.FirstOrDefault(m => m.Id == _module.Id);
            }
            // Não recriar telas de leitura/edição enquanto o aluno está interagindo.
            if (_page == "home" && !_busy) ShowProjectHome();
            Notice(result.IsSuccess
                ? "A IA criou seus cartões. Revise o que aprendeu hoje."
                : "A aula foi salva, mas a IA não criou os cartões. Abra Revisão para tentar novamente.",
                !result.IsSuccess);
        }
        catch (Exception)
        {
            if (_project?.Id == projectId)
                Notice("A aula foi salva, mas os cartões não foram criados. Abra Revisão para tentar novamente.", true);
        }
        finally { _reviewGenerationInFlight.Remove(moduleId); }
    }

    private async Task<bool> ConfirmArchiveReviewCardAsync(string question)
    {
        var dialog = new Window
        {
            Title = "Arquivar cartão",
            Width = 460,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Content = Card(StackWith(
            Label("Arquivar este cartão?", 20, Ink, FontWeight.Bold),
            Label(question, 14, Muted),
            Label("Ele sairá das próximas revisões. O histórico continuará salvo.", 13, Muted),
            Actions(ActionButton("Manter cartão", () => dialog.Close(false), false),
                ActionButton("Arquivar cartão", () => dialog.Close(true)))));
        return await dialog.ShowDialog<bool>(this);
    }
}
