using System.Text;

namespace StudyDesk.Tests;

internal static class MockHttp
{
    /// <summary>Lê cabeçalhos e consome o corpo (Content-Length) para o servidor falso não fechar a conexão com dados pendentes.</summary>
    public static async Task<List<string>> ReadRequestAsync(StreamReader reader)
    {
        var headers = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) headers.Add(line);
        var length = headers.Select(h => h.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
        var buffer = new char[Math.Max(0, length)];
        var read = 0;
        while (read < length)
        {
            var count = await reader.ReadAsync(buffer, read, length - read);
            if (count == 0) break;
            read += count;
        }
        return headers;
    }

    /// <summary>Como <see cref="ReadRequestAsync"/>, mas devolve o corpo; o leitor deve usar Latin1 (1 char por byte) e o corpo é decodificado como UTF-8.</summary>
    public static async Task<(List<string> Headers, string Body)> ReadRequestWithBodyAsync(StreamReader reader)
    {
        var headers = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) headers.Add(line);
        var length = headers.Select(h => h.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
        var buffer = new char[Math.Max(0, length)];
        var read = 0;
        while (read < length)
        {
            var count = await reader.ReadAsync(buffer, read, length - read);
            if (count == 0) break;
            read += count;
        }
        return (headers, Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(buffer, 0, read)));
    }
}
