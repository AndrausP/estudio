namespace StudyDesk.Domain;

public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public enum ProviderKind { OpenAiCompatible = 1, ClaudeCli = 2 }
public enum ProjectStage { New, Diagnosing, TrailDraft, Studying }
public enum ModuleStatus { Locked, Available, InProgress, Completed }
public enum QuestionKind { MultipleChoice, Open }
public enum ReviewGrade { Wrong, Correct, Easy }

public sealed class ProviderConfiguration : BaseEntity
{
    public string Name { get; set; } = "";
    public ProviderKind Kind { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string? SecretReference { get; set; }
}

public sealed class StudyProject : BaseEntity
{
    public string Title { get; set; } = "";
    public string Goal { get; set; } = "";
    public string ExplanationStyle { get; set; } = "Didático, com exemplos e linguagem clara";
    public Guid? ProviderId { get; set; }
    public ProjectStage Stage { get; set; } = ProjectStage.New;
    public Diagnostic? Diagnostic { get; set; }
    public List<StudyModule> Modules { get; set; } = [];
    public int FocusMinutes { get; set; }
    /// <summary>Material enviado pelo aluno (PDF, Markdown, texto, imagens) usado como contexto da IA.</summary>
    public List<ProjectAttachment> Attachments { get; set; } = [];
    /// <summary>Detalhes livres do aluno sobre o que espera deste projeto (nível, prazo, foco, preferências).</summary>
    public string Details { get; set; } = "";

