using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using StudyDesk.Domain;
using UglyToad.PdfPig;

namespace StudyDesk.Infrastructure;

/// <summary>Valida e extrai texto dos arquivos que o aluno anexa a um projeto.</summary>
internal static class AttachmentReader
{
    public const long MaxBytes = 25L * 1024 * 1024;
    public const int MaxExtractedChars = 60_000;
    public const int MaxAttachments = 20;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".csv", ".json", ".xml", ".yml", ".yaml", ".html", ".htm", ".log", ".ini", ".sql",
        ".cs", ".js", ".ts", ".py", ".java", ".c", ".cpp", ".h", ".go", ".rs", ".rb", ".php", ".css", ".sh", ".ps1", ".tex", ".rtf"
    };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public static string SupportedDescription => "PDF, Word (.docx), Markdown, texto, código e imagens (PNG, JPG, GIF, WEBP)";

    public static bool TryClassify(string path, out AttachmentKind kind)
    {
        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) { kind = AttachmentKind.Image; return true; }
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) { kind = AttachmentKind.Pdf; return true; }
        if (TextExtensions.Contains(ext) || ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)) { kind = AttachmentKind.Text; return true; }
        kind = default;
        return false;
    }

    public static string MimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>Extrai o texto de um arquivo; imagens retornam vazio. Lança InvalidDataException se o arquivo não puder ser lido.</summary>
    public static string ExtractText(string path, AttachmentKind kind)
    {
        try
        {
            var text = kind switch
            {
                AttachmentKind.Image => "",
                AttachmentKind.Pdf => ReadPdf(path),
                _ => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) ? ReadDocx(path) : ReadText(path)
            };
            text = text.Replace("\0", "").Trim();
            return text.Length > MaxExtractedChars ? text[..MaxExtractedChars] : text;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("Não foi possível ler o arquivo. Ele pode estar protegido ou corrompido.", ex);
        }
    }

    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxExtractedChars + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private static string ReadPdf(string path)
    {
        var builder = new StringBuilder();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            builder.AppendLine(page.Text);
            if (builder.Length > MaxExtractedChars) break;
        }
        return builder.ToString();
    }

    private static string ReadDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("Documento Word inválido.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        xml = Regex.Replace(xml, @"</w:p>", "\n");
        xml = Regex.Replace(xml, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(xml);
    }
}
