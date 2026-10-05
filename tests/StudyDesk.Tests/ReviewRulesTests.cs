using System.Text.Json;
using StudyDesk.Domain;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class ReviewRulesTests
{
    private static readonly DateOnly Today = new(2026, 12, 31);
    private static readonly DateTimeOffset NowUtc = new(2026, 12, 31, 18, 30, 0, TimeSpan.Zero);

    private static ReviewCard NewCard() => new() { Question = "O que é uma função?", Answer = "Uma operação reutilizável.", DueDateLocal = Today };

    [TestCase(ReviewGrade.Wrong, 1, 0)]
    [TestCase(ReviewGrade.Correct, 3, 1)]
    [TestCase(ReviewGrade.Easy, 7, 1)]
    public void Should_ScheduleNewCard_FromLocalCivilDate(ReviewGrade grade, int interval, int streak)
    {
        var card = NewCard();
        Assert.That(card.IsDue(Today), Is.True);
        card.Review(grade, Today, NowUtc);
        Assert.Multiple(() =>
        {
            Assert.That(card.IntervalDays, Is.EqualTo(interval));
            Assert.That(card.DueDateLocal, Is.EqualTo(Today.AddDays(interval)));
            Assert.That(card.CorrectStreak, Is.EqualTo(streak));
            Assert.That(card.Attempts, Has.Count.EqualTo(1));
            Assert.That(card.Attempts[0].ReviewedAtUtc, Is.EqualTo(NowUtc));
            Assert.That(card.Attempts[0].PreviousIntervalDays, Is.Zero);
            Assert.That(card.Attempts[0].NewIntervalDays, Is.EqualTo(interval));
            Assert.That(card.Attempts[0].NextDueDateLocal, Is.EqualTo(card.DueDateLocal));
        });
    }

    [Test]
    public void Should_DoubleTripleAndCapIntervals_AtNinetyDays()
    {
        var card = NewCard();
        card.Review(ReviewGrade.Correct, Today, NowUtc);
        card.Review(ReviewGrade.Correct, Today.AddDays(3), NowUtc.AddDays(3));
        Assert.That(card.IntervalDays, Is.EqualTo(6));
        card.Review(ReviewGrade.Easy, Today.AddDays(9), NowUtc.AddDays(9));
        Assert.That(card.IntervalDays, Is.EqualTo(18));
        card.Review(ReviewGrade.Easy, Today.AddDays(27), NowUtc.AddDays(27));
        Assert.That(card.IntervalDays, Is.EqualTo(54));
        card.Review(ReviewGrade.Easy, Today.AddDays(81), NowUtc.AddDays(81));
        Assert.That(card.IntervalDays, Is.EqualTo(90));
        Assert.That(card.CorrectStreak, Is.EqualTo(5));
        card.Review(ReviewGrade.Wrong, Today.AddDays(171), NowUtc.AddDays(171));
        Assert.Multiple(() =>
        {
            Assert.That(card.IntervalDays, Is.EqualTo(1));
            Assert.That(card.CorrectStreak, Is.Zero);
            Assert.That(card.Attempts, Has.Count.EqualTo(6));
        });
    }

    [Test]
    public void Should_RejectInvalidReview_WithoutAddingHistory()
    {
        var card = NewCard();
        Assert.Throws<ArgumentOutOfRangeException>(() => card.Review((ReviewGrade)99, Today, NowUtc));
        Assert.Throws<InvalidOperationException>(() => card.Review(ReviewGrade.Correct, Today.AddDays(-1), NowUtc));
        Assert.That(card.Attempts, Is.Empty);
        Assert.That(card.IntervalDays, Is.Zero);
        card.IsArchived = true;
        Assert.Throws<InvalidOperationException>(() => card.Review(ReviewGrade.Correct, Today, NowUtc));
        Assert.That(card.Attempts, Is.Empty);
    }

    [Test]
    public void Should_LoadLegacyProject_WithoutReviewFields()
    {
        var legacy = new Dictionary<string, object?>
        {
            ["Title"] = "C#",
            ["Modules"] = new[] { new Dictionary<string, object?> { ["Title"] = "Bases", ["Lesson"] = new Dictionary<string, object?> { ["Summary"] = "Resumo" } } }
        };
        var loaded = JsonSerializer.Deserialize<StudyProject>(JsonSerializer.Serialize(legacy))!;
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Modules[0].ReviewCards, Is.Empty);
            Assert.That(loaded.Modules[0].ReviewCardsGenerationCompleted, Is.False);
            Assert.That(loaded.Modules[0].Lesson!.Summary, Is.EqualTo("Resumo"));
            Assert.That(loaded.Modules[0].Lesson!.CompletedAtUtc, Is.Null);
        });
    }
}
