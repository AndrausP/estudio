using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StudyDesk.Application;
using StudyDesk.Domain;
using StudyDesk.Infrastructure;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class StudyToolsTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "studydesk-tools-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public void Should_FindLessonAndCard_IgnoringAccentsAndCase()
    {
        var module = new StudyModule
        {
            Title = "Funções", Objective = "Reuso",
            Lesson = new Lesson { Explanation = "Uma **função** recebe parâmetros e devolve um valor.", Summary = "Resumo" }
        };
        module.ReviewCards.Add(new ReviewCard { Question = "O que é recursão?", Answer = "Função que chama a si mesma" });
        module.ReviewCards.Add(new ReviewCard { Question = "Arquivado recursao", Answer = "x", IsArchived = true });
        var project = new StudyProject { Modules = [module] };
        var byAccent = ProjectSearch.Search(project, "FUNCAO");
        var card = ProjectSearch.Search(project, "recursao");
        var multi = ProjectSearch.Search(project, "parametros valor");
        Assert.Multiple(() =>
        {
            Assert.That(byAccent.Select(h => h.Kind), Does.Contain(SearchHitKind.Lesson).And.Contain(SearchHitKind.Card));
            Assert.That(ProjectSearch.Search(project, "funcoes").First().Kind, Is.EqualTo(SearchHitKind.Module));
            Assert.That(card, Has.Count.EqualTo(1));
            Assert.That(card[0].Kind, Is.EqualTo(SearchHitKind.Card));
            Assert.That(multi.Single().Snippet, Does.Contain("parâmetros"));
            Assert.That(ProjectSearch.Search(project, "   "), Is.Empty);
        });
    }

    [Test]
    public void Should_RankWeakCardsAndQuestions()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var weak = new ReviewCard { Question = "Difícil", Answer = "a", DueDateLocal = today };
        weak.Review(ReviewGrade.Wrong, today, DateTimeOffset.UtcNow);
        var fine = new ReviewCard { Question = "Fácil", Answer = "b", DueDateLocal = today };
        fine.Review(ReviewGrade.Easy, today, DateTimeOffset.UtcNow);
        var question = new ExamQuestion { Prompt = "Explique", Weight = 40 };
        var module = new StudyModule { Title = "M1", ReviewCards = [weak, fine] };
        module.Attempts.Add(new ExamAttempt { FrozenQuestions = [question], Feedback = [new QuestionFeedback { QuestionId = question.Id, AwardedPoints = 10 }], Score = 10 });
        var project = new StudyProject { Modules = [module] };
        Assert.Multiple(() =>
        {
            Assert.That(StudyStats.WeakCards(project).Select(x => x.Card.Question), Is.EqualTo(new[] { "Difícil" }));
            Assert.That(StudyStats.WeakQuestions(project).Single().Awarded, Is.EqualTo(10));
            Assert.That(StudyStats.ActivityCounts(project)[today], Is.EqualTo(3));
            Assert.That(StudyStats.ReviewAccuracy(project, DateTimeOffset.UtcNow.AddDays(-1)), Is.EqualTo(50));
        });
    }

    [Test]
    public async Task Should_TrackFocusSecondsPerDay_AndCreateDailyBackup()
    {
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("Claude", ProviderKind.ClaudeCli, "", "", null));
        var project = (await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender", "Didático", provider.Value!.Id))).Value!;
        var focus = new FocusSession { ProjectId = project.Id };
        focus.AddSeconds(90, DateOnly.FromDateTime(DateTime.Now));
        focus.AddSeconds(30, DateOnly.FromDateTime(DateTime.Now).AddDays(-1));
        await new LocalStore(_directory).SaveAsync(focus, CancellationToken.None);
        var byDay = await StudyService.CreateDefault(_directory).FocusSecondsByDayAsync(project.Id);
        var all = await service.FocusSecondsByDayAsync();
        var backups = Directory.GetFiles(Path.Combine(_directory, "backups"), "study-*.db");
        Assert.Multiple(() =>
        {
            Assert.That(byDay[DateOnly.FromDateTime(DateTime.Now)], Is.EqualTo(90));
            Assert.That(byDay[DateOnly.FromDateTime(DateTime.Now).AddDays(-1)], Is.EqualTo(30));
            Assert.That(all.Values.Sum(), Is.EqualTo(120));
            Assert.That(focus.EffectiveSeconds, Is.EqualTo(120));
            Assert.That(backups, Has.Length.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_AddNewPracticeExercises_WithoutDuplicates()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await MockHttp.ReadRequestAsync(reader);
            var content = "[{\"Prompt\":\"Exercício antigo\",\"Hint\":\"h\"},{\"Prompt\":\"Exercício novo\",\"Hint\":\"h\"}]";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(bytes);
        });
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("Mock", ProviderKind.OpenAiCompatible, endpoint, "mock", null));
        var project = (await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender", "Didático", provider.Value!.Id))).Value!;
        var module = new StudyModule { Title = "M", Status = ModuleStatus.Available, Lesson = new Lesson { Objectives = "o", Summary = "s" }, Exercises = [new PracticeExercise { Prompt = "Exercício antigo" }] };
        project.Modules = [module];
        project.Stage = ProjectStage.Studying;
        await new LocalStore(_directory).SaveAsync(project, CancellationToken.None);
        var result = await service.AddPracticeExercisesAsync(project.Id, module.Id);
        await server;
        listener.Stop();
        Assert.That(result.Value!.Select(x => x.Prompt), Is.EqualTo(new[] { "Exercício antigo", "Exercício novo" }));
    }
}
