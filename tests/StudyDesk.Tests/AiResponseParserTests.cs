using System.Text.Json;
using System.Text.Json.Serialization;
using StudyDesk.Domain;
using StudyDesk.Infrastructure;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class AiResponseParserTests
{
    [Test]
    public void Should_ParseStructuredDiagnosticFixture_When_OptionsAreObjects()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "structured-diagnostic.json"));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var questions = AiResponseParser.Parse<List<ExamQuestion>>(raw, options);
        Assert.Multiple(() =>
        {
            Assert.That(questions, Has.Count.EqualTo(5));
            Assert.That(questions![0].Options[2], Is.EqualTo("Um nome dado a um valor"));
            Assert.That(questions[0].CorrectAnswer, Is.EqualTo("Um nome dado a um valor"));
            Assert.That(questions[0].Kind, Is.EqualTo(QuestionKind.MultipleChoice));
        });
    }

    [Test]
    public void Should_ParseLessonFixture_When_ObjectivesAndExamplesAreArrays()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "structured-lesson.json"));
        var lesson = AiResponseParser.Parse<Lesson>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Multiple(() =>
        {
            Assert.That(lesson!.Objectives, Does.Contain("variável"));
            Assert.That(lesson.Examples, Does.Contain("Exemplo 1"));
            Assert.That(lesson.Explanation, Does.Contain("Erros comuns"));
            Assert.That(lesson.Summary, Is.Not.Empty);
        });
    }
}
