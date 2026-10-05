using StudyDesk.Domain;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using StudyDesk.Application;
using StudyDesk.Infrastructure;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private void ShowProjectSettings()
    {
        if (_project is null) { Navigate("welcome"); return; }
        Header("Configurações do projeto", _project.Title);
        var project = _project;
        var title = Field(project.Title, "Nome do projeto");
        var style = Field(project.ExplanationStyle, "Como seu professor deve explicar?", true);
        var providerIndex = Math.Max(0, _providers.ToList().FindIndex(p => p.Id == project.ProviderId));
        var provider = new ComboBox
        {
            ItemsSource = _providers.Select(p => p.Name).ToArray(),
            SelectedIndex = _providers.Count > 0 ? providerIndex : -1,
            MinHeight = 40
        };
        var root = Stack(18);
        root.Children.Add(Card(StackWith(
            Label("Dados do projeto", 19, Ink, FontWeight.Bold),
            Label("O objetivo e o diagnóstico ficam fixos para não invalidar a trilha. Aqui você ajusta nome, estilo das próximas aulas e a conexão de IA.", 13, Muted),
            FormField("Nome do projeto", title),
            FormField("Como explicar", style, "Vale para aulas, exercícios e tutor gerados daqui em diante."),
            FormField("Conexão de IA", provider, "Troque, por exemplo, da Claude Code CLI para uma API se a CLI estiver indisponível."),
            Actions(ActionButton("Salvar alterações", async () =>
            {
                await RunAsync(async () =>
                {
                    if (provider.SelectedIndex < 0 || provider.SelectedIndex >= _providers.Count)
                        throw new InvalidOperationException("Escolha uma conexão de IA.");
                    var input = new UpdateProjectInput(Required(title, "o nome do projeto"), Required(style, "a forma de explicar"),
                        _providers[provider.SelectedIndex].Id);
                    var result = await _service.UpdateProjectAsync(project.Id, input, Ct);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível salvar o projeto.", true); return; }
                    await RefreshProjectAsync();
                    Navigate("project");
                    Notice("Projeto atualizado.");
                });
            }), ActionButton("Voltar", () => Navigate("home"), false)))));

        root.Children.Add(ProjectMaterialsCard(project));

        root.Children.Add(Card(StackWith(
            Label("Caderno de estudo", 19, Ink, FontWeight.Bold),
            Label("Exporte aulas, cartões de revisão e histórico de provas em um arquivo Markdown para ler, imprimir ou guardar fora do aplicativo.", 13, Muted),
            ActionButton("Exportar caderno (.md)", ExportProjectNotebook, false))));

        root.Children.Add(Card(StackWith(
            Label("Backups automáticos", 19, Ink, FontWeight.Bold),
            Label("Ao abrir o Estúdio, uma cópia do banco é salva por dia (as 7 mais recentes ficam guardadas). Para restaurar, feche o app e copie um backup sobre o arquivo study.db.", 13, Muted),
            ActionButton("Abrir pasta de backups", () => OpenFolder(Path.Combine(_dataDirectory, "backups")), false))));

        var delete = ActionButton("Excluir trilha", async () => await DeleteProjectAsync(_project!), false);
        delete.Foreground = Brush.Parse("#FFB2C0");
        delete.BorderBrush = Brush.Parse("#7A3445");
        root.Children.Add(Card(StackWith(
            Label("Zona de risco", 19, Brush.Parse("#FFB2C0"), FontWeight.Bold),
            Label("Excluir remove o projeto, aulas, provas, cartões e tempo de foco deste computador. Exporte o caderno antes se quiser guardar o conteúdo.", 13, Muted),
            delete), Brush.Parse("#1A1216")));
        Display(root);
    }

    private void ExportProjectNotebook()
    {
        if (_project is null) return;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Estudio");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, ProjectExporter.SafeFileName(_project.Title) + ".md");
            File.WriteAllText(path, ProjectExporter.ToMarkdown(_project), new UTF8Encoding(false));
            Notice($"Caderno exportado para {path}");
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Notice("Não foi possível exportar o caderno: " + ex.Message, true); }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            else Notice(folder);
        }
        catch (Exception ex) { Notice("Não foi possível abrir a pasta: " + ex.Message, true); }
    }

    private async Task DeleteProjectAsync(StudyProject project)
    {
        if (_busy) return;
        var dialog = new Window
        {
            Title = "Excluir trilha", Width = 480, Height = 270, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Content = Card(StackWith(
            Label($"Excluir a trilha “{project.Title}”?", 20, Ink, FontWeight.Bold),
            Label("Esta ação não pode ser desfeita. Aulas, provas, cartões e tempo de foco deste projeto serão apagados.", 14, Muted),
            Actions(ActionButton("Manter trilha", () => dialog.Close(false), false),
                ActionButton("Excluir definitivamente", () => dialog.Close(true)))));
        if (!await dialog.ShowDialog<bool>(this)) return;
        await RunAsync(async () =>
        {
            if (_focus?.IsRunning == true && _focusProjectId == project.Id) _focus = null;
            var result = await _service.DeleteProjectAsync(project.Id, Ct);
            if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível excluir o projeto.", true); return; }
            _tutorHistory.Remove(project.Id);
            _tutorDrafts.Remove(project.Id);
            _projects = await _service.ListProjectsAsync();
            if (_project?.Id == project.Id)
            {
                _project = _projects.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt).FirstOrDefault();
                _module = null;
                if (_project is not null) RememberProject(_project.Id);
                RenderSidebar();
                Navigate(_project is null ? "welcome" : "home");
            }
            else RenderSidebar();
            Notice($"Trilha “{project.Title}” excluída.");
        });
    }
}
