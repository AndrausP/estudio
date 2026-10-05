using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StudyDesk.Application;
using StudyDesk.Domain;
using StudyDesk.Infrastructure;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class TextRoundtripTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "studydesk-text-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public async Task Should_PreservePortugueseAccents_ThroughApiAndSqlite()
    {
        using var listener = StartListener(out var endpoint);
        var requests = new List<string>();
        var server = ServeAsync(listener, ["{\"answer\":\"A função calcula a média de ações e informações.\"}"], requests);
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("API", ProviderKind.OpenAiCompatible, endpoint, "mock", null));
        var created = await service.CreateProjectAsync(new CreateProjectInput("Introdução à programação", "Aprender função, ação e conexão", "Explicação com exemplos e revisão", provider.Value!.Id));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var answer = await service.AskTutorAsync(created.Value!.Id, null, "O que é uma função? Explique ação e conexão.", timeout.Token);
        await server;
        var reopened = StudyService.CreateDefault(_directory);
        var project = await reopened.GetProjectAsync(created.Value.Id);
        Assert.Multiple(() =>
        {
            Assert.That(answer.IsSuccess, Is.True);
            Assert.That(answer.Value, Is.EqualTo("A função calcula a média de ações e informações."));
            Assert.That(requests.Single(), Does.Contain("O que é uma função? Explique ação e conexão."));
            Assert.That(project.Value!.Title, Is.EqualTo("Introdução à programação"));
            Assert.That(project.Value.Goal, Is.EqualTo("Aprender função, ação e conexão"));
            Assert.That(project.Value.ExplanationStyle, Is.EqualTo("Explicação com exemplos e revisão"));
        });
    }

    [Test]
    public async Task Should_PreserveIndentedMultilineCode_InPracticeAndExam()
    {
        using var listener = StartListener(out var endpoint);
        var requests = new List<string>();
        var server = ServeAsync(listener,
            ["{\"feedback\":\"Indentação preservada.\",\"exampleSolution\":\"int ação = 1;\"}", "{\"fraction\":1.0,\"feedback\":\"Solução correta.\"}"], requests);
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("API", ProviderKind.OpenAiCompatible, endpoint, "mock", null));
        var created = await service.CreateProjectAsync(new CreateProjectInput("C#", "Praticar funções", "Com exemplos", provider.Value!.Id));
        var project = created.Value!;
        var exercise = new PracticeExercise { Prompt = "Escreva uma função." };
        var question = new ExamQuestion { Kind = QuestionKind.Open, Prompt = "Escreva código.", Rubric = "Código válido", Weight = 100 };
        var module = new StudyModule
        {
            Title = "Funções",
            Objective = "Escrever funções",
            Status = ModuleStatus.Available,
            Exercises = [exercise],
            Exam = new Exam { Questions = [question] }
        };
        project.Stage = ProjectStage.Studying;
        project.Modules = [module];
        await new LocalStore(_directory).SaveAsync(project, CancellationToken.None);
        const string code = "  public int Somar()\r\n  {\r\n      return 1 + 2;\r\n  }\r\n";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var practice = await service.SubmitPracticeAsync(project.Id, module.Id, exercise.Id, code, timeout.Token);
        var exam = await service.SubmitExamAsync(project.Id, module.Id, [new QuestionAnswer { QuestionId = question.Id, Answer = code }], timeout.Token);
        await server;
        var reopened = await StudyService.CreateDefault(_directory).GetProjectAsync(project.Id);
        var saved = reopened.Value!.Modules.Single();
        Assert.Multiple(() =>
        {
            Assert.That(practice.IsSuccess, Is.True);
            Assert.That(exam.IsSuccess, Is.True);
            Assert.That(exam.Value!.Score, Is.EqualTo(100));
            Assert.That(requests, Has.Count.EqualTo(2));
            Assert.That(requests[0], Does.Contain(code));
            Assert.That(requests[1], Does.Contain(code));
            Assert.That(saved.Exercises.Single().Submissions.Single().Answer, Is.EqualTo(code));
            Assert.That(saved.Attempts.Single().Answers.Single().Answer, Is.EqualTo(code));
        });
    }

    private static TcpListener StartListener(out string endpoint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        return listener;
    }

    private static async Task ServeAsync(TcpListener listener, IReadOnlyList<string> responses, List<string> requests)
    {
        foreach (var content in responses)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int headerEnd;
            int contentLength;
            while (true)
            {
                var count = await stream.ReadAsync(chunk);
                if (count == 0) throw new IOException("Requisição HTTP incompleta.");
                buffer.Write(chunk, 0, count);
                var bytes = buffer.ToArray();
                headerEnd = HeaderEnd(bytes);
                if (headerEnd < 0) continue;
                var headers = Encoding.ASCII.GetString(bytes, 0, headerEnd);
                var lengthLine = headers.Split("\r\n").Single(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                contentLength = int.Parse(lengthLine.Split(':')[1].Trim());
                if (bytes.Length >= headerEnd + 4 + contentLength) break;
            }
            var requestBytes = buffer.ToArray();
            var requestJson = Encoding.UTF8.GetString(requestBytes, headerEnd + 4, contentLength);
            using var request = JsonDocument.Parse(requestJson);
            requests.Add(request.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!);
            var responseBody = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            var responseBytes = Encoding.UTF8.GetBytes(responseBody);
            var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders);
            await stream.WriteAsync(responseBytes);
        }
    }

    private static int HeaderEnd(byte[] bytes)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
        return -1;
    }
}
