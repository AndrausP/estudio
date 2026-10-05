using StudyDesk.Domain;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class StudyRulesTests
{
    private static StudyProject Draft()
    {
        var project = new StudyProject { Title = "C#", Goal = "Aprender C#", Diagnostic = new Diagnostic { CompletedAt = DateTime.UtcNow } };
        project.SetModules([new ModuleOutline(null, "Bases", "Sintaxe"), new ModuleOutline(null, "Objetos", "POO")]);
        return project;
    }

    [Test]
    public void Should_RequireDiagnostic_When_ConfirmingTrail()
    {
        var project = new StudyProject();
        Assert.Throws<InvalidOperationException>(() => project.ConfirmTrail());
    }

    [Test]
    public void Should_UnlockNextModule_When_ScoreIs70()
    {
        var project = Draft();
        project.ConfirmTrail();
        project.RegisterAttempt(project.Modules[0].Id, new ExamAttempt { Score = 70 });
        Assert.Multiple(() =>
        {
            Assert.That(project.Modules[0].Status, Is.EqualTo(ModuleStatus.Completed));
            Assert.That(project.Modules[1].Status, Is.EqualTo(ModuleStatus.Available));
            Assert.That(project.Modules[0].BestScore, Is.EqualTo(70));
        });
    }

    [Test]
    public void Should_KeepNextModuleLocked_When_ScoreIs69()
    {
        var project = Draft();
        project.ConfirmTrail();
        project.RegisterAttempt(project.Modules[0].Id, new ExamAttempt { Score = 69 });
        Assert.Multiple(() =>
        {
            Assert.That(project.Modules[0].Status, Is.EqualTo(ModuleStatus.InProgress));
            Assert.That(project.Modules[1].Status, Is.EqualTo(ModuleStatus.Locked));
        });
    }

    [Test]
    public void Should_KeepHistoryAndBestScore_When_RetakingExam()
    {
        var project = Draft();
        project.ConfirmTrail();
        project.RegisterAttempt(project.Modules[0].Id, new ExamAttempt { Score = 85 });
        project.RegisterAttempt(project.Modules[0].Id, new ExamAttempt { Score = 40 });
        Assert.Multiple(() =>
        {
            Assert.That(project.Modules[0].Attempts, Has.Count.EqualTo(2));
            Assert.That(project.Modules[0].BestScore, Is.EqualTo(85));
            Assert.That(project.Modules[0].Status, Is.EqualTo(ModuleStatus.Completed));
        });
    }

    [Test]
    public void Should_RejectEditingStartedModule_When_TrailConfirmed()
    {
        var project = Draft();
        project.ConfirmTrail();
        var first = project.Modules[0];
        first.Status = ModuleStatus.InProgress;
        Assert.Throws<InvalidOperationException>(() => project.SetModules([new ModuleOutline(first.Id, "Outro", first.Objective), new ModuleOutline(project.Modules[1].Id, "Objetos", "POO")]));
    }

    [Test]
    public void Should_AllowEditingUnstartedModule_When_TrailConfirmed()
    {
        var project = Draft();
        project.ConfirmTrail();
        var second = project.Modules[1];
        project.SetModules([new ModuleOutline(project.Modules[0].Id, "Bases", "Sintaxe"), new ModuleOutline(second.Id, "Classes", "POO moderna")]);
        Assert.That(project.Modules[1].Title, Is.EqualTo("Classes"));
    }

    [Test]
    public void Should_RejectDuplicateModuleIds_When_EditingTrail()
    {
        var project = Draft();
        var id = project.Modules[0].Id;
        Assert.Throws<ArgumentException>(() => project.SetModules([
            new ModuleOutline(id, "Bases", "Sintaxe"),
            new ModuleOutline(id, "Bases repetidas", "Sintaxe")
        ]));
        Assert.That(project.Modules, Has.Count.EqualTo(2));
    }

    [Test]
    public void Should_RejectUnknownAnswerIds_When_SubmittingAssessment()
    {
        var known = new ExamQuestion();
        var other = new ExamQuestion();
        var answers = new[]
        {
            new QuestionAnswer { QuestionId = known.Id, Answer = "A" },
            new QuestionAnswer { QuestionId = Guid.NewGuid(), Answer = "B" }
        };
        Assert.That(AssessmentRules.ValidAnswers([known, other], answers), Is.False);
    }

    [Test]
    public void Should_RejectExtraAnswerIds_When_SubmittingAssessment()
    {
        var known = new ExamQuestion();
        var answers = new[]
        {
            new QuestionAnswer { QuestionId = known.Id, Answer = "A" },
            new QuestionAnswer { QuestionId = Guid.NewGuid(), Answer = "B" }
        };
        Assert.That(AssessmentRules.ValidAnswers([known], answers), Is.False);
    }
}
