using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StudyDesk.Application;
using StudyDesk.Domain;
using StudyDesk.Infrastructure;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class MaterialAndBoardTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "studydesk-material-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private async Task<(IStudyService Service, StudyProject Project)> NewProjectAsync(string endpoint = "http://127.0.0.1:1/v1")
    {
        var service = StudyService.CreateDefault(_directory);
        var provider = await service.SaveProviderAsync(new ProviderInput("Mock", ProviderKind.OpenAiCompatible, endpoint, "mock", null));
        var project = (await service.CreateProjectAsync(new CreateProjectInput("C#", "Aprender", "Didático", provider.Value!.Id, "Prova em 2 meses"))).Value!;
        return (service, project);
    }

    [Test]
    public async Task Should_ExtractTextAndRemoveFile_When_AttachingMarkdown()
    {
        var (service, project) = await NewProjectAsync();
        var file = Path.Combine(_directory, "notas.md");
        await File.WriteAllTextAsync(file, "# Resumo\nClasses e herança em C#.");
        var added = await service.AddAttachmentAsync(project.Id, file);
        var reloaded = (await service.GetProjectAsync(project.Id)).Value!;
        var stored = Path.Combine(_directory, "attachments", project.Id.ToString("N"), added.Value!.StoredName);
        Assert.Multiple(() =>
        {
            Assert.That(added.IsSuccess, Is.True);
            Assert.That(reloaded.Attachments.Single().ExtractedText, Does.Contain("herança"));
            Assert.That(reloaded.Details, Is.EqualTo("Prova em 2 meses"));
            Assert.That(File.Exists(stored), Is.True);
        });
        var removed = await service.RemoveAttachmentAsync(project.Id, added.Value.Id);
        Assert.Multiple(() =>
        {
            Assert.That(removed.IsSuccess, Is.True);
            Assert.That(File.Exists(stored), Is.False);
            Assert.That((service.GetProjectAsync(project.Id).Result.Value!).Attachments, Is.Empty);
        });
    }

    [Test]
    public async Task Should_RejectAttachment_When_FormatIsNotSupported()
    {
        var (service, project) = await NewProjectAsync();
        var file = Path.Combine(_directory, "programa.exe");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var result = await service.AddAttachmentAsync(project.Id, file);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error, Does.Contain("Formato não suportado"));
        });
    }

    [Test]
    public async Task Should_SaveAndReadProfile_When_UserWritesAboutThemselves()
    {
        var service = StudyService.CreateDefault(_directory);
        Assert.That((await service.GetProfileAsync()).About, Is.Empty);
        var saved = await service.SaveProfileAsync("Sou júnior em C#", "Júnior em C#, quer chegar a pleno.");
        var reopened = await StudyService.CreateDefault(_directory).GetProfileAsync();
        Assert.Multiple(() =>
        {
            Assert.That(saved.IsSuccess, Is.True);
            Assert.That(reopened.About, Is.EqualTo("Sou júnior em C#"));
            Assert.That(reopened.Summary, Is.EqualTo("Júnior em C#, quer chegar a pleno."));
        });
    }

    [Test]
    public void Should_DropInvalidItemsAndClampCoordinates_When_SanitizingBoard()
    {
        var scene = new WhiteboardScene
        {
            Title = "Fluxo",
            Items =
            [
                new BoardItem { Type = "BOX", X = -5, Y = 150, W = 20, H = 10, Text = "A" },
                new BoardItem { Type = "script", X = 1, Y = 1 },
                new BoardItem { Type = "arrow", X = 10, Y = 10, X2 = 300, Y2 = double.NaN }
            ]
        }.Sanitized();
        Assert.Multiple(() =>
        {
            Assert.That(scene.Items, Has.Count.EqualTo(2));
            Assert.That(scene.Items[0].Type, Is.EqualTo("box"));
            Assert.That(scene.Items[0].X, Is.EqualTo(0));
            Assert.That(scene.Items[0].Y, Is.EqualTo(100));
            Assert.That(scene.Items[1].X2, Is.EqualTo(100));
            Assert.That(scene.Items[1].Y2, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Should_SendProfileMaterialAndNotes_AndKeepBoards_When_GeneratingLesson()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var requestBody = "";
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
            (_, requestBody) = await MockHttp.ReadRequestWithBodyAsync(reader);
            var lesson = JsonSerializer.Serialize(new
            {
                Objectives = "o", Explanation = "e", Examples = "x", Summary = "s",
                Boards = new[] { new { Title = "Fluxo", Caption = "c", Items = new object[] { new { Type = "box", X = 10, Y = 10, W = 30, H = 15, Text = "Início" }, new { Type = "evil", X = 1, Y = 1 } } } }
            });
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = lesson } } } }));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(bytes);
        });
        var (service, project) = await NewProjectAsync(endpoint);
        await service.SaveProfileAsync("texto", "Aluno noturno que aprende com casos reais");
        var file = Path.Combine(_directory, "apostila.txt");
        await File.WriteAllTextAsync(file, "CONTEUDO-DA-APOSTILA sobre polimorfismo");
        await service.AddAttachmentAsync(project.Id, file);
        var module = new StudyModule { Title = "POO", Objective = "Entender POO", Status = ModuleStatus.Available, UserNotes = "ANOTACAO-DO-ALUNO usar analogias de cozinha" };
        var stored = (await service.GetProjectAsync(project.Id)).Value!;
        stored.Modules = [module];
        stored.Stage = ProjectStage.Studying;
        await new LocalStore(_directory).SaveAsync(stored, CancellationToken.None);
        var result = await service.GetOrCreateLessonAsync(project.Id, module.Id);
        await server;
        listener.Stop();
        Assert.That(result.IsSuccess, Is.True, result.Error);
        Assert.Multiple(() =>
        {
            Assert.That(result.Value!.Boards, Has.Count.EqualTo(1));
            Assert.That(result.Value.Boards[0].Items, Has.Count.EqualTo(1));
            Assert.That(requestBody, Does.Contain("Aluno noturno que aprende com casos reais"));
            Assert.That(requestBody, Does.Contain("CONTEUDO-DA-APOSTILA"));
            Assert.That(requestBody, Does.Contain("ANOTACAO-DO-ALUNO"));
            Assert.That(requestBody, Does.Contain("Prova em 2 meses"));
            Assert.That(requestBody, Does.Contain("Boards"));
        });
    }
}
