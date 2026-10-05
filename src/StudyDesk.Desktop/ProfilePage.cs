using Avalonia.Controls;
using Avalonia.Media;
using StudyDesk.Domain;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    /// <summary>"Sobre você": texto livre + resumo feito pela IA, usados como contexto em todas as aulas.</summary>
    private async void ShowProfile()
    {
        Header("Configurações", "Sobre você");
        var profile = await _service.GetProfileAsync();
        var about = Field(profile.About, "Ex.: Sou analista júnior, trabalho com C# há 1 ano, estudo à noite 1h por dia, aprendo melhor com exemplos reais e quero chegar a pleno em 6 meses.", true);
        about.MinHeight = 170;
        var summary = Field(profile.Summary, "O resumo aparece aqui. Você pode editar antes de salvar.", true);
        summary.MinHeight = 120;
        var provider = new ComboBox { ItemsSource = _providers.Select(p => p.Name).ToArray(), SelectedIndex = _providers.Count > 0 ? 0 : -1, MinHeight = 40 };

        var root = Stack(18);
        root.Children.Add(Label("Conte quem você é, sua experiência, objetivos e como gosta de aprender. A IA faz um resumo e o professor passa a usá-lo em todos os projetos para falar no seu nível. É opcional e fica apenas neste computador (o texto vai à IA configurada quando você gera conteúdo).", 14, Muted));
        root.Children.Add(Card(StackWith(
            Label("Sobre você", 18, Ink, FontWeight.Bold),
            FormField("Escreva livremente", about, "Não inclua senhas, documentos ou dados de terceiros."),
            FormField("Conexão usada para resumir", provider),
            ActionButton("✦  Resumir com IA", async () =>
            {
                if (provider.SelectedIndex < 0 || provider.SelectedIndex >= _providers.Count) { Notice("Configure uma conexão de IA para resumir.", true); return; }
                var text = about.Text ?? "";
                var providerId = _providers[provider.SelectedIndex].Id;
                Notice("A IA está resumindo o que você escreveu…");
                await RunAsync(async () =>
                {
                    var result = await _service.SummarizeProfileAsync(text, providerId, Ct);
                    if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível resumir.", true); return; }
                    summary.Text = result.Value;
                    Notice("Resumo pronto. Revise, edite se quiser e salve.");
                });
            }, false),
            FormField("Resumo que o professor vai usar", summary, "Se ficar vazio, o professor usa o texto acima."),
            Actions(ActionButton("Salvar", async () =>
            {
                var aboutText = about.Text ?? "";
                var summaryText = summary.Text ?? "";
                await RunAsync(async () =>
                {
                    var result = await _service.SaveProfileAsync(aboutText, summaryText);
                    Notice(result.IsSuccess ? "Perfil salvo. O professor vai usá-lo nas próximas aulas." : result.Error ?? "Não foi possível salvar.", !result.IsSuccess);
                });
            }), ActionButton("Voltar", () => Navigate(_project is null ? "welcome" : "home"), false)))));
        Display(root);
    }
}
