using Avalonia.Controls;
using Avalonia.Media;
using StudyDesk.Infrastructure;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private void ShowSearch()
    {
        if (_project is null) { Navigate("welcome"); return; }
        Header("Busca", string.IsNullOrWhiteSpace(_searchQuery) ? "Buscar no projeto" : $"Resultados para “{_searchQuery}”");
        var root = Stack(14);
        var hits = ProjectSearch.Search(_project, _searchQuery);
        root.Children.Add(Label(hits.Count == 0
            ? "Nada encontrado em módulos, aulas, exercícios ou cartões. Tente outra palavra ou pergunte ao Tutor IA."
            : $"{hits.Count} resultado(s) em “{_project.Title}”. A busca ignora acentos e maiúsculas.", 14, Muted));
        if (hits.Count == 0 && !string.IsNullOrWhiteSpace(_searchQuery))
        {
            var query = _searchQuery;
            root.Children.Add(ActionButton("Perguntar ao tutor sobre isso →", () =>
            {
                _tutorDrafts[_project.Id] = $"Pode me explicar {query}?";
                Navigate("tutor");
            }, false));
        }
        foreach (var hit in hits)
        {
            var (kind, route) = hit.Kind switch
            {
                SearchHitKind.Lesson => ("AULA", "lesson"),
                SearchHitKind.Exercise => ("EXERCÍCIO", "practice"),
                SearchHitKind.Card => ("CARTÃO", "review"),
                _ => ("MÓDULO", "lesson")
            };
            var module = _project.Modules.FirstOrDefault(m => m.Id == hit.ModuleId);
            var body = StackWith(
                Label($"{kind} · {hit.ModuleTitle} · {hit.Section}", 11, Teal, FontWeight.Bold),
                Label(hit.Snippet, 14, Ink));
            if (module is not null)
            {
                var open = ActionButton(module.Status == Domain.ModuleStatus.Locked ? "Módulo bloqueado" : "Abrir →", () =>
                {
                    _module = module;
                    Navigate(route);
                }, false);
                open.IsEnabled = module.Status != Domain.ModuleStatus.Locked || route == "review";
                body.Children.Add(open);
            }
            root.Children.Add(Card(body, Brush.Parse("#11141A")));
        }
        Display(root);
    }
}
