using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Automation;
using Avalonia.Threading;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private void ShowLesson()
    {
        if (_project is null || _module is null) { Navigate("home"); return; }
        Header($"Módulo {_module.Order + 1:00} · Aula", _module.Title);
        if (_module.Status == ModuleStatus.Locked)
        {
            Display(Card(Label("Este módulo será liberado quando você atingir 70% na prova anterior.", 15, Muted)));
            return;
        }
        var root = Stack(18);
        root.Children.Add(Actions(ActionButton("← Trilha", () => Navigate("trail"), false),
            ActionButton("Praticar", () => Navigate("practice"), false),
            ActionButton("Fazer prova", () => Navigate("exam"), false),
            ActionButton("Foco", () => Navigate("focus"), false)));
        if (_module.Lesson is null)
        {
            root.Children.Add(Card(StackWith(
                Label("Sua aula ainda não foi preparada", 20, Ink, FontWeight.Bold),
                Label("A IA vai organizar objetivos, explicação, exemplos e resumo no estilo que você escolheu.", 14, Muted),
                ActionButton("Gerar aula", async () =>
                {
                    Notice("Preparando a aula…");
                    await RunAsync(async () =>
                    {
                        var result = await _service.GetOrCreateLessonAsync(_project.Id, _module.Id, Ct);
                        if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível preparar a aula.", true); return; }
                        await RefreshProjectAsync(); Navigate("lesson");
                    });
                }))));
        }
        else
        {
            var lesson = _module.Lesson;
            root.Children.Add(Label("Uma aula pensada para o seu objetivo. Leia por partes e retome quando quiser.", 14, Muted));
            var progressText = Label($"Leitura: {lesson.ResumePosition} de 4 seções concluídas", 13, Teal, FontWeight.SemiBold);
            var progress = new ProgressBar { Minimum = 0, Maximum = 4, Value = lesson.ResumePosition, Height = 8 };
            root.Children.Add(Card(StackWith(progressText, progress)));
            root.Children.Add(LessonSection(1, "O que você vai aprender", lesson.Objectives, lesson, progress, progressText));
            root.Children.Add(LessonSection(2, "Entenda o assunto", lesson.Explanation, lesson, progress, progressText));
            root.Children.Add(LessonSection(3, "Exemplos para aplicar", lesson.Examples, lesson, progress, progressText));
            if (lesson.Boards.Count > 0) root.Children.Add(LessonBoardCard(lesson));
            root.Children.Add(LessonSection(4, "Em resumo", lesson.Summary, lesson, progress, progressText));
            root.Children.Add(Actions(ActionButton("Ir para a prática →", () => Navigate("practice")),
                ActionButton("Fazer prova", () => Navigate("exam"), false)));
            if (lesson.CompletedAtUtc is null)
                root.Children.Add(ActionButton("Concluir leitura das 4 seções", async () =>
                    await CompleteLessonAndPrepareReviewAsync(_project.Id, _module.Id), false));
            else
                root.Children.Add(Card(StackWith(
                    Label(_module.ReviewCardsGenerationCompleted ? "Cartões de revisão disponíveis" : "Reforce o que aprendeu", 17, Ink, FontWeight.Bold),
                    Label(_module.ReviewCardsGenerationCompleted
                        ? "A revisão espaçada está pronta. Os conceitos aparecem novamente nos próximos dias conforme suas respostas."
                        : "A leitura está salva. Gere cartões para praticar a recuperação dos conceitos.", 13, Muted),
                    Actions(ActionButton("Abrir revisão →", () => Navigate("review"), false),
                        _module.ReviewCardsGenerationCompleted
                            ? Label($"{_module.ReviewCards.Count(c => !c.IsArchived)} cartões ativos", 12, Teal)
                            : ActionButton("Tentar gerar cartões", async () => await GenerateLessonCardsAsync(_project.Id, _module.Id), false)))));
        }
        Display(root);
    }

    private Border LessonSection(int index, string title, string content, Lesson lesson, ProgressBar progress, TextBlock progressText)
    {
        var body = Stack(16);
        body.Children.Add(Label($"{index:00}  {title}", 19, Teal, FontWeight.Bold));
        body.Children.Add(MarkdownBlocks(content));
        var sectionAction = index == 4 && lesson.CompletedAtUtc is null ? "Concluir aula e preparar revisão" :
            lesson.ResumePosition >= index ? "Seção lida ✓" : "Marcar seção como lida";
        body.Children.Add(ActionButton(sectionAction, async () =>
        {
            if (_project is null || _module is null) return;
            if (index == 4 && lesson.CompletedAtUtc is null)
            {
                await CompleteLessonAndPrepareReviewAsync(_project.Id, _module.Id);
                return;
            }
            await RunAsync(async () =>
            {
                var nextPosition = Math.Max(index, lesson.ResumePosition);
                var result = await _service.SaveLessonPositionAsync(_project.Id, _module.Id, nextPosition);
                if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível salvar a leitura.", true); return; }
                lesson.ResumePosition = nextPosition;
                progress.Value = nextPosition;
                progressText.Text = $"Leitura: {nextPosition} de 4 seções concluídas";
                Notice(nextPosition == 4 ? "Aula lida. A prática é o próximo passo." : $"Seção {index} registrada. Continue a leitura.");
            });
        }, false));
        var explanationKey = $"{_module?.Id:N}:{index}";
        var explanation = Stack(10);
        if (_alternateExplanations.TryGetValue(explanationKey, out var cached)) explanation.Children.Add(AlternateExplanation(cached));
        var explainAgain = ActionButton("✦  Explicar de outro jeito", async () =>
        {
            if (_project is null || _module is null) return;
            var projectId = _project.Id;
            var moduleId = _module.Id;
            Notice("O tutor está preparando outra explicação…");
            await RunAsync(async () =>
            {
                var excerpt = content.Length > 1800 ? content[..1800] + "…" : content;
                var result = await _service.AskTutorAsync(projectId, moduleId,
                    $"Não entendi bem a seção \"{title}\" da aula. Explique de outro jeito, com uma analogia nova e um exemplo diferente do texto. Conteúdo da seção: {excerpt}", [], Ct);
                if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Value)) { Notice(result.Error ?? "O tutor não conseguiu responder.", true); return; }
                _alternateExplanations[explanationKey] = result.Value;
                explanation.Children.Clear();
                explanation.Children.Add(AlternateExplanation(result.Value));
                Notice("Nova explicação logo abaixo da seção.");
            });
        }, false);
        var sectionButton = body.Children[^1];
        body.Children.Remove(sectionButton);
        body.Children.Add(Actions(sectionButton, explainAgain));
        body.Children.Add(explanation);
        return Card(body, index == 3 ? Brush.Parse("#1B2026") : Surface);
    }

    private readonly Dictionary<string, string> _alternateExplanations = [];

    private static Border AlternateExplanation(string text)
        => new()
        {
            Background = Brush.Parse("#161B2E"), BorderBrush = Brush.Parse("#435BD8"), BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10), Padding = new Avalonia.Thickness(16),
            Child = StackWith(Label("OUTRA FORMA DE VER", 11, Brush.Parse("#91A0FF"), FontWeight.Bold), MarkdownBlocks(text))
        };

    private void ShowPractice()
    {
        if (_project is null || _module is null) { Navigate("home"); return; }
        Header($"Módulo {_module.Order + 1:00} · Prática", "Treine antes da prova");
        var root = Stack(18);
        root.Children.Add(Actions(ActionButton("← Aula", () => Navigate("lesson"), false),
            ActionButton("Ir para prova →", () => Navigate("exam"), false)));
        if (_module.Exercises.Count == 0)
        {
            root.Children.Add(Card(StackWith(Label("Transforme teoria em prática", 20, Ink, FontWeight.Bold),
                Label("Resolva exercícios com pistas e feedback explicativo do seu professor virtual.", 14, Muted),
                ActionButton("Gerar exercícios", async () =>
                {
                    Notice("Preparando exercícios…");
                    await RunAsync(async () =>
                    {
                        var result = await _service.GetOrCreatePracticeAsync(_project.Id, _module.Id, Ct);
                        if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível gerar exercícios.", true); return; }
                        await RefreshProjectAsync(); Navigate("practice");
                    });
                }))));
        }
        else
        {
            Border? feedbackToShow = null;
            var exerciseNumber = 0;
            foreach (var exercise in _module.Exercises)
            {
                exerciseNumber++;
                var editor = AnswerEditor(exercise.Id, _practiceDrafts.GetValueOrDefault(exercise.Id),
                    value => _practiceDrafts[exercise.Id] = value, "Sua resposta");
                var answer = editor.Input;
                var card = Stack(12);
                card.Children.Add(Label($"EXERCÍCIO {exerciseNumber:00} DE {_module.Exercises.Count:00}", 12, Teal, FontWeight.Bold));
                card.Children.Add(Label(exercise.Prompt, 17, Ink, FontWeight.SemiBold));
                if (!string.IsNullOrWhiteSpace(exercise.Hint))
                {
                    // A pista fica oculta: tentar recuperar a resposta sozinho fixa melhor o conteúdo.
                    var hint = Label("Pista: " + exercise.Hint, 13, Muted);
                    hint.IsVisible = false;
                    Button showHint = null!;
                    showHint = ActionButton("Mostrar pista", () => { hint.IsVisible = true; showHint.IsVisible = false; }, false);
                    showHint.Padding = new Avalonia.Thickness(12, 6);
                    card.Children.Add(showHint);
                    card.Children.Add(hint);
                }
                card.Children.Add(editor.Panel);
                card.Children.Add(ActionButton("Enviar resposta", async () =>
                {
                    await RunAsync(async () =>
                    {
                        var value = RequiredRaw(answer, "sua resposta");
                        Notice("Analisando sua resposta…");
                        var result = await _service.SubmitPracticeAsync(_project.Id, _module.Id, exercise.Id, value, Ct);
                        if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível analisar a resposta.", true); return; }
                        _practiceDrafts.Remove(exercise.Id);
                        _pendingPracticeFeedbackId = exercise.Id;
                        await RefreshProjectAsync(); Navigate("practice"); Notice("Feedback disponível abaixo do exercício.");
                    });
                }));
                var attemptNumber = exercise.Submissions.Count;
                foreach (var submission in exercise.Submissions.OrderByDescending(s => s.CreatedAt))
                {
                    var latest = attemptNumber == exercise.Submissions.Count;
                    var feedbackCard = Card(StackWith(
                        Label(attemptNumber == exercise.Submissions.Count ? "FEEDBACK MAIS RECENTE" : $"TENTATIVA {attemptNumber}", 12, Teal, FontWeight.Bold),
                        Label("Sua resposta", 13, Muted),
                        RawAnswerBlock(submission.Answer),
                        MarkdownBlocks(submission.Feedback),
                        Label("Exemplo de solução", 13, Teal, FontWeight.SemiBold),
                        MarkdownBlocks(submission.ExampleSolution)), Brush.Parse("#1B2026"));
                    if (latest && _pendingPracticeFeedbackId == exercise.Id)
                    {
                        feedbackCard.Focusable = true;
                        AutomationProperties.SetName(feedbackCard, $"Feedback mais recente do exercício {exerciseNumber}");
                        feedbackToShow = feedbackCard;
                    }
                    card.Children.Add(feedbackCard);
                    attemptNumber--;
                }
                root.Children.Add(Card(card));
            }
            var projectId = _project.Id;
            var moduleId = _module.Id;
            root.Children.Add(Card(StackWith(
                Label("Quer treinar mais?", 18, Ink, FontWeight.Bold),
                Label($"{_module.Exercises.Count} exercício(s) neste módulo. Peça mais 2, um pouco mais desafiadores e sem repetir os anteriores.", 14, Muted),
                ActionButton("＋  Gerar mais exercícios", async () =>
                {
                    Notice("Criando exercícios novos…");
                    await RunAsync(async () =>
                    {
                        var result = await _service.AddPracticeExercisesAsync(projectId, moduleId, Ct);
                        if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível gerar mais exercícios.", true); return; }
                        await RefreshProjectAsync(); Navigate("practice"); Notice("Novos exercícios adicionados ao final da lista.");
                    });
                }, false))));
            root.Children.Add(Card(StackWith(Label("Pronto para demonstrar o que aprendeu?", 18, Ink, FontWeight.Bold),
                Label("Use o feedback acima para revisar. A prova tem tentativas livres e aprovação a partir de 70%.", 14, Muted),
                ActionButton("Ir para a prova →", () => Navigate("exam")))));
            if (feedbackToShow is not null)
            {
                var target = feedbackToShow;
                _pendingPracticeFeedbackId = null;
                Dispatcher.UIThread.Post(() => { target.BringIntoView(); target.Focus(); }, DispatcherPriority.Loaded);
            }
        }
        Display(root);
    }

    private void ShowExam()
    {
        if (_project is null || _module is null) { Navigate("home"); return; }
        Header($"Módulo {_module.Order + 1:00} · Avaliação", "Mostre o que aprendeu");
        if (_module.Exam is { Questions.Count: > 0 } exam)
        {
            ShowQuestions(exam.Questions, true, ExamHistoryCard(_module, exam));
            return;
        }
        Display(Card(StackWith(
            Label("Uma prova para avançar", 20, Ink, FontWeight.Bold),
            Label("A aprovação é a partir de 70%. Se precisar, estude o feedback e tente novamente. Não há limite de tentativas.", 14, Muted),
            Actions(ActionButton("Preparar prova", async () =>
            {
                Notice("Preparando a prova…");
                await RunAsync(async () =>
                {
                    var result = await _service.GetOrCreateExamAsync(_project.Id, _module.Id, Ct);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível preparar a prova.", true); return; }
                    await RefreshProjectAsync(); Navigate("exam");
                });
            }), ActionButton("Voltar à aula", () => Navigate("lesson"), false)))));
    }

    private Control? ExamHistoryCard(StudyModule module, Exam exam)
    {
        if (_project is null || module.Attempts.Count == 0) return null;
        var last = module.Attempts.OrderBy(a => a.CreatedAt).Last();
        var triedThisVersion = module.Attempts.Any(a => a.ExamId == exam.Id);
        var projectId = _project.Id;
        var moduleId = module.Id;
        var actions = Actions(ActionButton("Ver correção da última tentativa", () => ShowExamResult(last), false));
        if (triedThisVersion)
            actions.Children.Add(ActionButton("Gerar nova versão da prova", async () =>
            {
                Notice("Preparando novas questões sobre os mesmos objetivos…");
                await RunAsync(async () =>
                {
                    var result = await _service.RegenerateExamAsync(projectId, moduleId, Ct);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível gerar a nova prova.", true); return; }
                    await RefreshProjectAsync(); Navigate("exam");
                    Notice("Nova versão pronta. Suas tentativas anteriores continuam no histórico.");
                });
            }, false));
        return Card(StackWith(
            Label("SEU HISTÓRICO NESTE MÓDULO", 11, Teal, FontWeight.Bold),
            Label($"Última tentativa: {last.Score}% · Melhor nota: {module.BestScore}% · {module.Attempts.Count} tentativa(s)", 15, Ink, FontWeight.SemiBold),
            Label(triedThisVersion
                ? "Refazer as mesmas questões pode medir memória da prova, não do conteúdo. Gere uma nova versão para testar o domínio de verdade."
                : "Esta é uma versão nova da prova.", 13, Muted),
            actions), Brush.Parse("#11141A"));
    }

    private void ShowExamResult(ExamAttempt attempt)
    {
        Header("Resultado da prova", attempt.Passed ? "Você avançou!" : "Vamos reforçar e tentar de novo");
        var root = Stack(18);
        root.Children.Add(Card(StackWith(
            Label($"{attempt.Score}%", 46, attempt.Passed ? Teal : Brush.Parse("#FF9A86"), FontWeight.Bold),
            Label(attempt.Passed ? "Aprovado. O próximo módulo foi liberado." : "A nota mínima é 70%. Leia os comentários por questão antes de tentar novamente.", 16, Ink),
            new ProgressBar { Minimum = 0, Maximum = 100, Value = attempt.Score, Height = 10 })));
        root.Children.Add(Label("Seu feedback, questão por questão", 19, Ink, FontWeight.Bold));
        var questionNumber = 0;
        foreach (var feedback in attempt.Feedback)
        {
            questionNumber++;
            var question = attempt.FrozenQuestions.FirstOrDefault(q => q.Id == feedback.QuestionId);
            var answer = attempt.Answers.FirstOrDefault(a => a.QuestionId == feedback.QuestionId)?.Answer;
            root.Children.Add(Card(StackWith(
                Label($"QUESTÃO {questionNumber:00}", 12, Teal, FontWeight.Bold),
                Label(question?.Prompt ?? "Questão", 15, Ink, FontWeight.SemiBold),
                Label("Sua resposta", 13, Muted),
                RawAnswerBlock(answer ?? "—"),
                Label($"{feedback.AwardedPoints} de {question?.Weight ?? 0} pontos", 13,
                    feedback.AwardedPoints == question?.Weight ? Teal : Brush.Parse("#FF9A86"), FontWeight.SemiBold),
                MarkdownBlocks(feedback.Feedback))));
        }
        var next = _project?.Modules.OrderBy(m => m.Order)
            .FirstOrDefault(m => _module is not null && m.Order == _module.Order + 1 && m.Status != ModuleStatus.Locked);
        if (attempt.Passed && next is not null)
            root.Children.Add(Actions(ActionButton("Ir para o próximo módulo →", () => { _module = next; Navigate("lesson"); }),
                ActionButton("Ver trilha", () => Navigate("trail"), false)));
        else if (attempt.Passed)
            root.Children.Add(ActionButton("Ver trilha concluída", () => Navigate("trail")));
        else
            root.Children.Add(Actions(ActionButton("Tentar novamente", () => Navigate("exam")),
                ActionButton("Rever aula", () => Navigate("lesson"), false),
                ActionButton("Ver trilha", () => Navigate("trail"), false)));
        Display(root);
    }

    private void ShowProgress()
    {
        if (_project is null) { Navigate("welcome"); return; }
        Header("Seu progresso", _project.Title);
        var root = Stack(16);
        var total = _project.Modules.Count;
        var done = _project.Modules.Count(m => m.Status == ModuleStatus.Completed);
        root.Children.Add(Card(StackWith(Label($"{done}/{total} módulos concluídos", 23, Ink, FontWeight.Bold),
            new ProgressBar { Minimum = 0, Maximum = Math.Max(total, 1), Value = done, Height = 12 },
            Label($"{_project.FocusMinutes} minutos de foco registrados · Sequência atual: {StudyStats.CurrentStreak(_project, DateOnly.FromDateTime(DateTime.Now))} dia(s) · Nota mínima: 70%", 14, Muted))));
        root.Children.Add(ActivityHeatmap(_project));
        if (WeakPointsCard(_project) is { } weak) root.Children.Add(weak);
        var upcoming = _project.Modules.OrderBy(m => m.Order)
            .FirstOrDefault(m => m.Status is ModuleStatus.Available or ModuleStatus.InProgress);
        if (upcoming is not null)
            root.Children.Add(Card(StackWith(Label("PRÓXIMO PASSO", 12, Teal, FontWeight.Bold),
                Label(upcoming.Title, 19, Ink, FontWeight.Bold),
                Label(upcoming.Status == ModuleStatus.InProgress ? "Retome a aula e use o feedback da última prova para melhorar." :
                    "Este módulo está liberado. Comece pela aula e pratique antes da avaliação.", 14, Muted),
                ActionButton("Continuar estudos →", () => { _module = upcoming; Navigate("lesson"); })), Brush.Parse("#1B2026")));
        foreach (var module in _project.Modules.OrderBy(m => m.Order))
        {
            var panel = Stack(8);
            panel.Children.Add(Label($"{module.Order + 1:00}  {module.Title}", 17, Ink, FontWeight.SemiBold));
            panel.Children.Add(Label(StatusName(module.Status) + $" · {module.Attempts.Count} tentativa(s)" +
                (module.Attempts.Count > 0 ? $" · Melhor nota: {module.BestScore}%" : ""), 13,
                module.Status == ModuleStatus.Completed ? Teal : Muted));
            foreach (var attempt in module.Attempts.OrderByDescending(a => a.CreatedAt))
                panel.Children.Add(Label($"{attempt.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {attempt.Score}% · {(attempt.Passed ? "Aprovado" : "Revisar")}", 12, attempt.Passed ? Teal : Muted));
            root.Children.Add(Card(panel));
        }
        root.Children.Add(Actions(ActionButton("Voltar ao projeto", () => Navigate("home"), false),
            ActionButton("Configurar projeto", () => Navigate("project"), false)));
        Display(root);
    }
}
