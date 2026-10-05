using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyDesk.Domain;

namespace StudyDesk.Infrastructure;

internal sealed class LocalStore
{
    private readonly string _connectionString;
    private readonly string _secretDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LocalStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _secretDirectory = Path.Combine(dataDirectory, "secrets");
        Directory.CreateDirectory(_secretDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "study.db"), Pooling = false }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 1) throw new InvalidOperationException("Banco de dados criado por versão mais recente do StudyDesk.");
        if (version == 0)
        {
            using var migration = connection.BeginTransaction();
            command.Transaction = migration;
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, title TEXT NOT NULL, stage INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS providers(id TEXT PRIMARY KEY, kind INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS focus_sessions(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, module_id TEXT, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_focus_project_module ON focus_sessions(project_id, module_id);
            PRAGMA user_version = 1;
            """;
            command.ExecuteNonQuery();
            migration.Commit();
            command.Transaction = null;
        }
        // Uma sessão interrompida pelo fechamento não deve acumular tempo fora do app.
        command.CommandText = "UPDATE focus_sessions SET payload=json_set(payload, '$.IsRunning', json('false'), '$.StartedAt', NULL) WHERE json_extract(payload, '$.IsRunning')=1";
        command.ExecuteNonQuery();
        BackupDirectory = Path.Combine(dataDirectory, "backups");
        try { DailyBackup(connection); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException) { /* backup é proteção extra; nunca impede abrir o app */ }
    }

    /// <summary>Pasta com as cópias diárias do banco.</summary>
    public string BackupDirectory { get; }

    /// <summary>Uma cópia consistente por dia (API de backup do SQLite), mantendo as 7 mais recentes.</summary>
    private void DailyBackup(SqliteConnection source)
    {
        Directory.CreateDirectory(BackupDirectory);
        var target = Path.Combine(BackupDirectory, $"study-{DateTime.Now:yyyy-MM-dd}.db");
        if (File.Exists(target)) return;
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }
        foreach (var old in Directory.GetFiles(BackupDirectory, "study-*.db").OrderByDescending(x => x).Skip(7))
            File.Delete(old);
    }

    /// <summary>Lista projetos locais.</summary>
    public Task<IReadOnlyList<StudyProject>> ProjectsAsync(CancellationToken ct) => ListAsync<StudyProject>("projects", ct);
    /// <summary>Lista provedores locais.</summary>
    public Task<IReadOnlyList<ProviderConfiguration>> ProvidersAsync(CancellationToken ct) => ListAsync<ProviderConfiguration>("providers", ct);
    /// <summary>Lista sessões de foco locais.</summary>
    public Task<IReadOnlyList<FocusSession>> FocusAsync(CancellationToken ct) => ListAsync<FocusSession>("focus_sessions", ct);

    private async Task<IReadOnlyList<T>> ListAsync<T>(string table, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table}";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<T>();
        while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions)!);
        return result;
    }

    /// <summary>Persiste projeto atomicamente.</summary>
    public Task SaveAsync(StudyProject project, CancellationToken ct) => SaveAsync("projects", project.Id, JsonSerializer.Serialize(project), ct, project.Title, (int)project.Stage);
    /// <summary>Persiste provedor sem token.</summary>
    public Task SaveAsync(ProviderConfiguration provider, CancellationToken ct) => SaveAsync("providers", provider.Id, JsonSerializer.Serialize(provider), ct, null, (int)provider.Kind);
    /// <summary>Persiste sessão de foco.</summary>
    public Task SaveAsync(FocusSession focus, CancellationToken ct) => SaveAsync("focus_sessions", focus.Id, JsonSerializer.Serialize(focus), ct, focus.ProjectId.ToString(), null, focus.ModuleId?.ToString());

    private async Task SaveAsync(string table, Guid id, string payload, CancellationToken ct, string? text, int? number, string? moduleId = null)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = table switch
        {
            "projects" => "INSERT INTO projects(id,title,stage,payload) VALUES($id,$text,$num,$payload) ON CONFLICT(id) DO UPDATE SET title=$text,stage=$num,payload=$payload",
            "providers" => "INSERT INTO providers(id,kind,payload) VALUES($id,$num,$payload) ON CONFLICT(id) DO UPDATE SET kind=$num,payload=$payload",
            _ => "INSERT INTO focus_sessions(id,project_id,module_id,payload) VALUES($id,$text,$module,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload"
        };
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        command.Parameters.AddWithValue("$num", (object?)number ?? DBNull.Value);
        command.Parameters.AddWithValue("$module", (object?)moduleId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Exclui provedor.</summary>
    public async Task DeleteProviderAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM providers WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
        var secret = Path.Combine(_secretDirectory, $"{id:N}.bin");
        if (File.Exists(secret)) File.Delete(secret);
    }

    /// <summary>Exclui projeto e sessões de foco associadas em uma transação.</summary>
    public async Task DeleteProjectAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM focus_sessions WHERE project_id=$id; DELETE FROM projects WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Protege chave com DPAPI do usuário Windows (fora do Windows, arquivo restrito ao usuário, usado em desenvolvimento e testes).</summary>
    public async Task SaveSecretAsync(Guid id, string secret, CancellationToken ct)
    {
        var path = Path.Combine(_secretDirectory, $"{id:N}.bin");
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(path, encrypted, ct);
            return;
        }
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(secret), ct);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Recupera chave protegida para uso interno.</summary>
    public async Task<string?> ReadSecretAsync(Guid id, CancellationToken ct)
    {
        var path = Path.Combine(_secretDirectory, $"{id:N}.bin");
        if (!File.Exists(path)) return null;
        var stored = await File.ReadAllBytesAsync(path, ct);
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8.GetString(stored);
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; } // chave criada por outro usuário/computador: pede nova chave em vez de travar o app
    }
}
