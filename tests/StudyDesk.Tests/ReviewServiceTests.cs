using System.Net;
using System.Net.Sockets;
using System.Text;
using StudyDesk.Application;
using StudyDesk.Domain;
using StudyDesk.Infrastructure;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class ReviewServiceTests
{
    private string _directory = null!;
    private const string InvalidCards = "{\"cards\":[{\"Question\":\"Q1\",\"Answer\":\"A1\"},{\"Question\":\"Q2\",\"Answer\":\"A2\"},{\"Question\":\"Q3\",\"Answer\":\"A3\"}]}";
    private const string ValidCards = "{\"cards\":[{\"Question\":\"Q1\",\"Answer\":\"A1\"},{\"Question\":\"Q2\",\"Answer\":\"A2\"},{\"Question\":\"Q3\",\"Answer\":\"A3\"},{\"Question\":\"Q4\",\"Answer\":\"A4\"}]}";

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "studydesk-review-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public async Task Should_KeepLessonCompleted_When_GenerationFailsThenRetrySucceeds()
    {
        using var listener = StartListener(out var endpoint);
        var server = ServeAsync(listener, [InvalidCards, ValidCards]);
        var service = StudyService.CreateDefault(_directory);
        var (project, module) = await ReadyProjectAsync(service, endpoint);
        var completed = await service.CompleteLessonAsync(project.Id, module.Id);
        var failed = await service.GenerateReviewCardsAsync(project.Id, module.Id);
        var afterFailure = await service.GetProjectAsync(project.Id);
        var retried = await service.GenerateReviewCardsAsync(project.Id, module.Id);
        await server;
        var repeated = await service.GenerateReviewCardsAsync(project.Id, module.Id);
        var reopened = await StudyService.CreateDefault(_directory).GetProjectAsync(project.Id);
        Assert.Multiple(() =>
        {
            Assert.That(completed.IsSuccess, Is.True);
            Assert.That(completed.Value!.ResumePosition, Is.EqualTo(4));
            Assert.That(failed.IsSuccess, Is.False);
            Assert.That(afterFailure.Value!.Modules[0].Lesson!.CompletedAtUtc, Is.Not.Null);
            Assert.That(afterFailure.Value.Modules[0].ReviewCardsGenerationCompleted, Is.False);
            Assert.That(afterFailure.Value.Modules[0].ReviewCards, Is.Empty);
            Assert.That(retried.IsSuccess, Is.True);
            Assert.That(retried.Value, Has.Count.EqualTo(4));
            Assert.That(retried.Value![0].DueDateLocal, Is.Not.EqualTo(DateOnly.MinValue));
            Assert.That(retried.Value[0].IsDue(DateOnly.FromDateTime(DateTime.Now)), Is.True);
            Assert.That(repeated.Value, Has.Count.EqualTo(4));
            Assert.That(repeated.Value![0].Id, Is.EqualTo(retried.Value![0].Id));
            Assert.That(reopened.Value!.Modules[0].ReviewCardsGenerationCompleted, Is.True);
            Assert.That(reopened.Value.Modules[0].ReviewCards, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Should_KeepHistoryAfterEditAndArchive_AndIsolateProjects()
    {
        using var listener = StartListener(out var endpoint);
        var server = ServeAsync(listener, [ValidCards]);
        var service = StudyService.CreateDefault(_directory);
        var (project, module) = await ReadyProjectAsync(service, endpoint);
        await service.CompleteLessonAsync(project.Id, module.Id);
        var generated = await service.GenerateReviewCardsAsync(project.Id, module.Id);
        await server;
        var card = generated.Value![0];
        var reviewed = await service.ReviewCardAsync(project.Id, card.Id, ReviewGrade.Correct);
        var edited = await service.EditReviewCardAsync(project.Id, card.Id, "Pergunta com ç e ã?", "Resposta com revisão.");
        var archived = await service.ArchiveReviewCardAsync(project.Id, card.Id);
        var due = await service.ListReviewCardsAsync(project.Id, dueOnly: true);
        var all = await service.ListReviewCardsAsync(project.Id);
        var other = await service.CreateProjectAsync(new CreateProjectInput("Outro", "Outro objetivo", "Didático", project.ProviderId!.Value));
        var otherDue = await service.ListReviewCardsAsync(other.Value!.Id, dueOnly: true);
        var forbidden = await service.EditReviewCardAsync(other.Value.Id, card.Id, "Inválida", "Inválida");
        var reopened = await StudyService.CreateDefault(_directory).GetProjectAsync(project.Id);
        var saved = reopened.Value!.Modules[0].ReviewCards.Single(x => x.Id == card.Id);
        Assert.Multiple(() =>
        {
            Assert.That(reviewed.IsSuccess, Is.True);
            Assert.That(edited.IsSuccess, Is.True);
            Assert.That(archived.IsSuccess, Is.True);
            Assert.That(due.Value, Has.Count.EqualTo(3));
            Assert.That(all.Value, Has.Count.EqualTo(4));
            Assert.That(otherDue.Value, Is.Empty);
            Assert.That(forbidden.IsSuccess, Is.False);
            Assert.That(saved.IsArchived, Is.True);
            Assert.That(saved.Question, Is.EqualTo("Pergunta com ç e ã?"));
            Assert.That(saved.Answer, Is.EqualTo("Resposta com revisão."));
            Assert.That(saved.Attempts, Has.Count.EqualTo(1));
            Assert.That(saved.Attempts[0].ReviewedAtUtc.Offset, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public async Task Should_PreserveCardsAndFocus_When_CheckpointRacesGeneration()
    {
        using var listener = StartListener(out var endpoint);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeAsync(listener, [ValidCards], accepted, TimeSpan.FromMilliseconds(200));
        var service = StudyService.CreateDefault(_directory);
        var secondService = StudyService.CreateDefault(_directory);
        var (project, module) = await ReadyProjectAsync(service, endpoint);
        var store = new LocalStore(_directory);
        await service.CompleteLessonAsync(project.Id, module.Id);
        await service.StartFocusAsync(project.Id);
        var focus = (await store.FocusAsync(CancellationToken.None)).Single();
        focus.StartedAt = DateTime.UtcNow.AddMinutes(-2);
        await store.SaveAsync(focus, CancellationToken.None);
        var generation = service.GenerateReviewCardsAsync(project.Id, module.Id);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var checkpoint = secondService.CheckpointFocusAsync(project.Id);
        await Task.WhenAll(generation, checkpoint, server);
        var reopened = await StudyService.CreateDefault(_directory).GetProjectAsync(project.Id);
        Assert.Multiple(() =>
        {
            Assert.That(generation.Result.IsSuccess, Is.True);
            Assert.That(checkpoint.Result.IsSuccess, Is.True);
            Assert.That(reopened.Value!.Modules[0].ReviewCards, Has.Count.EqualTo(4));
            Assert.That(reopened.Value.FocusMinutes, Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public async Task Should_PreserveLessonCompletionAndReview_When_BothMutateProject()
    {
        var service = StudyService.CreateDefault(_directory);
        var secondService = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("Claude", ProviderKind.ClaudeCli, "", "", null));
        var created = await service.CreateProjectAsync(new CreateProjectInput("C#", "Revisar funções", "Didático", provider.Value!.Id));
        var project = created.Value!;
        var module = new StudyModule { Status = ModuleStatus.Available, Lesson = new Lesson { Summary = "Resumo" } };
        var card = new ReviewCard { ModuleId = module.Id, SourceLessonId = module.Lesson.Id, Question = "Q", Answer = "A", DueDateLocal = DateOnly.FromDateTime(DateTime.Now) };
        module.ReviewCards.Add(card);
        module.ReviewCardsGenerationCompleted = true;
        project.Modules = [module];
        project.Stage = ProjectStage.Studying;
        await new LocalStore(_directory).SaveAsync(project, CancellationToken.None);
        var completed = service.CompleteLessonAsync(project.Id, module.Id);
        var reviewed = secondService.ReviewCardAsync(project.Id, card.Id, ReviewGrade.Wrong);
        await Task.WhenAll(completed, reviewed);
        var reopened = await StudyService.CreateDefault(_directory).GetProjectAsync(project.Id);
        Assert.Multiple(() =>
        {
            Assert.That(completed.Result.IsSuccess, Is.True);
            Assert.That(reviewed.Result.IsSuccess, Is.True);
            Assert.That(reopened.Value!.Modules[0].Lesson!.CompletedAtUtc, Is.Not.Null);
            Assert.That(reopened.Value.Modules[0].Lesson!.ResumePosition, Is.EqualTo(4));
            Assert.That(reopened.Value.Modules[0].ReviewCards[0].Attempts, Has.Count.EqualTo(1));
        });
    }

    private async Task<(StudyProject, StudyModule)> ReadyProjectAsync(IStudyService service, string endpoint)
    {
        var provider = await service.SaveProviderAsync(new ProviderInput("Mock", ProviderKind.OpenAiCompatible, endpoint, "mock", null));
        var created = await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender funções", "Exemplos", provider.Value!.Id));
        var project = created.Value!;
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

    private static async Task ServeAsync(TcpListener listener, IReadOnlyList<string> responses, TaskCompletionSource? accepted = null, TimeSpan? delay = null)
    {
        foreach (var content in responses)
        {
            using var client = await listener.AcceptTcpClientAsync();
            accepted?.TrySetResult();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await MockHttp.ReadRequestAsync(reader);
            if (delay is { } wait) await Task.Delay(wait);
            var body = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            var bytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(bytes);
        }
    }
}
