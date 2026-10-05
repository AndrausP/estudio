using StudyDesk.Application;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private void ShowDiagnostic()
    {
        if (_project is null) return;
        Header("Etapa 1 · Diagnóstico", "Vamos entender seu ponto de partida");
        var diagnostic = _project.Diagnostic;
        if (diagnostic?.CompletedAt is not null)
        {
            Display(Card(StackWith(Label("Diagnóstico concluído", 21, Ink, FontWeight.Bold),
                Label(diagnostic.Summary, 15, Muted), ActionButton("Abrir trilha", () => Navigate("trail")))));
            return;
        }
        if (diagnostic?.Questions.Count > 0) { ShowQuestions(diagnostic.Questions, false); return; }
        Display(Card(StackWith(
            Label("Um pequeno teste para personalizar sua jornada", 21, Ink, FontWeight.Bold),
            Label("Responda com o que sabe hoje. O resultado define a profundidade das primeiras aulas; você poderá revisar a trilha antes de começar.", 15, Muted),
            ActionButton("Gerar diagnóstico", async () =>
            {
                Notice("A IA está preparando suas perguntas…");
                await RunAsync(async () =>
                {
                    var result = await _service.StartDiagnosticAsync(_project.Id, Ct);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível criar o diagnóstico.", true); return; }
                    await RefreshProjectAsync();
                    Navigate("diagnostic");
                });
            }))));
    }

    private void ShowQuestions(IReadOnlyList<ExamQuestion> questions, bool isExam, Control? preface = null)
    {
        if (_project is null) return;
        var stack = Stack(20);
        if (preface is not null) stack.Children.Add(preface);
        stack.Children.Add(Label(isExam
            ? "Responda todas as questões. Você pode tentar novamente quantas vezes precisar. A nota mínima é 70%."
            : "Este diagnóstico não vale nota. Responda para que a trilha comece no nível certo.", 15, Muted));
        stack.Children.Add(Label($"{questions.Count} questões · respostas em edição são mantidas enquanto o app estiver aberto", 12, Teal, FontWeight.SemiBold));
        var answers = new List<(ExamQuestion question, Func<string> read, Control focus)>();
        for (var i = 0; i < questions.Count; i++)
        {
            var question = questions[i];
            var panel = Stack(12);
            panel.Children.Add(Label($"QUESTÃO {i + 1} DE {questions.Count}" + (isExam ? $" · {question.Weight} PONTOS" : ""), 12, Teal, FontWeight.Bold));
            panel.Children.Add(Label(CleanInline(question.Prompt), 16, Ink, FontWeight.SemiBold));
            if (question.Kind == QuestionKind.MultipleChoice && question.Options.Count > 0)
            {
                var group = new List<RadioButton>();
                foreach (var option in question.Options)
                {
                    var radio = new RadioButton { Content = TextRepair.Fix(option), GroupName = $"q-{question.Id}", Margin = new Avalonia.Thickness(0, 3) };
                    AutomationProperties.SetName(radio, $"Questão {i + 1}: {option}");
                    radio.IsChecked = _questionDrafts.GetValueOrDefault(question.Id) == option;
                    radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) _questionDrafts[question.Id] = option; };
                    group.Add(radio);
                    panel.Children.Add(radio);
                }
                answers.Add((question, () => group.FirstOrDefault(r => r.IsChecked == true)?.Content?.ToString() ?? "", group[0]));
            }
            else
            {
                var editor = AnswerEditor(question.Id, _questionDrafts.GetValueOrDefault(question.Id),
                    value => _questionDrafts[question.Id] = value, $"Resposta da questão {i + 1}");
                panel.Children.Add(editor.Panel);
                answers.Add((question, () => editor.Input.Text ?? "", editor.Input));
            }
            stack.Children.Add(Card(panel));
        }
        stack.Children.Add(Actions(ActionButton(isExam ? "Enviar prova" : "Concluir diagnóstico", async () =>
        {
            var submitted = answers.Select(x => new QuestionAnswer { QuestionId = x.question.Id, Answer = x.read() }).ToArray();
            var firstBlank = Array.FindIndex(submitted, a => string.IsNullOrWhiteSpace(a.Answer));
            if (firstBlank >= 0)
            {
                answers[firstBlank].focus.Focus();
                Notice($"Responda a questão {firstBlank + 1} antes de enviar.", true);
                return;
            }
            if (isExam && !await ConfirmExamSubmissionAsync(submitted.Length)) return;
            await RunAsync(async () =>
            {
                if (isExam)
                {
                    if (_module is null) return;
                    Notice("Corrigindo a prova…");
                    var result = await _service.SubmitExamAsync(_project.Id, _module.Id, submitted, Ct);
                    if (!result.IsSuccess || result.Value is null) { Notice(result.Error ?? "Não foi possível corrigir a prova.", true); return; }
                    foreach (var answer in submitted) _questionDrafts.Remove(answer.QuestionId);
                    await RefreshProjectAsync();
                    ShowExamResult(result.Value);
                }
                else
                {
                    Notice("Analisando suas respostas…");
                    var result = await _service.CompleteDiagnosticAsync(_project.Id, submitted, Ct);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível concluir o diagnóstico.", true); return; }
                    foreach (var answer in submitted) _questionDrafts.Remove(answer.QuestionId);
                    await RefreshProjectAsync();
                    Navigate("trail");
                }
            });
        }), ActionButton("Voltar", () => Navigate(isExam ? "lesson" : "home"), false)));
        Display(stack);
    }

    private async Task<bool> ConfirmExamSubmissionAsync(int questionCount)
    {
        var dialog = new Window
        {
            Title = "Confirmar envio da prova",
            Width = 450,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Content = Card(StackWith(
            Label("Enviar esta tentativa?", 20, Ink, FontWeight.Bold),
            Label($"Você respondeu {questionCount} questões. Após o envio, a IA corrigirá as respostas e registrará sua nota. Você poderá tentar novamente quantas vezes quiser.", 14, Muted),
            Actions(ActionButton("Revisar respostas", () => dialog.Close(false), false),
                ActionButton("Confirmar envio", () => dialog.Close(true)))));
        return await dialog.ShowDialog<bool>(this);
    }

    private void ShowTrail()
    {
        if (_project is null) return;
        if (_project.Diagnostic?.CompletedAt is null) { Navigate("diagnostic"); return; }
        _trailHasUnsavedEdits = false;
        Header("Etapa 2 · Trilha", _project.Stage == ProjectStage.Studying ? "Mapa da sua jornada" : "Revise sua trilha antes de começar");
        if (_project.Modules.Count == 0)
        {
            Display(DiagnosticResultView(_project.Diagnostic.Summary, async () =>
            {
                Notice("Montando a trilha de estudo…");
                await RunAsync(async () =>
                {
                    var result = await _service.GenerateTrailAsync(_project.Id, Ct);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível gerar a trilha.", true); return; }
                    await RefreshProjectAsync(); Navigate("trail");
                });
            }));
            return;
        }
        var editable = _project.Modules.OrderBy(m => m.Order)
            .Select(m => new EditableModule(m.Id, m.Title, m.Objective, m.Status) { Notes = m.UserNotes, WantsExamples = m.WantsExamples, WantsWhiteboard = m.WantsWhiteboard }).ToList();
        RenderTrailEditor(editable);
    }

    private sealed class EditableModule(Guid? id, string title, string objective, ModuleStatus status)
    {
        public Guid? Id { get; } = id;
        public ModuleStatus Status { get; } = status;
        public string Title { get; set; } = title;
        public string Objective { get; set; } = objective;
        public string Notes { get; set; } = "";
        public bool WantsExamples { get; set; } = true;
        public bool WantsWhiteboard { get; set; } = true;
    }

    private void RenderTrailEditor(List<EditableModule> modules)
    {
        if (_project is null) return;
        var confirmed = _project.Stage == ProjectStage.Studying;
        var stack = Stack(16);
        stack.Children.Add(Label(confirmed
            ? "Módulos iniciados ficam protegidos. Você pode ajustar os próximos módulos e seguir no seu ritmo."
            : "Renomeie, adicione, remova e reorganize os módulos. Confirme quando a trilha fizer sentido para você.", 14, Muted));
        var inputs = new List<(EditableModule module, TextBox title, TextBox objective, TextBox notes, CheckBox examples, CheckBox board)>();
        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            var locked = confirmed && module.Status is ModuleStatus.InProgress or ModuleStatus.Completed;
            var title = Field(module.Title);
            var objective = Field(module.Objective, multiline: true);
            var notes = Field(module.Notes, "Ex.: quero exemplos do dia a dia, evitar teoria longa, praticar com casos reais, comparar com X…", multiline: true);
            notes.MinHeight = 80;
            var examples = new CheckBox { Content = "Quero exemplos práticos", IsChecked = module.WantsExamples };
            var board = new CheckBox { Content = "Usar a lousa (desenhos e diagramas)", IsChecked = module.WantsWhiteboard };
            title.IsEnabled = objective.IsEnabled = notes.IsEnabled = examples.IsEnabled = board.IsEnabled = !locked;
            notes.TextChanged += (_, _) => _trailHasUnsavedEdits = true;
            examples.IsCheckedChanged += (_, _) => _trailHasUnsavedEdits = true;
            board.IsCheckedChanged += (_, _) => _trailHasUnsavedEdits = true;
            title.TextChanged += (_, _) => _trailHasUnsavedEdits = true;
            objective.TextChanged += (_, _) => _trailHasUnsavedEdits = true;
            inputs.Add((module, title, objective, notes, examples, board));
            var body = Stack(10);
            body.Children.Add(Label($"MÓDULO {i + 1:00} · {StatusName(module.Status)}", 12, Teal, FontWeight.Bold));
            body.Children.Add(FormField("Título", title));
            body.Children.Add(FormField("Objetivo", objective));
            body.Children.Add(FormField("O que você quer neste módulo (opcional)", notes, "A IA segue isto ao criar a aula, os exercícios e a prova: foco, tipo de exemplo, nível de detalhe, o que evitar."));
            body.Children.Add(Actions(examples, board));
            var position = i;
            if (!locked)
            {
                var up = ActionButton("↑", () => { Capture(); if (position > 0) { (modules[position - 1], modules[position]) = (modules[position], modules[position - 1]); _trailHasUnsavedEdits = true; RenderTrailEditor(modules); } }, false);
                var down = ActionButton("↓", () => { Capture(); if (position < modules.Count - 1) { (modules[position + 1], modules[position]) = (modules[position], modules[position + 1]); _trailHasUnsavedEdits = true; RenderTrailEditor(modules); } }, false);
                var remove = ActionButton("Remover", () => { Capture(); modules.RemoveAt(position); _trailHasUnsavedEdits = true; RenderTrailEditor(modules); }, false);
                AutomationProperties.SetName(up, $"Mover {module.Title} para cima");
                AutomationProperties.SetName(down, $"Mover {module.Title} para baixo");
                AutomationProperties.SetName(remove, $"Remover {module.Title}");
                up.IsEnabled = position > 0;
                down.IsEnabled = position < modules.Count - 1;
                body.Children.Add(Actions(up, down, remove));
            }
            else body.Children.Add(Label("Este módulo já começou e não pode ser alterado.", 12, Muted));
            if (confirmed && module.Id is { } id && module.Status != ModuleStatus.Locked)
                body.Children.Add(ActionButton("Estudar módulo →", () =>
                { _module = _project.Modules.First(m => m.Id == id); Navigate("lesson"); }));
            stack.Children.Add(Card(body));
        }
        stack.Children.Add(ActionButton("＋  Adicionar módulo", () =>
        { Capture(); modules.Add(new EditableModule(null, "Novo módulo", "Descreva o objetivo", ModuleStatus.Locked)); _trailHasUnsavedEdits = true; RenderTrailEditor(modules); }, false));
        stack.Children.Add(Actions(
            ActionButton(confirmed ? "Salvar alterações" : "Salvar rascunho", async () =>
            {
                Capture();
                await RunAsync(async () =>
                {
                    var outlines = modules.Select(m => new ModuleOutline(m.Id, m.Title.Trim(), m.Objective.Trim(), m.Notes.Trim(), m.WantsExamples, m.WantsWhiteboard)).ToArray();
                    var result = await _service.SetModulesAsync(_project.Id, outlines);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível salvar a trilha.", true); return; }
                    await RefreshProjectAsync(); Navigate("trail"); Notice("Trilha salva.");
                });
            }, confirmed),
            DeleteTrailButton(),
            confirmed ? ActionButton("Voltar ao projeto", () => Navigate("home"), false)
                : ActionButton("Salvar e iniciar estudos", async () =>
                {
                    Capture();
                    await RunAsync(async () =>
                    {
                        var outlines = modules.Select(m => new ModuleOutline(m.Id, m.Title.Trim(), m.Objective.Trim(), m.Notes.Trim(), m.WantsExamples, m.WantsWhiteboard)).ToArray();
                        var saved = await _service.SetModulesAsync(_project.Id, outlines);
                        if (!saved.IsSuccess) { Notice(saved.Error ?? "Não foi possível salvar a trilha.", true); return; }
                        var result = await _service.ConfirmTrailAsync(_project.Id);
                        if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível confirmar a trilha.", true); return; }
                        await RefreshProjectAsync(); _trailHasUnsavedEdits = false; Navigate("home"); Notice("Trilha confirmada. Sua primeira aula está liberada.");
                    });
                })));
        Display(stack);

        void Capture()
        {
            foreach (var item in inputs)
            {
                item.module.Title = item.title.Text ?? "";
                item.module.Objective = item.objective.Text ?? "";
                item.module.Notes = item.notes.Text ?? "";
                item.module.WantsExamples = item.examples.IsChecked != false;
                item.module.WantsWhiteboard = item.board.IsChecked != false;
            }
        }
    }
}
