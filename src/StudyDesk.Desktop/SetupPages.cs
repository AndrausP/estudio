using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StudyDesk.Application;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private void ShowProviders()
    {
        Header("Configurações", "Conexões de IA");
        var root = Stack(20);
        root.Children.Add(Label("Escolha como o Estúdio vai criar aulas e avaliar respostas. Teste a conexão antes de iniciar um projeto.", 15, Muted));
        foreach (var provider in _providers)
        {
            var row = Stack(10);
            row.Children.Add(Label(provider.Name, 18, Ink, FontWeight.SemiBold));
            row.Children.Add(Label(provider.Kind == ProviderKind.ClaudeCli
                ? $"Claude Code CLI · modelo: {(string.IsNullOrWhiteSpace(provider.Model) ? "padrão da sua conta" : provider.Model)} · autenticação pela instalação local"
                : $"{ProviderName(provider.Kind)} · modelo: {provider.Model}", 13, Muted));
            row.Children.Add(Actions(
                ActionButton("Testar conexão", async () => await TestProviderAsync(provider.Id), false),
                ActionButton("Editar", () => ShowProviderForm(provider), false),
                ActionButton("Excluir", async () => await DeleteProviderAsync(provider.Id), false)));
            root.Children.Add(Card(row));
        }
        if (_providers.Count == 0) root.Children.Add(Card(Label("Ainda não há conexões. Configure uma para começar.", 15, Muted)));
        root.Children.Add(ActionButton("＋  Adicionar conexão", () => ShowProviderForm()));
        Display(root);
    }

    private void ShowProviderForm(ProviderConfiguration? existing = null)
    {
        Header("Conexões de IA", existing is null ? "Nova conexão" : "Editar conexão");
        var name = Field(existing?.Name, "Ex.: Claude Code do meu computador");
        var kind = new ComboBox { ItemsSource = SupportedProviders.Select(ProviderName).ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(SupportedProviders, existing?.Kind ?? ProviderKind.ClaudeCli)), MinHeight = 40 };
        var endpoint = Field(existing?.Endpoint, "Ex.: https://api.openai.com/v1");
        var model = Field(existing?.Model, "Ex.: gpt-4.1 (na CLI, deixe vazio para o padrão da conta)");
        var models = new ComboBox { MinHeight = 40, PlaceholderText = "Escolha na lista ou digite acima" };
        models.SelectionChanged += (_, _) => { if (models.SelectedItem is string chosen) model.Text = chosen; };
        var key = Field(null, existing is null ? "Chave da API, se necessária" : "Deixe vazio para manter a chave atual");
        key.PasswordChar = '●';
        var stack = Stack(18);
        stack.Children.Add(FormField("Nome da conexão", name));
        stack.Children.Add(FormField("Tipo de provedor", kind));
        var cliInfo = Label("Claude Code CLI: instale o comando claude e faça login antes de testar. Nenhuma chave precisa ser copiada aqui.", 13, Muted);
        var endpointRow = FormField("Endereço da API", endpoint, "Use a URL base HTTPS do provedor compatível com OpenAI.");
        var modelHelp = Label("", 12, Muted);
        var modelRow = FormField("Modelo da IA", StackWith(model, models, Actions(ActionButton("Buscar modelos", async () =>
        {
            var selectedKind = SupportedProviders[Math.Max(0, kind.SelectedIndex)];
            var input = new ProviderInput(name.Text?.Trim() ?? "", selectedKind, endpoint.Text?.Trim() ?? "", model.Text?.Trim() ?? "", key.Text?.Trim());
            Notice("Buscando modelos disponíveis…");
            await RunAsync(async () =>
            {
                var result = await _service.ListModelsAsync(input, existing?.Id, Ct);
                if (!result.IsSuccess || result.Value is null || result.Value.Count == 0) { Notice(result.Error ?? "Nenhum modelo encontrado. Digite o nome do modelo.", true); return; }
                models.ItemsSource = result.Value;
                Notice($"{result.Value.Count} modelos encontrados. Escolha um na lista.");
            });
        }, false)), modelHelp));
        if (existing?.Kind is null or ProviderKind.ClaudeCli) models.ItemsSource = new[] { "opus", "sonnet", "haiku", "claude-opus-5-5", "claude-sonnet-5-5", "claude-fable-5-1", "claude-haiku-4-5-20251001" };
        var keyRow = FormField("Chave de API", key, "A chave é protegida pelo armazenamento seguro do sistema.");
        stack.Children.Add(cliInfo);
        stack.Children.Add(endpointRow);
        stack.Children.Add(modelRow);
        stack.Children.Add(keyRow);
        void UpdateProviderFields()
        {
            var isApi = SupportedProviders[Math.Max(0, kind.SelectedIndex)] == ProviderKind.OpenAiCompatible;
            cliInfo.IsVisible = !isApi;
            endpointRow.IsVisible = keyRow.IsVisible = isApi;
            modelHelp.Text = isApi ? "A lista vem do endereço da API configurado." : "Apelidos como opus, sonnet e haiku usam sempre a versão mais recente; vazio usa o padrão da sua conta.";
            if (!isApi) models.ItemsSource = new[] { "opus", "sonnet", "haiku", "claude-opus-5-5", "claude-sonnet-5-5", "claude-fable-5-1", "claude-haiku-4-5-20251001" };
        }
        kind.SelectionChanged += (_, _) => UpdateProviderFields();
        UpdateProviderFields();
        stack.Children.Add(Actions(ActionButton("Salvar conexão", async () =>
        {
            await RunAsync(async () =>
            {
                var selectedKind = SupportedProviders[Math.Max(0, kind.SelectedIndex)];
                var input = new ProviderInput(Required(name, "o nome"), selectedKind,
                    selectedKind == ProviderKind.OpenAiCompatible ? Required(endpoint, "o endereço da API") : "",
                    selectedKind == ProviderKind.OpenAiCompatible ? Required(model, "o modelo") : model.Text?.Trim() ?? "",
                    key.Text?.Trim());
                var result = await _service.SaveProviderAsync(input, existing?.Id);
                if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível salvar a conexão.", true); return; }
                _providers = (await _service.ListProvidersAsync()).Where(p => SupportedProviders.Contains(p.Kind)).ToArray();
                ShowProviders();
                Notice("Conexão salva. Faça o teste antes de estudar.");
            });
        }), ActionButton("Cancelar", ShowProviders, false)));
        Display(Card(stack));
    }

    private async Task TestProviderAsync(Guid id)
    {
        Notice("Testando a conexão…");
        await RunAsync(async () =>
        {
            var result = await _service.TestProviderAsync(id, Ct);
            Notice(result.IsSuccess ? result.Value ?? "Conexão pronta." : result.Error ?? "Falha na conexão.", !result.IsSuccess);
        });
    }

    private async Task DeleteProviderAsync(Guid id)
    {
        await RunAsync(async () =>
        {
            var result = await _service.DeleteProviderAsync(id);
            if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível excluir.", true); return; }
            _providers = (await _service.ListProvidersAsync()).Where(p => SupportedProviders.Contains(p.Kind)).ToArray();
            ShowProviders();
            Notice("Conexão excluída.");
        });
    }

    private void ShowNewProject()
    {
        Header("Novo projeto", "O que você quer aprender?");
        var title = Field(null, "Ex.: Minha faculdade de C#");
        var goal = Field(null, "Ex.: Criar aplicações profissionais em C# partindo do básico", true);
        var style = Field("Didático, com exemplos progressivos e linguagem clara", "Como seu professor deve explicar?", true);
        var provider = new ComboBox { ItemsSource = _providers.Select(p => p.Name).ToArray(), SelectedIndex = _providers.Count > 0 ? 0 : -1, MinHeight = 40 };
        var details = Field(null, "Ex.: já sei lógica de programação, tenho 1h por dia, prova em 2 meses, quero muita prática.", true);
        var pendingFiles = new List<string>();
        var stack = Stack(18);
        stack.Children.Add(Label("Um projeto é uma jornada independente. Seu professor virtual vai adaptar a trilha depois de conhecer seu nível.", 15, Muted));
        stack.Children.Add(FormField("Nome do projeto", title));
        stack.Children.Add(FormField("Objetivo de aprendizagem", goal));
        stack.Children.Add(FormField("Como explicar", style, "Ex.: direto ao ponto, com analogias, exercícios práticos ou aprofundamento teórico."));
        stack.Children.Add(FormField("Detalhes (opcional)", details, "Conte o que a IA deve saber: seu nível, prazo, foco, o que evitar."));
        stack.Children.Add(FormField("Material de estudo (opcional)", PendingFilesPanel(pendingFiles)));
        stack.Children.Add(FormField("Conexão de IA", provider));
        if (_providers.Count == 0) stack.Children.Add(Label("Configure uma conexão de IA para criar o projeto.", 13, Brush.Parse("#FFB2C0")));
        stack.Children.Add(Actions(
            ActionButton("Criar projeto", async () =>
            {
                await RunAsync(async () =>
                {
                    if (provider.SelectedIndex < 0 || provider.SelectedIndex >= _providers.Count)
                        throw new InvalidOperationException("Escolha uma conexão de IA.");
                    var input = new CreateProjectInput(Required(title, "o nome do projeto"), Required(goal, "o objetivo"),
                        Required(style, "a forma de explicar"), _providers[provider.SelectedIndex].Id, details.Text?.Trim() ?? "");
                    if (_focus?.IsRunning == true && _focusProjectId is { } previousId)
                    {
                        await _service.PauseFocusAsync(previousId, _focusModuleId);
                        _focus = null;
                    }
                    var result = await _service.CreateProjectAsync(input);
                    if (!result.IsSuccess || result.Value is null) { Notice(result.Error ?? "Não foi possível criar o projeto.", true); return; }
                    var problems = pendingFiles.Count == 0 ? [] : await AttachPendingAsync(result.Value.Id, pendingFiles);
                    _project = (await _service.GetProjectAsync(result.Value.Id)).Value ?? result.Value;
                    RememberProject(_project.Id);
                    _module = null;
                    _projects = await _service.ListProjectsAsync();
                    RenderSidebar();
                    Navigate("home");
                    Notice(problems.Count == 0 ? "Projeto criado. O próximo passo é o diagnóstico obrigatório." : "Projeto criado, mas: " + string.Join(" ", problems), problems.Count > 0);
                });
            }),
            ActionButton("Configurar IA", () => Navigate("providers"), false)));
        Display(Card(stack));
    }

    private void ShowProjectHome()
    {
        if (_project is null) { ShowWelcome(); return; }
        Header("Projeto de estudo", _project.Title);
        var stack = Stack(18);
        stack.Children.Add(Label(_project.Goal, 16, Muted));
        if (_project.Diagnostic?.CompletedAt is null)
        {
            stack.Children.Add(Card(StackWith(
                Label("Primeiro: descubra seu ponto de partida", 20, Ink, FontWeight.Bold),
                Label("O diagnóstico é obrigatório e ajuda a IA a montar uma trilha adequada ao que você já sabe.", 14, Muted),
                ActionButton(_project.Diagnostic?.Questions.Count > 0 ? "Continuar diagnóstico" : "Começar diagnóstico", () => Navigate("diagnostic")))));
        }
        else if (_project.Stage == ProjectStage.TrailDraft || _project.Modules.Count == 0)
        {
            stack.Children.Add(Card(StackWith(
                Label(_project.Modules.Count == 0 ? "Diagnóstico concluído. Agora vem sua trilha." : "Sua trilha está pronta para revisão", 20, Ink, FontWeight.Bold),
                Label(_project.Diagnostic.Summary, 14, Muted),
                ActionButton(_project.Modules.Count == 0 ? "Criar minha trilha →" : "Revisar trilha →", () => Navigate("trail")))));
        }
        else
        {
            Header("Seu campus pessoal", "Sua linha de estudo");
            Display(DashboardContent());
            return;
        }
        Display(stack);
    }

    private Border ModuleCard(StudyModule module)
    {
        var stack = Stack(8);
        stack.Children.Add(Label($"{module.Order + 1:00}  {module.Title}", 17, Ink, FontWeight.SemiBold));
        stack.Children.Add(Label(module.Objective, 13, Muted));
        stack.Children.Add(Label(StatusName(module.Status) + (module.Attempts.Count > 0 ? $" · Melhor nota: {module.BestScore}%" : ""), 12, Teal, FontWeight.SemiBold));
        if (module.Status == ModuleStatus.Locked)
        {
            var previous = _project?.Modules.FirstOrDefault(m => m.Order == module.Order - 1);
            stack.Children.Add(Label(previous is null ? "Conclua o módulo anterior para liberar." :
                $"Para liberar: alcance 70% na prova de “{previous.Title}”.", 12, Muted));
        }
        if (module.Status != ModuleStatus.Locked)
            stack.Children.Add(ActionButton("Abrir módulo →", () => { _module = module; Navigate("lesson"); }, false));
        return Card(stack);
    }
}
