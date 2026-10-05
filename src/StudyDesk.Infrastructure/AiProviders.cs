using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StudyDesk.Application;
using StudyDesk.Domain;

namespace StudyDesk.Infrastructure;

internal static class AiProviderFactory
{
    public static IAiProvider Create(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAiCompatible => new OpenAiProvider(),
        ProviderKind.ClaudeCli => new ClaudeCliProvider(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal static class AiInstructions
{
    public const string System =
        "Você é o professor do aplicativo Estúdio. Escreva sempre em português do Brasil e responda somente com JSON válido no formato pedido, sem texto antes ou depois. " +
        "REGRA PRINCIPAL: use sempre os detalhes que o aluno forneceu. Isso inclui o material anexado (arquivos e imagens), os detalhes do projeto e as preferências de cada módulo " +
        "(se ele quer exemplos, o que enfatizar, o que evitar, nível de profundidade). Baseie-se nesse material antes de recorrer a conhecimento geral e diga quando algo não estiver nele. " +
        "Use a lousa (campo Boards, cenas com caixas, setas, textos e anotações) para ilustrar exemplos, fluxos, comparações e passos sempre que isso ajudar a entender ou quando o aluno pedir.";
}

internal static class AiHttp
{
    public static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    public static async Task<Result<IReadOnlyList<string>>> GetModelsAsync(Uri uri, string? token, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return Result<IReadOnlyList<string>>.Failure("Credencial inválida.");
            if (!response.IsSuccessStatusCode) return Result<IReadOnlyList<string>>.Failure($"O provedor não listou modelos (HTTP {(int)response.StatusCode}). Digite o nome do modelo.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var ids = new List<string>();
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray())
                    if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } text) ids.Add(text);
            return Result<IReadOnlyList<string>>.Success(ids.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Result<IReadOnlyList<string>>.Failure("Tempo esgotado ao buscar modelos."); }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return Result<IReadOnlyList<string>>.Failure("Não foi possível conectar ao provedor."); }
        catch (JsonException) { return Result<IReadOnlyList<string>>.Failure("O provedor retornou uma lista de modelos inválida."); }
    }

    public static async Task<Result<string>> PostAsync(Uri uri, object body, string? token, Func<JsonElement, string?> extract, CancellationToken ct, TimeSpan? requestTimeout = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(180));
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return Result<string>.Failure("Credencial inválida.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Result<string>.Failure("Limite de requisições do provedor atingido.");
            if (!response.IsSuccessStatusCode) return Result<string>.Failure($"Provedor indisponível (HTTP {(int)response.StatusCode}). Confira endpoint e modelo.");
            var raw = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(raw);
            return extract(document.RootElement) is { } content && !string.IsNullOrWhiteSpace(content)
                ? Result<string>.Success(content)
                : Result<string>.Failure("Resposta vazia ou inválida do provedor.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Result<string>.Failure("Tempo esgotado ao consultar a IA."); }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return Result<string>.Failure("Não foi possível conectar ao provedor."); }
        catch (JsonException) { return Result<string>.Failure("O provedor retornou JSON inválido."); }
    }
}

internal sealed class OpenAiProvider : IAiProvider
{
    private const long MaxImageBytes = 5L * 1024 * 1024;
    public ProviderKind Kind => ProviderKind.OpenAiCompatible;
    public Task<Result<string>> TestAsync(ProviderConfiguration configuration, string? apiKey, CancellationToken ct) => GenerateJsonAsync(configuration, apiKey, "Responda apenas {\"ok\":true}.", ct);

    public Task<Result<IReadOnlyList<string>>> ListModelsAsync(ProviderConfiguration configuration, string? apiKey, CancellationToken ct)
        => AiHttp.GetModelsAsync(new Uri(new Uri(configuration.Endpoint.TrimEnd('/') + "/"), "models"), apiKey, ct);

    public async Task<Result<string>> GenerateJsonAsync(ProviderConfiguration configuration, string? apiKey, string prompt, CancellationToken ct, IReadOnlyList<string>? imagePaths = null)
    {
        var endpoint = new Uri(new Uri(configuration.Endpoint.TrimEnd('/') + "/"), "chat/completions");
        Func<JsonElement, string?> extract = root => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        var images = LoadImages(imagePaths);
        if (images.Count > 0)
        {
            var parts = new List<object> { new { type = "text", text = prompt } };
            parts.AddRange(images.Select(url => (object)new { type = "image_url", image_url = new { url } }));
            var withImages = await AiHttp.PostAsync(endpoint, Body(configuration, parts), apiKey, extract, ct);
            if (withImages.IsSuccess) return withImages;
            // Modelo sem visão costuma responder 400: segue só com o texto dos anexos.
        }
        return await AiHttp.PostAsync(endpoint, Body(configuration, prompt), apiKey, extract, ct);
    }

