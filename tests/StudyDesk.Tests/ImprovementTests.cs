using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StudyDesk.Application;
using StudyDesk.Domain;
using StudyDesk.Infrastructure;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class ImprovementTests
{
    private string _directory = null!;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private const string ExamV1 = """
        [{"Kind":0,"Prompt":"Qual palavra declara variável local implícita?","Options":["var","let","dim"],"CorrectAnswer":"A","Rubric":"var","Weight":50},
         {"Kind":1,"Prompt":"Explique o que é uma função.","Options":[],"CorrectAnswer":"","Rubric":"Recebe e retorna valores","Weight":50}]
        """;
    private const string ExamV2 = """
        Claro! Aqui está a nova prova:
        ```json
        [{"Kind":0,"Prompt":"Qual tipo representa texto?","Options":["a) int","b) string","c) bool"],"CorrectAnswer":"b)","Rubric":"string","Weight":60},
         {"Kind":1,"Prompt":"Quando usar uma função?","Options":[],"CorrectAnswer":"","Rubric":"Reuso","Weight":40}]
        ```
        """;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "studydesk-improve-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestCase("B", "dois")]
    [TestCase("b)", "dois")]
    [TestCase("2", "dois")]
    [TestCase("B) dois", "dois")]
    [TestCase(" dois ", "dois")]
    [TestCase("DOIS", "dois")]
    public void Should_NormalizeCorrectAnswer_ToOptionText(string correct, string expected)
        => Assert.That(AiResponseParser.NormalizeCorrectAnswer(correct, ["um", "dois", "três"]), Is.EqualTo(expected));

    [Test]
    public void Should_ExtractJson_When_ModelAddsProseAndFences()
    {
        var raw = "Segue o resultado:\n```json\n{\"summary\":\"Nível {básico} com \\\"aspas\\\"\"}\n```\nBons estudos!";
        Assert.That(AiResponseParser.ExtractJson(raw), Is.EqualTo("{\"summary\":\"Nível {básico} com \\\"aspas\\\"\"}"));
    }

    [Test]
    public void Should_CountStreak_IncludingYesterday_AndIgnoreGaps()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var module = new StudyModule();
        DateTime At(int daysAgo) => DateTime.Now.Date.AddDays(-daysAgo).AddHours(12).ToUniversalTime();
        module.Attempts.Add(new ExamAttempt { CreatedAt = At(1), Score = 50 });
        module.Attempts.Add(new ExamAttempt { CreatedAt = At(2), Score = 50 });
        module.Attempts.Add(new ExamAttempt { CreatedAt = At(4), Score = 50 });
        var project = new StudyProject { Modules = [module] };
        Assert.Multiple(() =>
        {
            Assert.That(StudyStats.CurrentStreak(project, today), Is.EqualTo(2));
            Assert.That(StudyStats.CurrentStreak(project, today.AddDays(2)), Is.EqualTo(0));
        });
    }

    [Test]
    public void Should_ExportNotebook_WithLessonCardsAndSafeName()
    {
        var module = new StudyModule
        {
            Title = "Funções", Objective = "Entender funções", Status = ModuleStatus.Completed,
            Lesson = new Lesson { Objectives = "Definir função", Explanation = "## Ideia\nUma função…", Examples = "```csharp\nint Dobro(int x) => x * 2;\n```", Summary = "Resumo com ação" }
        };
        module.ReviewCards.Add(new ReviewCard { Question = "O que é função?", Answer = "Bloco reutilizável" });
        module.ReviewCards.Add(new ReviewCard { Question = "Arquivado", Answer = "x", IsArchived = true });
        var project = new StudyProject { Title = "C#: básico?", Goal = "Aprender C#", Modules = [module] };
        var markdown = ProjectExporter.ToMarkdown(project);
        Assert.Multiple(() =>
        {
            Assert.That(markdown, Does.Contain("# C#: básico?"));
            Assert.That(markdown, Does.Contain("int Dobro(int x) => x * 2;"));
            Assert.That(markdown, Does.Contain("O que é função?"));
            Assert.That(markdown, Does.Not.Contain("Arquivado"));
            Assert.That(ProjectExporter.SafeFileName(project.Title), Is.EqualTo("C#_ básico_"));
        });
    }

    [Test]
    public async Task Should_RegenerateExam_OnlyAfterAttempt_AndKeepFrozenHistory()
    {
        using var listener = StartListener(out var endpoint);
        var bodies = new List<string>();
        var server = ServeAsync(listener, [ExamV1, "{\"fraction\":1,\"feedback\":\"Ótimo\"}", ExamV2], bodies);
        var service = StudyService.CreateDefault(_directory);
        var (project, module) = await ReadyProjectAsync(service, endpoint);
        var exam = await service.GetOrCreateExamAsync(project.Id, module.Id);
        var blocked = await service.RegenerateExamAsync(project.Id, module.Id);
        var answers = exam.Value!.Questions.Select(q => new QuestionAnswer { QuestionId = q.Id, Answer = q.Kind == QuestionKind.MultipleChoice ? "var" : "Recebe e retorna" }).ToList();
        var attempt = await service.SubmitExamAsync(project.Id, module.Id, answers);
        var regenerated = await service.RegenerateExamAsync(project.Id, module.Id);
        await server;
        var reopened = (await service.GetProjectAsync(project.Id)).Value!.Modules[0];
        Assert.Multiple(() =>
        {
            Assert.That(exam.Value.Questions[0].CorrectAnswer, Is.EqualTo("var"));
            Assert.That(blocked.IsSuccess, Is.False);
            Assert.That(attempt.Value!.Score, Is.EqualTo(100));
            Assert.That(regenerated.IsSuccess, Is.True, regenerated.Error);
            Assert.That(regenerated.Value!.Id, Is.Not.EqualTo(exam.Value.Id));
            Assert.That(regenerated.Value.Questions[0].CorrectAnswer, Is.EqualTo("b) string"));
            Assert.That(UserPrompt(bodies[2]), Does.Contain("nova versão"));
            Assert.That(reopened.Exam!.Id, Is.EqualTo(regenerated.Value.Id));
            Assert.That(reopened.Attempts.Single().FrozenQuestions[0].Prompt, Does.Contain("var"));
            Assert.That(reopened.Status, Is.EqualTo(ModuleStatus.Completed));
        });
    }

    [Test]
    public async Task Should_SendRecentConversation_ToTutor()
    {
        using var listener = StartListener(out var endpoint);
        var bodies = new List<string>();
        var server = ServeAsync(listener, ["{\"answer\":\"Claro, outro exemplo.\"}"], bodies);
        var service = StudyService.CreateDefault(_directory);
        var (project, module) = await ReadyProjectAsync(service, endpoint);
        var reply = await service.AskTutorAsync(project.Id, module.Id, "Explique melhor", [new TutorTurn(true, "O que é recursão?"), new TutorTurn(false, "É uma função que chama a si mesma.")]);
        await server;
        Assert.Multiple(() =>
        {
            Assert.That(reply.Value, Is.EqualTo("Claro, outro exemplo."));
            Assert.That(UserPrompt(bodies[0]), Does.Contain("O que é recursão?"));
            Assert.That(UserPrompt(bodies[0]), Does.Contain("chama a si mesma"));
            Assert.That(bodies[0], Does.Contain("\"role\":\"system\""));
        });
    }

    [Test]
    public async Task Should_UpdateAndDeleteProject_WithFocusSessions()
    {
        var service = StudyService.CreateDefault(_directory);
        var cli = await service.SaveProviderAsync(new ProviderInput("Claude", ProviderKind.ClaudeCli, "", "", null));
        var api = await service.SaveProviderAsync(new ProviderInput("API", ProviderKind.OpenAiCompatible, "http://127.0.0.1:1/v1", "m", null));
        var project = (await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender", "Didático", cli.Value!.Id))).Value!;
        var keep = (await service.CreateProjectAsync(new CreateProjectInput("Outro", "Outro", "Didático", cli.Value.Id))).Value!;
        await service.StartFocusAsync(project.Id);
        await service.PauseFocusAsync(project.Id);
        var updated = await service.UpdateProjectAsync(project.Id, new UpdateProjectInput("  C# avançado ", "Direto", api.Value!.Id));
        var invalid = await service.UpdateProjectAsync(project.Id, new UpdateProjectInput("", "Direto", api.Value.Id));
        var providerInUse = await service.DeleteProviderAsync(api.Value.Id);
        var deleted = await service.DeleteProjectAsync(project.Id);
        var reopened = StudyService.CreateDefault(_directory);
        var projects = await reopened.ListProjectsAsync();
        var focus = await new LocalStore(_directory).FocusAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(updated.Value!.Title, Is.EqualTo("C# avançado"));
            Assert.That(updated.Value.ProviderId, Is.EqualTo(api.Value.Id));
            Assert.That(invalid.IsSuccess, Is.False);
            Assert.That(providerInUse.IsSuccess, Is.False);
            Assert.That(deleted.IsSuccess, Is.True);
            Assert.That(projects.Select(x => x.Id), Is.EquivalentTo(new[] { keep.Id }));
            Assert.That(focus.Any(x => x.ProjectId == project.Id), Is.False);
        });
    }

    private async Task<(StudyProject, StudyModule)> ReadyProjectAsync(IStudyService service, string endpoint)
    {
        var provider = await service.SaveProviderAsync(new ProviderInput("Mock", ProviderKind.OpenAiCompatible, endpoint, "mock", null));
        var project = (await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender funções", "Exemplos", provider.Value!.Id))).Value!;
        var module = new StudyModule { Title = "Funções", Objective = "Explicar funções", Status = ModuleStatus.Available, Lesson = new Lesson { Objectives = "Explicar função", Summary = "Função recebe e retorna valores" } };
        project.Modules = [module];
        project.Stage = ProjectStage.Studying;
        await new LocalStore(_directory).SaveAsync(project, CancellationToken.None);
        return (project, module);
    }

    private static TcpListener StartListener(out string endpoint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        return listener;
    }

    private static async Task ServeAsync(TcpListener listener, IReadOnlyList<string> responses, List<string> bodies)
    {
        foreach (var content in responses)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[8192];
            int headerEnd;
            while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) break;
                buffer.AddRange(chunk[..read]);
            }
            var headers = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd);
            var length = int.Parse(headers.Split("\r\n").First(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
            while (buffer.Count - headerEnd - 4 < length)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) break;
                buffer.AddRange(chunk[..read]);
            }
            bodies.Add(Encoding.UTF8.GetString(buffer.ToArray(), headerEnd + 4, length));
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            var bytes = Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(bytes);
        }
    }

    private static string UserPrompt(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
    }

    private static int IndexOf(List<byte> data, byte[] pattern)
    {
        for (var i = 0; i <= data.Count - pattern.Length; i++)
            if (!pattern.Where((b, j) => data[i + j] != b).Any()) return i;
        return -1;
    }
}
