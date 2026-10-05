using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private const string AttachmentHelp = "PDF, Word (.docx), Markdown, texto, código e imagens (PNG, JPG, GIF, WEBP). Até 25 MB por arquivo. A IA usa esse material para montar o diagnóstico, a trilha e as aulas.";

    private async Task<List<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolha o material de estudo",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Material de estudo")
                {
                    Patterns = ["*.pdf", "*.docx", "*.md", "*.markdown", "*.txt", "*.csv", "*.json", "*.xml", "*.html", "*.cs", "*.js", "*.ts", "*.py", "*.java", "*.sql", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"]
                },
                FilePickerFileTypes.All
            ]
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
    }

    private static string FileSize(long bytes) => bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / 1024.0 / 1024.0:0.0} MB";

    private static Control FileRow(string name, string detail, Action remove)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        var text = StackWith(Label(name, 14, Ink, FontWeight.SemiBold), Label(detail, 12, Muted));
        ((StackPanel)text).Spacing = 2;
        var button = new Button { Content = "✕", Background = Surface, Foreground = Danger, Padding = new Thickness(10, 6), VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(button, $"Remover {name}");
        button.Click += (_, _) => remove();
        Grid.SetColumn(button, 1);
        row.Children.Add(text);
        row.Children.Add(button);
        return row;
    }

    /// <summary>Lista de arquivos ainda não enviados (tela de novo projeto); eles são anexados quando o projeto é criado.</summary>
    private Control PendingFilesPanel(List<string> files)
    {
        var host = new ContentControl();
        void Render()
        {
            var stack = Stack(10);
            stack.Children.Add(Label(AttachmentHelp, 13, Muted));
            foreach (var path in files.ToList())
            {
                long size = 0;
                try { size = new FileInfo(path).Length; } catch (IOException) { }
                stack.Children.Add(FileRow(Path.GetFileName(path), FileSize(size), () => { files.Remove(path); Render(); }));
            }
            stack.Children.Add(Actions(ActionButton("📎  Anexar arquivos", async () =>
            {
                foreach (var path in await PickFilesAsync())
                    if (!files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
                Render();
            }, false)));
            host.Content = stack;
        }
        Render();
        return host;
    }

    /// <summary>Envia os arquivos pendentes ao projeto recém-criado e avisa o que não deu certo.</summary>
    private async Task<List<string>> AttachPendingAsync(Guid projectId, IEnumerable<string> files)
    {
        var problems = new List<string>();
        foreach (var path in files)
        {
            var result = await _service.AddAttachmentAsync(projectId, path, Ct);
            if (!result.IsSuccess) problems.Add(result.Error ?? $"Falha ao anexar {Path.GetFileName(path)}.");
            else if (result.Value!.Kind != AttachmentKind.Image && string.IsNullOrWhiteSpace(result.Value.ExtractedText))
                problems.Add($"“{result.Value.FileName}” não tem texto selecionável (talvez seja um PDF escaneado); a IA não conseguirá lê-lo.");
        }
        return problems;
    }

    /// <summary>Material e detalhes do projeto, editáveis a qualquer momento em ⚙ Projeto.</summary>
    private Control ProjectMaterialsCard(StudyProject project)
    {
        var details = Field(project.Details, "Ex.: quero focar em prática, já sei o básico de lógica, prova em 2 meses, prefiro exemplos do dia a dia.", true);
        details.MinHeight = 100;
        var stack = Stack(12);
        stack.Children.Add(Label("Material e detalhes", 19, Ink, FontWeight.Bold));
        stack.Children.Add(Label(AttachmentHelp, 13, Muted));
        if (project.Attachments.Count == 0) stack.Children.Add(Label("Nenhum arquivo anexado.", 13, Muted));
        foreach (var attachment in project.Attachments)
        {
            var current = attachment;
            var detail = $"{FileSize(current.SizeBytes)} · {(current.Kind == AttachmentKind.Image ? "imagem" : string.IsNullOrWhiteSpace(current.ExtractedText) ? "sem texto legível" : $"{current.ExtractedText.Length:N0} caracteres lidos")}";
            stack.Children.Add(FileRow(current.FileName, detail, async () => await RunAsync(async () =>
            {
                var result = await _service.RemoveAttachmentAsync(project.Id, current.Id);
                if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível remover.", true); return; }
                await RefreshProjectAsync();
                Navigate("project");
            })));
        }
        stack.Children.Add(ActionButton("📎  Anexar arquivos", async () =>
        {
            var picked = await PickFilesAsync();
            if (picked.Count == 0) return;
            await RunAsync(async () =>
            {
                var problems = await AttachPendingAsync(project.Id, picked);
                await RefreshProjectAsync();
                Navigate("project");
                Notice(problems.Count == 0 ? "Material anexado." : string.Join(" ", problems), problems.Count > 0);
            });
        }, false));
        stack.Children.Add(FormField("Detalhes para a IA considerar em todo o projeto", details));
        stack.Children.Add(ActionButton("Salvar detalhes", async () =>
        {
            var text = details.Text ?? "";
            await RunAsync(async () =>
            {
                var result = await _service.UpdateProjectDetailsAsync(project.Id, text);
                if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível salvar.", true); return; }
                await RefreshProjectAsync();
                Notice("Detalhes salvos. Valem para as próximas aulas e exercícios.");
            });
        }, false));
        return Card(stack);
    }
}
