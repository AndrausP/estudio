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
    public const string System = "Você é o professor do aplicativo Estúdio. Escreva sempre em português do Brasil e responda somente com JSON válido no formato pedido, sem texto antes ou depois.";
}

internal static class AiHttp
{
    public static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
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
    public ProviderKind Kind => ProviderKind.OpenAiCompatible;
    public Task<Result<string>> TestAsync(ProviderConfiguration configuration, string? apiKey, CancellationToken ct) => GenerateJsonAsync(configuration, apiKey, "Responda apenas {\"ok\":true}.", ct);
    public Task<Result<string>> GenerateJsonAsync(ProviderConfiguration configuration, string? apiKey, string prompt, CancellationToken ct)
    {
        var endpoint = new Uri(new Uri(configuration.Endpoint.TrimEnd('/') + "/"), "chat/completions");
        return AiHttp.PostAsync(endpoint, new
            {
                model = configuration.Model,
                messages = new[]
                {
                    new { role = "system", content = AiInstructions.System },
                    new { role = "user", content = prompt }
                },
                temperature = 0.3
            }, apiKey,
            root => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(), ct);
    }
}

internal sealed class ClaudeCliProvider : IAiProvider
{
    public ProviderKind Kind => ProviderKind.ClaudeCli;
    public Task<Result<string>> TestAsync(ProviderConfiguration configuration, string? apiKey, CancellationToken ct) => GenerateJsonAsync(configuration, apiKey, "Responda apenas {\"ok\":true}.", ct);

    public async Task<Result<string>> GenerateJsonAsync(ProviderConfiguration configuration, string? apiKey, string prompt, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "studydesk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
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
            process.StartInfo.ArgumentList.Add("");
            if (!string.IsNullOrWhiteSpace(configuration.Model)) { process.StartInfo.ArgumentList.Add("--model"); process.StartInfo.ArgumentList.Add(configuration.Model); }
            process.StartInfo.ArgumentList.Add("--append-system-prompt");
            process.StartInfo.ArgumentList.Add(AiInstructions.System);
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
