using StudyDesk.Domain;

namespace StudyDesk.Application;

public sealed record Result<T>(bool IsSuccess, T? Value, string? Error)
{
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(string error) => new(false, default, error);
}

public sealed record ProviderInput(string Name, ProviderKind Kind, string Endpoint, string Model, string? ApiKey);
public sealed record CreateProjectInput(string Title, string Goal, string ExplanationStyle, Guid ProviderId);
public sealed record UpdateProjectInput(string Title, string ExplanationStyle, Guid ProviderId);

public interface IAiProvider
{
    ProviderKind Kind { get; }
    /// <summary>Verifica configuração e acesso ao provedor.</summary>
    Task<Result<string>> TestAsync(ProviderConfiguration configuration, string? apiKey, CancellationToken ct = default);
    /// <summary>Gera texto estruturado em JSON.</summary>
    Task<Result<string>> GenerateJsonAsync(ProviderConfiguration configuration, string? apiKey, string prompt, CancellationToken ct = default);
}

public interface IStudyService
{
    /// <summary>Lista provedores configurados sem retornar segredos.</summary>
    Task<IReadOnlyList<ProviderConfiguration>> ListProvidersAsync(CancellationToken ct = default);
    /// <summary>Salva configuração de provedor e protege a chave.</summary>
    Task<Result<ProviderConfiguration>> SaveProviderAsync(ProviderInput input, Guid? id = null, CancellationToken ct = default);
    /// <summary>Exclui um provedor não utilizado.</summary>
    Task<Result<bool>> DeleteProviderAsync(Guid id, CancellationToken ct = default);
    /// <summary>Testa a conexão com um provedor.</summary>
    Task<Result<string>> TestProviderAsync(Guid id, CancellationToken ct = default);
    /// <summary>Lista projetos locais.</summary>
    Task<IReadOnlyList<StudyProject>> ListProjectsAsync(CancellationToken ct = default);
    /// <summary>Busca projeto e seu estado persistido.</summary>
    Task<Result<StudyProject>> GetProjectAsync(Guid id, CancellationToken ct = default);
    /// <summary>Cria projeto.</summary>
    Task<Result<StudyProject>> CreateProjectAsync(CreateProjectInput input, CancellationToken ct = default);
    /// <summary>Altera nome, estilo de explicação e conexão de IA sem tocar no progresso.</summary>
    Task<Result<StudyProject>> UpdateProjectAsync(Guid projectId, UpdateProjectInput input, CancellationToken ct = default);
    /// <summary>Exclui o projeto e suas sessões de foco.</summary>
    Task<Result<bool>> DeleteProjectAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>Gera diagnóstico obrigatório.</summary>
    Task<Result<Diagnostic>> StartDiagnosticAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>Avalia diagnóstico e habilita geração da trilha.</summary>
    Task<Result<Diagnostic>> CompleteDiagnosticAsync(Guid projectId, IReadOnlyList<QuestionAnswer> answers, CancellationToken ct = default);
    /// <summary>Gera rascunho da trilha.</summary>
    Task<Result<IReadOnlyList<StudyModule>>> GenerateTrailAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>Edita módulos conforme regras do domínio.</summary>
    Task<Result<IReadOnlyList<StudyModule>>> SetModulesAsync(Guid projectId, IReadOnlyList<ModuleOutline> modules, CancellationToken ct = default);
    /// <summary>Confirma trilha após diagnóstico.</summary>
    Task<Result<StudyProject>> ConfirmTrailAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>Gera ou reabre aula de módulo disponível.</summary>
    Task<Result<Lesson>> GetOrCreateLessonAsync(Guid projectId, Guid moduleId, CancellationToken ct = default);
    /// <summary>Salva ponto de retomada da aula.</summary>
    Task<Result<Lesson>> SaveLessonPositionAsync(Guid projectId, Guid moduleId, int position, CancellationToken ct = default);
    /// <summary>Grava conclusão da aula antes da geração opcional de revisão.</summary>
    Task<Result<Lesson>> CompleteLessonAsync(Guid projectId, Guid moduleId, CancellationToken ct = default);
    /// <summary>Gera ou reabre 4 a 8 cartões; falha não desfaz a conclusão da aula.</summary>
    Task<Result<IReadOnlyList<ReviewCard>>> GenerateReviewCardsAsync(Guid projectId, Guid moduleId, CancellationToken ct = default);
    /// <summary>Lista cartões do projeto; dueOnly retorna somente ativos vencidos.</summary>
    Task<Result<IReadOnlyList<ReviewCard>>> ListReviewCardsAsync(Guid projectId, bool dueOnly = false, DateOnly? todayLocal = null, CancellationToken ct = default);
    /// <summary>Edita conteúdo de cartão sem alterar origem ou histórico.</summary>
    Task<Result<ReviewCard>> EditReviewCardAsync(Guid projectId, Guid cardId, string question, string answer, CancellationToken ct = default);
    /// <summary>Arquiva cartão preservando seu histórico.</summary>
    Task<Result<ReviewCard>> ArchiveReviewCardAsync(Guid projectId, Guid cardId, CancellationToken ct = default);
    /// <summary>Registra avaliação e agenda próxima data civil local.</summary>
    Task<Result<ReviewCard>> ReviewCardAsync(Guid projectId, Guid cardId, ReviewGrade grade, DateOnly? todayLocal = null, CancellationToken ct = default);
    /// <summary>Gera ou reabre exercícios de prática.</summary>
    Task<Result<IReadOnlyList<PracticeExercise>>> GetOrCreatePracticeAsync(Guid projectId, Guid moduleId, CancellationToken ct = default);
    /// <summary>Acrescenta 2 exercícios mais desafiadores, sem repetir os existentes.</summary>
    Task<Result<IReadOnlyList<PracticeExercise>>> AddPracticeExercisesAsync(Guid projectId, Guid moduleId, CancellationToken ct = default);
    /// <summary>Registra resposta de prática e feedback.</summary>
    Task<Result<PracticeSubmission>> SubmitPracticeAsync(Guid projectId, Guid moduleId, Guid exerciseId, string answer, CancellationToken ct = default);
    /// <summary>Gera ou reabre prova imutável.</summary>
    Task<Result<Exam>> GetOrCreateExamAsync(Guid projectId, Guid moduleId, CancellationToken ct = default);
    /// <summary>Cria nova versão da prova depois de ao menos uma tentativa; tentativas anteriores ficam congeladas.</summary>
    Task<Result<Exam>> RegenerateExamAsync(Guid projectId, Guid moduleId, CancellationToken ct = default);
    /// <summary>Corrige prova e registra tentativa.</summary>
    Task<Result<ExamAttempt>> SubmitExamAsync(Guid projectId, Guid moduleId, IReadOnlyList<QuestionAnswer> answers, CancellationToken ct = default);
    /// <summary>Responde dúvida contextual do aluno; conversa não é persistida nesta versão.</summary>
    Task<Result<string>> AskTutorAsync(Guid projectId, Guid? moduleId, string question, CancellationToken ct = default);
    /// <summary>Responde dúvida considerando as últimas mensagens da conversa em memória.</summary>
    Task<Result<string>> AskTutorAsync(Guid projectId, Guid? moduleId, string question, IReadOnlyList<TutorTurn> history, CancellationToken ct = default);
    /// <summary>Retorna sessão de foco persistida.</summary>
    Task<Result<FocusSession>> GetFocusAsync(Guid projectId, Guid? moduleId = null, CancellationToken ct = default);
    /// <summary>Inicia ou retoma foco.</summary>
    Task<Result<FocusSession>> StartFocusAsync(Guid projectId, Guid? moduleId = null, CancellationToken ct = default);
    /// <summary>Pausa foco e grava segundos efetivos.</summary>
    Task<Result<FocusSession>> PauseFocusAsync(Guid projectId, Guid? moduleId = null, CancellationToken ct = default);
    /// <summary>Grava o tempo efetivo em andamento sem pausar o foco.</summary>
    Task<Result<FocusSession>> CheckpointFocusAsync(Guid projectId, Guid? moduleId = null, CancellationToken ct = default);
    /// <summary>Segundos de foco por dia local; sem projeto, soma todos os projetos.</summary>
    Task<IReadOnlyDictionary<DateOnly, int>> FocusSecondsByDayAsync(Guid? projectId = null, CancellationToken ct = default);
    /// <summary>Reinicia contador de foco.</summary>
    Task<Result<FocusSession>> ResetFocusAsync(Guid projectId, Guid? moduleId = null, CancellationToken ct = default);
}