    /// <summary>Confirma a trilha somente após concluir o diagnóstico.</summary>
    public void ConfirmTrail()
    {
        if (Diagnostic?.CompletedAt is null) throw new InvalidOperationException("Conclua o diagnóstico antes de confirmar a trilha.");
        if (Modules.Count == 0) throw new InvalidOperationException("A trilha precisa conter módulos.");
        Stage = ProjectStage.Studying;
        Modules = Modules.OrderBy(x => x.Order).ToList();
        for (var i = 0; i < Modules.Count; i++)
        {
            Modules[i].Order = i;
            Modules[i].Status = i == 0 ? ModuleStatus.Available : ModuleStatus.Locked;
        }
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Altera módulos ainda não iniciados.</summary>
    public void SetModules(IReadOnlyList<ModuleOutline> outlines)
    {
        if (Diagnostic?.CompletedAt is null) throw new InvalidOperationException("Conclua o diagnóstico antes de editar a trilha.");
        if (outlines.Count == 0) throw new ArgumentException("Inclua pelo menos um módulo.", nameof(outlines));
        if (outlines.Any(x => string.IsNullOrWhiteSpace(x.Title))) throw new ArgumentException("Todo módulo precisa de título.", nameof(outlines));
        if (outlines.Where(x => x.Id.HasValue).GroupBy(x => x.Id).Any(x => x.Count() > 1))
            throw new ArgumentException("O mesmo módulo não pode aparecer duas vezes na trilha.", nameof(outlines));
        var existing = Modules.ToDictionary(x => x.Id);
        var next = new List<StudyModule>();
        foreach (var outline in outlines)
        {
            StudyModule module;
            if (outline.Id is { } id)
            {
                if (!existing.TryGetValue(id, out module!)) throw new InvalidOperationException("Módulo não pertence ao projeto.");
                if (Stage == ProjectStage.Studying && module.Status is ModuleStatus.InProgress or ModuleStatus.Completed &&
                    (module.Order != next.Count || module.Title != outline.Title || module.Objective != outline.Objective))
                    throw new InvalidOperationException("Módulo iniciado não pode ser alterado.");
            }
            else module = new StudyModule();
            module.Title = outline.Title.Trim();
            module.Objective = outline.Objective.Trim();
            module.UserNotes = (outline.Notes ?? "").Trim();
            module.WantsExamples = outline.WantsExamples;
            module.WantsWhiteboard = outline.WantsWhiteboard;
            module.Order = next.Count;
            next.Add(module);
        }
        if (Stage == ProjectStage.Studying && Modules.Any(x => x.Status is ModuleStatus.InProgress or ModuleStatus.Completed && next.All(y => y.Id != x.Id)))
            throw new InvalidOperationException("Módulo iniciado não pode ser removido.");
        Modules = next;
        if (Stage == ProjectStage.Studying) RecalculateAvailability();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Libera o módulo seguinte quando a nota mínima é atingida.</summary>
    public void RegisterAttempt(Guid moduleId, ExamAttempt attempt)
    {
        var module = Modules.SingleOrDefault(x => x.Id == moduleId) ?? throw new InvalidOperationException("Módulo não encontrado.");
        if (module.Status == ModuleStatus.Locked) throw new InvalidOperationException("Módulo bloqueado.");
        if (attempt.Score is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(attempt));
        module.Attempts.Add(attempt);
        if (attempt.Score >= 70) module.Status = ModuleStatus.Completed;
        else if (module.Status != ModuleStatus.Completed) module.Status = ModuleStatus.InProgress;
        RecalculateAvailability();
        UpdatedAt = DateTime.UtcNow;
    }

    private void RecalculateAvailability()
    {
        var ordered = Modules.OrderBy(x => x.Order).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Status is ModuleStatus.Completed or ModuleStatus.InProgress) continue;
            ordered[i].Status = i == 0 || ordered[i - 1].Status == ModuleStatus.Completed ? ModuleStatus.Available : ModuleStatus.Locked;
        }
    }
}

public sealed record ModuleOutline(Guid? Id, string Title, string Objective, string? Notes = "", bool WantsExamples = true, bool WantsWhiteboard = true);

public enum AttachmentKind { Text, Pdf, Image }

/// <summary>Arquivo anexado a um projeto; o texto extraído (limitado) alimenta a IA e as imagens vão como visão quando o provedor aceita.</summary>
public sealed class ProjectAttachment : BaseEntity
{
    public string FileName { get; set; } = "";
    public AttachmentKind Kind { get; set; }
    /// <summary>Caminho relativo à pasta de anexos do projeto.</summary>
    public string StoredName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ExtractedText { get; set; } = "";
}

public sealed class Diagnostic : BaseEntity
{
    public List<ExamQuestion> Questions { get; set; } = [];
    public List<QuestionAnswer> Answers { get; set; } = [];
    public string Summary { get; set; } = "";
    public DateTime? CompletedAt { get; set; }
}

public sealed class StudyModule : BaseEntity
{
    public int Order { get; set; }
    public string Title { get; set; } = "";
    public string Objective { get; set; } = "";
    /// <summary>O que o aluno quer neste módulo, além do objetivo (foco, nível de detalhe, tipos de exemplo, o que evitar).</summary>
    public string UserNotes { get; set; } = "";
    public bool WantsExamples { get; set; } = true;
    public bool WantsWhiteboard { get; set; } = true;
    public ModuleStatus Status { get; set; } = ModuleStatus.Locked;
    public Lesson? Lesson { get; set; }
    public List<PracticeExercise> Exercises { get; set; } = [];
    public Exam? Exam { get; set; }
    public List<ExamAttempt> Attempts { get; set; } = [];
    public List<ReviewCard> ReviewCards { get; set; } = [];
    public bool ReviewCardsGenerationCompleted { get; set; }
    public int BestScore => Attempts.Count == 0 ? 0 : Attempts.Max(x => x.Score);
}

public sealed class Lesson : BaseEntity
{
    public string Objectives { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Examples { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>Cenas da lousa que ilustram a aula (diagramas, fluxos, passo a passo).</summary>
    public List<WhiteboardScene> Boards { get; set; } = [];
    public int ResumePosition { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class ReviewCard : BaseEntity
{
    public Guid ModuleId { get; set; }
    public Guid SourceLessonId { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public bool IsArchived { get; set; }
    public int IntervalDays { get; set; }
    public DateOnly DueDateLocal { get; set; }
    public int CorrectStreak { get; set; }
    public List<ReviewAttempt> Attempts { get; set; } = [];

    /// <summary>Indica se um cartão ativo vence até a data civil local informada.</summary>
    public bool IsDue(DateOnly todayLocal) => !IsArchived && DueDateLocal <= todayLocal;

    /// <summary>Aplica agendamento determinístico e registra histórico UTC.</summary>
    public void Review(ReviewGrade grade, DateOnly todayLocal, DateTimeOffset reviewedAtUtc)
    {
        if (!Enum.IsDefined(grade)) throw new ArgumentOutOfRangeException(nameof(grade));
        if (IsArchived) throw new InvalidOperationException("Cartão arquivado não pode ser revisado.");
        if (!IsDue(todayLocal)) throw new InvalidOperationException("Cartão ainda não venceu.");
        if (reviewedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("O instante da revisão deve estar em UTC.", nameof(reviewedAtUtc));
        if (IntervalDays is < 0 or > 90) throw new InvalidOperationException("Intervalo atual do cartão é inválido.");
        var previous = IntervalDays;
        var next = grade switch
        {
            ReviewGrade.Wrong => 1,
            ReviewGrade.Correct => Math.Min(90, Math.Max(3, previous * 2)),
            ReviewGrade.Easy => Math.Min(90, Math.Max(7, previous * 3)),
            _ => throw new ArgumentOutOfRangeException(nameof(grade))
        };
        var nextDueDate = todayLocal.AddDays(next);
        var nextStreak = grade == ReviewGrade.Wrong ? 0 : checked(CorrectStreak + 1);
        IntervalDays = next;
        DueDateLocal = nextDueDate;
        CorrectStreak = nextStreak;
        Attempts.Add(new ReviewAttempt
        {
            ReviewedAtUtc = reviewedAtUtc,
            Grade = grade,
            PreviousIntervalDays = previous,
            NewIntervalDays = next,
            NextDueDateLocal = DueDateLocal
        });
        UpdatedAt = DateTime.UtcNow;
    }
}

public sealed class ReviewAttempt : BaseEntity
{
    public DateTimeOffset ReviewedAtUtc { get; set; }
    public ReviewGrade Grade { get; set; }
    public int PreviousIntervalDays { get; set; }
    public int NewIntervalDays { get; set; }
    public DateOnly NextDueDateLocal { get; set; }
}

public sealed class PracticeExercise : BaseEntity
{
    public string Prompt { get; set; } = "";
    public string Hint { get; set; } = "";
    public List<PracticeSubmission> Submissions { get; set; } = [];
}

public sealed class PracticeSubmission : BaseEntity
{
    public string Answer { get; set; } = "";
    public string Feedback { get; set; } = "";
    public string ExampleSolution { get; set; } = "";
}

public sealed class Exam : BaseEntity
{
    public List<ExamQuestion> Questions { get; set; } = [];
}

public sealed class ExamQuestion : BaseEntity
{
    public QuestionKind Kind { get; set; }
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public string CorrectAnswer { get; set; } = "";
    public string Rubric { get; set; } = "";
    public int Weight { get; set; }
}

public sealed class QuestionAnswer
{
    public Guid QuestionId { get; set; }
    public string Answer { get; set; } = "";
}

public static class AssessmentRules
{
    /// <summary>Exige exatamente uma resposta preenchida para cada questão conhecida.</summary>
    public static bool ValidAnswers(IReadOnlyList<ExamQuestion> questions, IReadOnlyList<QuestionAnswer> answers)
    {
        if (answers.Count != questions.Count) return false;
        var ids = questions.Select(x => x.Id).ToHashSet();
        return answers.All(x => ids.Contains(x.QuestionId) && !string.IsNullOrWhiteSpace(x.Answer))
            && answers.Select(x => x.QuestionId).Distinct().Count() == answers.Count;
    }
}

public sealed class QuestionFeedback
{
    public Guid QuestionId { get; set; }
    public int AwardedPoints { get; set; }
    public string Feedback { get; set; } = "";
}

public sealed class ExamAttempt : BaseEntity
{
    public Guid ExamId { get; set; }
    public List<ExamQuestion> FrozenQuestions { get; set; } = [];
    public List<QuestionAnswer> Answers { get; set; } = [];
    public List<QuestionFeedback> Feedback { get; set; } = [];
    public int Score { get; set; }
    public bool Passed => Score >= 70;
}

public sealed class FocusSession : BaseEntity
{
    public Guid ProjectId { get; set; }
    public Guid? ModuleId { get; set; }
    public int EffectiveSeconds { get; set; }
    public DateTime? StartedAt { get; set; }
    public bool IsRunning { get; set; }
    /// <summary>Segundos efetivos por data civil local (chave yyyy-MM-dd), para meta diária e mapa de atividade.</summary>
    public Dictionary<string, int> DailySeconds { get; set; } = [];

    /// <summary>Soma segundos ao total e ao dia local informado.</summary>
    public void AddSeconds(int seconds, DateOnly dayLocal)
    {
        if (seconds <= 0) return;
        EffectiveSeconds += seconds;
        var key = dayLocal.ToString("yyyy-MM-dd");
        DailySeconds[key] = DailySeconds.GetValueOrDefault(key) + seconds;
    }
}

public sealed record TutorTurn(bool FromUser, string Text);

public static class StudyStats
{
    /// <summary>Datas civis locais em que houve atividade registrada de estudo no projeto.</summary>
    public static IReadOnlySet<DateOnly> ActivityDays(StudyProject project)
    {
        var days = new HashSet<DateOnly>();
        void Add(DateTime utc) => days.Add(DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()));
        foreach (var module in project.Modules)
        {
            if (module.Lesson?.CompletedAtUtc is { } completed) days.Add(DateOnly.FromDateTime(completed.ToLocalTime().DateTime));
            foreach (var attempt in module.Attempts) Add(attempt.CreatedAt);
            foreach (var submission in module.Exercises.SelectMany(x => x.Submissions)) Add(submission.CreatedAt);
            foreach (var review in module.ReviewCards.SelectMany(x => x.Attempts)) days.Add(DateOnly.FromDateTime(review.ReviewedAtUtc.ToLocalTime().DateTime));
        }
        return days;
    }

    /// <summary>Dias consecutivos de estudo até hoje; ontem conta para não zerar antes de o aluno estudar no dia.</summary>
    public static int CurrentStreak(StudyProject project, DateOnly todayLocal)
    {
        var days = ActivityDays(project);
        var cursor = days.Contains(todayLocal) ? todayLocal : todayLocal.AddDays(-1);
        var streak = 0;
        while (days.Contains(cursor)) { streak++; cursor = cursor.AddDays(-1); }
        return streak;
    }

    /// <summary>Quantidade de eventos de estudo por dia (aulas, práticas, provas e revisões).</summary>
    public static IReadOnlyDictionary<DateOnly, int> ActivityCounts(StudyProject project)
    {
        var counts = new Dictionary<DateOnly, int>();
        void Add(DateOnly day) => counts[day] = counts.GetValueOrDefault(day) + 1;
        DateOnly Local(DateTime utc) => DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime());
        foreach (var module in project.Modules)
        {
            if (module.Lesson?.CompletedAtUtc is { } completed) Add(DateOnly.FromDateTime(completed.ToLocalTime().DateTime));
            foreach (var attempt in module.Attempts) Add(Local(attempt.CreatedAt));
            foreach (var submission in module.Exercises.SelectMany(x => x.Submissions)) Add(Local(submission.CreatedAt));
            foreach (var review in module.ReviewCards.SelectMany(x => x.Attempts)) Add(DateOnly.FromDateTime(review.ReviewedAtUtc.ToLocalTime().DateTime));
        }
        return counts;
    }

    /// <summary>Cartões ativos que o aluno mais errou, do mais frágil ao menos frágil.</summary>
    public static IReadOnlyList<(ReviewCard Card, int Wrong, int Total)> WeakCards(StudyProject project, int take = 5)
        => project.Modules.SelectMany(m => m.ReviewCards)
            .Where(c => !c.IsArchived)
            .Select(c => (Card: c, Wrong: c.Attempts.Count(a => a.Grade == ReviewGrade.Wrong), Total: c.Attempts.Count))
            .Where(x => x.Wrong > 0)
            .OrderByDescending(x => (double)x.Wrong / x.Total).ThenByDescending(x => x.Wrong)
            .Take(take).ToList();

    /// <summary>Questões da última tentativa de cada módulo em que o aluno perdeu pontos.</summary>
    public static IReadOnlyList<(StudyModule Module, ExamQuestion Question, int Awarded)> WeakQuestions(StudyProject project, int take = 5)
    {
        var result = new List<(StudyModule, ExamQuestion, int)>();
        foreach (var module in project.Modules.OrderBy(m => m.Order))
        {
            var last = module.Attempts.OrderBy(a => a.CreatedAt).LastOrDefault();
            if (last is null) continue;
            foreach (var feedback in last.Feedback)
            {
                var question = last.FrozenQuestions.FirstOrDefault(q => q.Id == feedback.QuestionId);
                if (question is not null && feedback.AwardedPoints < question.Weight) result.Add((module, question, feedback.AwardedPoints));
            }
        }
        return result.Take(take).ToList();
    }

    /// <summary>Percentual de acerto nas revisões dos últimos dias.</summary>
    public static int? ReviewAccuracy(StudyProject project, DateTimeOffset sinceUtc)
    {
        var attempts = project.Modules.SelectMany(m => m.ReviewCards).SelectMany(c => c.Attempts).Where(a => a.ReviewedAtUtc >= sinceUtc).ToList();
        if (attempts.Count == 0) return null;
        return (int)Math.Round(100.0 * attempts.Count(a => a.Grade != ReviewGrade.Wrong) / attempts.Count);
    }
}


/// <summary>Uma cena da lousa: itens posicionados em coordenadas de 0 a 100 (porcentagem da largura e da altura).</summary>
public sealed class WhiteboardScene
{
    public string Title { get; set; } = "";
    public string Caption { get; set; } = "";
    public List<BoardItem> Items { get; set; } = [];

    /// <summary>Remove itens inválidos e limita coordenadas e quantidade, pois o JSON vem da IA.</summary>
    public WhiteboardScene Sanitized()
    {
        var kinds = new HashSet<string> { "box", "circle", "arrow", "line", "text", "note" };
        var clean = new List<BoardItem>();
        foreach (var item in Items.Take(80))
        {
            var kind = (item.Type ?? "").Trim().ToLowerInvariant();
            if (!kinds.Contains(kind)) continue;
            clean.Add(new BoardItem
            {
                Type = kind,
                X = Clamp(item.X), Y = Clamp(item.Y), W = Clamp(item.W, 1), H = Clamp(item.H, 1),
                X2 = Clamp(item.X2), Y2 = Clamp(item.Y2),
                Text = (item.Text ?? "").Length > 200 ? item.Text![..200] : item.Text ?? "",
                Color = item.Color ?? ""
            });
        }
        return new WhiteboardScene { Title = Title ?? "", Caption = Caption ?? "", Items = clean };
    }

    private static double Clamp(double value, double min = 0) => double.IsNaN(value) ? min : Math.Min(100, Math.Max(min, value));
}

/// <summary>Elemento desenhável. box/circle/note usam X,Y,W,H; text usa X,Y; arrow/line vão de (X,Y) a (X2,Y2).</summary>
public sealed class BoardItem
{
    public string Type { get; set; } = "box";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 20;
    public double H { get; set; } = 10;
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public string Text { get; set; } = "";
    /// <summary>white, yellow, green, blue, red, orange ou purple.</summary>
    public string Color { get; set; } = "";
}

/// <summary>Perfil global do aluno: texto livre e o resumo (feito pela IA e editável) que a IA usa para entendê-lo em todos os projetos.</summary>
public sealed class UserProfile
{
    public string About { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
}
