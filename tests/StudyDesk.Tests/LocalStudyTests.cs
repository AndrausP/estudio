using StudyDesk.Application;
using StudyDesk.Domain;
using StudyDesk.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class LocalStudyTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "studydesk-test-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public async Task Should_ReopenProject_When_ServiceRestarts()
    {
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("Claude CLI", ProviderKind.ClaudeCli, "", "", null));
        Assert.That(provider.IsSuccess, Is.True);
        var created = await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender C#", "Exemplos", provider.Value!.Id));
        Assert.That(created.IsSuccess, Is.True);
        var reopened = StudyService.CreateDefault(_directory);
        var project = await reopened.GetProjectAsync(created.Value!.Id);
        Assert.Multiple(() =>
        {
            Assert.That(project.IsSuccess, Is.True);
            Assert.That(project.Value!.Title, Is.EqualTo("C#"));
            Assert.That(project.Value.Stage, Is.EqualTo(ProjectStage.New));
        });
    }

    [Test]
    public async Task Should_RejectEmptyTutorQuestion_WithoutCallingProvider()
    {
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("Claude CLI", ProviderKind.ClaudeCli, "", "", null));
        var project = await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender C#", "Didático", provider.Value!.Id));
        var answer = await service.AskTutorAsync(project.Value!.Id, null, "   ");
        Assert.Multiple(() =>
        {
            Assert.That(answer.IsSuccess, Is.False);
            Assert.That(answer.Error, Does.Contain("dúvida"));
        });
    }

    [Test]
    public async Task Should_PersistFocusWithoutCountingPausedTime()
    {
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("Claude CLI", ProviderKind.ClaudeCli, "", "", null));
        var project = await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender", "Didático", provider.Value!.Id));
        await service.StartFocusAsync(project.Value!.Id);
        var paused = await service.PauseFocusAsync(project.Value.Id);
        var reopened = StudyService.CreateDefault(_directory);
        var focus = await reopened.GetFocusAsync(project.Value.Id);
        Assert.Multiple(() =>
        {
            Assert.That(paused.IsSuccess, Is.True);
            Assert.That(focus.Value!.IsRunning, Is.False);
            Assert.That(focus.Value.EffectiveSeconds, Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task Should_UpdateProjectMinutesAndRecoverCheckpoint_When_Reopening()
    {
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("Claude CLI", ProviderKind.ClaudeCli, "", "", null));
        var project = await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender", "Didático", provider.Value!.Id));
        await service.StartFocusAsync(project.Value!.Id);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "study.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE focus_sessions SET payload=json_set(payload, '$.StartedAt', $started) WHERE project_id=$project";
            command.Parameters.AddWithValue("$started", DateTime.UtcNow.AddSeconds(-65).ToString("O"));
            command.Parameters.AddWithValue("$project", project.Value.Id.ToString());
            await command.ExecuteNonQueryAsync();
        }
        var checkpoint = await service.CheckpointFocusAsync(project.Value.Id);
        Assert.That(checkpoint.Value!.EffectiveSeconds, Is.GreaterThanOrEqualTo(60));
        var reopened = StudyService.CreateDefault(_directory);
        var focus = await reopened.GetFocusAsync(project.Value.Id);
        var savedProject = await reopened.GetProjectAsync(project.Value.Id);
        Assert.Multiple(() =>
        {
            Assert.That(focus.Value!.IsRunning, Is.False);
            Assert.That(focus.Value.EffectiveSeconds, Is.GreaterThanOrEqualTo(60));
            Assert.That(savedProject.Value!.FocusMinutes, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    [Platform("Win")]
    public async Task Should_KeepApiKeyOutOfSqlite_When_SavingProvider()
    {
        var service = StudyService.CreateDefault(_directory);
        const string key = "secret-sentinel-123";
        var result = await service.SaveProviderAsync(new ProviderInput("API", ProviderKind.OpenAiCompatible, "https://example.com/v1", "gpt", key));
        Assert.That(result.IsSuccess, Is.True);
        var database = await File.ReadAllBytesAsync(Path.Combine(_directory, "study.db"));
        Assert.That(System.Text.Encoding.UTF8.GetString(database), Does.Not.Contain(key));
    }

    [TestCase(true)]
    [TestCase(false)]
    [Platform("Win")]
    public async Task Should_TestOpenAiCompatibleProvider_When_LocalMockResponds(bool withKey)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var headers = await MockHttp.ReadRequestAsync(reader);
            var body = "{\"choices\":[{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
            await stream.WriteAsync(bytes);
            return headers;
        });
        var service = StudyService.CreateDefault(_directory);
        var saved = await service.SaveProviderAsync(new ProviderInput("Mock API", ProviderKind.OpenAiCompatible, $"http://127.0.0.1:{port}/v1", "mock", withKey ? "test-token" : null));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await service.TestProviderAsync(saved.Value!.Id, timeout.Token);
        var requestHeaders = await server;
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(requestHeaders, Has.Some.Contains("POST /v1/chat/completions"));
            Assert.That(requestHeaders.Any(x => x.Contains("Authorization: Bearer test-token")), Is.EqualTo(withKey));
        });
    }
}