    private static object Body(ProviderConfiguration configuration, object userContent) => new
    {
        model = configuration.Model,
        messages = new object[]
        {
            new { role = "system", content = AiInstructions.System },
            new { role = "user", content = userContent }
        },
        temperature = 0.3
    };

    private static List<string> LoadImages(IReadOnlyList<string>? paths)
    {
        var result = new List<string>();
        foreach (var path in (paths ?? []).Take(4))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxImageBytes) continue;
                result.Add($"data:{AttachmentReader.MimeType(path)};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
}

internal sealed class ClaudeCliProvider : IAiProvider
{
    public ProviderKind Kind => ProviderKind.ClaudeCli;
    public Task<Result<string>> TestAsync(ProviderConfiguration configuration, string? apiKey, CancellationToken ct) => GenerateJsonAsync(configuration, apiKey, "Responda apenas {\"ok\":true}.", ct);

    /// <summary>A CLI não lista modelos; oferece os apelidos e os identificadores atuais.</summary>
    public Task<Result<IReadOnlyList<string>>> ListModelsAsync(ProviderConfiguration configuration, string? apiKey, CancellationToken ct)
        => Task.FromResult(Result<IReadOnlyList<string>>.Success(KnownModels));

    public static readonly IReadOnlyList<string> KnownModels =
        ["opus", "sonnet", "haiku", "claude-opus-5-5", "claude-sonnet-5-5", "claude-fable-5-1", "claude-haiku-4-5-20251001"];

    public async Task<Result<string>> GenerateJsonAsync(ProviderConfiguration configuration, string? apiKey, string prompt, CancellationToken ct, IReadOnlyList<string>? imagePaths = null)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "studydesk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var imageNames = new List<string>();
            foreach (var path in (imagePaths ?? []).Take(4))
            {
                try
                {
                    if (new FileInfo(path) is not { Exists: true, Length: <= 5 * 1024 * 1024 }) continue;
                    var name = $"imagem{imageNames.Count + 1}{Path.GetExtension(path).ToLowerInvariant()}";
                    File.Copy(path, Path.Combine(temporary, name));
                    imageNames.Add(name);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            // O prompt inteiro segue por stdin (UTF-8): argumentos de linha de comando passam pelo cmd.exe e podem corromper acentos.
            prompt = AiInstructions.System + "\n\n" + prompt;
            if (imageNames.Count > 0)
                prompt += $"\n\nImagens enviadas pelo aluno (abra cada uma com a ferramenta Read antes de responder): {string.Join(", ", imageNames)}.";
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "claude",
                WorkingDirectory = temporary,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-p");
            process.StartInfo.ArgumentList.Add("--output-format");
            process.StartInfo.ArgumentList.Add("json");
            process.StartInfo.ArgumentList.Add("--tools");
            process.StartInfo.ArgumentList.Add(imageNames.Count > 0 ? "Read" : "");
            if (!string.IsNullOrWhiteSpace(configuration.Model)) { process.StartInfo.ArgumentList.Add("--model"); process.StartInfo.ArgumentList.Add(configuration.Model); }
            if (!TryStart(process)) return Result<string>.Failure("Claude Code CLI não está instalada ou não foi encontrada no PATH.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(300));
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token);
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;
                _ = await stderr; // erro da CLI pode conter prompt ou credencial; não o exibimos
                if (process.ExitCode != 0) return Result<string>.Failure("Claude CLI falhou. Confira instalação e login com `claude auth status`.");
                using var json = JsonDocument.Parse(output);
                return json.RootElement.TryGetProperty("result", out var result)
                    ? Result<string>.Success(result.GetString() ?? "")
                    : Result<string>.Failure("Resposta inválida da Claude CLI.");
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                if (ct.IsCancellationRequested) throw;
                return Result<string>.Failure("Tempo esgotado ao consultar Claude CLI.");
            }
        }
        catch (JsonException) { return Result<string>.Failure("Claude CLI retornou JSON inválido."); }
        finally { try { Directory.Delete(temporary, true); } catch (IOException) { } }
    }

    /// <summary>Tenta o executável nativo e, no Windows, o wrapper claude.cmd instalado pelo npm.</summary>
    private static bool TryStart(Process process)
    {
        foreach (var candidate in OperatingSystem.IsWindows() ? new[] { "claude", "claude.cmd" } : ["claude"])
        {
            process.StartInfo.FileName = candidate;
            try { return process.Start(); }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return false;
    }
}
