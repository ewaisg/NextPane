using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using MimeKit;
using NexPane.Shell.Content;
using OpenMcdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace NexPane.Indexing.Content;

/// <summary><c>documents.extract_status</c> values (§9.1).</summary>
public enum ExtractStatus
{
    Ok = 0,
    Failed = 1,
    TooLarge = 2,
    Encrypted = 3,
    Unsupported = 4,
    Pending = 5,
}

/// <summary>Result of one extraction.</summary>
public sealed record ExtractionResult(ExtractStatus Status, string? Text, string Extractor, string? Error = null, bool NeedsOcr = false)
{
    public static ExtractionResult Unsupported(string extractor) => new(ExtractStatus.Unsupported, null, extractor);
}

/// <summary>A content extractor (§7.6); the first one that accepts a file wins.</summary>
public interface IContentExtractor
{
    /// <summary>Value stored in <c>documents.extractor</c>.</summary>
    string Name { get; }

    bool CanExtract(string extension);

    Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct);
}

/// <summary>Plain text with BOM detection, strict UTF-8 validation and ANSI code page fallback; binary files
/// (NUL in the first 8 KB) are rejected.</summary>
public sealed class PlainTextExtractor : IContentExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "text", "md", "markdown", "csv", "tsv", "log", "ini", "cfg", "conf", "json", "xml", "yaml", "yml", "toml", "html", "htm", "css",
        "cs", "c", "h", "cpp", "hpp", "java", "kt", "py", "js", "mjs", "ts", "tsx", "jsx", "go", "rs", "rb", "php", "swift", "ps1", "psm1", "sh",
        "bat", "cmd", "sql", "xaml", "csproj", "props", "targets", "sln", "lua", "r", "vb", "fs", "srt", "vtt", "tex", "rst", "properties",
    };

    static PlainTextExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Name => "text";

    public bool CanExtract(string extension) => Extensions.Contains(extension);

    public async Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var text = Decode(bytes);
        if (text is null)
        {
            return ExtractionResult.Unsupported(Name);
        }

        return new ExtractionResult(ExtractStatus.Ok, text.Length > maxChars ? text[..maxChars] : text, Name);
    }

    /// <summary>Decodes text bytes, or returns null for binary content.</summary>
    public static string? Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return ansi.GetString(bytes);
        }
    }
}

/// <summary>PDF text layer via PdfPig; pages with almost no text (scans) request OCR.</summary>
public sealed class PdfPigExtractor : IContentExtractor
{
    public const int MinimumAverageCharsPerPage = 32;

    public string Name => "pdfpig";

    public bool CanExtract(string extension) => extension.Equals("pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var document = PdfDocument.Open(path);
            var text = new StringBuilder();
            var pages = 0;
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                pages++;
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(string.Join(' ', page.GetWords().Select(w => w.Text)));
                if (text.Length >= maxChars)
                {
                    break;
                }
            }

            var result = text.Length > maxChars ? text.ToString(0, maxChars) : text.ToString();
            var needsOcr = pages > 0 && result.Length / (double)pages < MinimumAverageCharsPerPage;
            return new ExtractionResult(ExtractStatus.Ok, result, Name, NeedsOcr: needsOcr);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            return new ExtractionResult(ExtractStatus.Encrypted, null, Name, ex.Message);
        }
    }, ct);
}

/// <summary>.docx / .xlsx / .pptx via the Open XML SDK.</summary>
public sealed class OpenXmlExtractor : IContentExtractor
{
    public string Name => "openxml";

    public bool CanExtract(string extension) => extension.ToLowerInvariant() is "docx" or "docm" or "xlsx" or "xlsm" or "pptx" or "pptm";

    public Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct) => Task.Run(() =>
    {
        var text = new StringBuilder();
        switch (Path.GetExtension(path).TrimStart('.').ToLowerInvariant())
        {
            case "docx" or "docm":
            {
                using var doc = WordprocessingDocument.Open(path, false);
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body is not null)
                {
                    foreach (var paragraph in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
                    {
                        text.Append(paragraph.InnerText).Append('\n');
                        if (text.Length >= maxChars)
                        {
                            break;
                        }
                    }
                }

                break;
            }

            case "xlsx" or "xlsm":
            {
                using var doc = SpreadsheetDocument.Open(path, false);
                var workbook = doc.WorkbookPart;
                var shared = workbook?.SharedStringTablePart?.SharedStringTable?.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>()
                    .Select(i => i.InnerText).ToArray() ?? [];
                foreach (var sheet in workbook?.WorksheetParts ?? [])
                {
                    foreach (var cell in sheet.Worksheet?.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>() ?? [])
                    {
                        ct.ThrowIfCancellationRequested();
                        var value = cell.CellValue?.Text ?? cell.InlineString?.InnerText;
                        if (value is null)
                        {
                            continue;
                        }

                        if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString &&
                            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Length)
                        {
                            value = shared[index];
                        }

                        text.Append(value).Append('\t');
                        if (text.Length >= maxChars)
                        {
                            break;
                        }
                    }

                    text.Append('\n');
                }

                break;
            }

            default:
            {
                using var doc = PresentationDocument.Open(path, false);
                foreach (var slide in doc.PresentationPart?.SlideParts ?? [])
                {
                    foreach (var run in slide.Slide?.Descendants<DocumentFormat.OpenXml.Drawing.Text>() ?? [])
                    {
                        text.Append(run.Text).Append(' ');
                    }

                    text.Append('\n');
                    if (text.Length >= maxChars)
                    {
                        break;
                    }
                }

                break;
            }
        }

        var result = text.ToString().Trim();
        return new ExtractionResult(ExtractStatus.Ok, result.Length > maxChars ? result[..maxChars] : result, Name);
    }, ct);
}

/// <summary>.eml via MimeKit: headers plus text body (HTML stripped when no text part exists).</summary>
public sealed partial class EmlExtractor : IContentExtractor
{
    public string Name => "mimekit";

    public bool CanExtract(string extension) => extension.Equals("eml", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct)
    {
        var message = await MimeMessage.LoadAsync(path, ct).ConfigureAwait(false);
        var body = message.TextBody ?? StripHtml(message.HtmlBody ?? string.Empty);
        var text = $"{message.Subject}\n{message.From}\n{message.To}\n{body}".Trim();
        return new ExtractionResult(ExtractStatus.Ok, text.Length > maxChars ? text[..maxChars] : text, Name);
    }

    internal static string StripHtml(string html) => WebUtilityDecode(Tags().Replace(html, " "));

    private static string WebUtilityDecode(string text) => System.Net.WebUtility.HtmlDecode(text);

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();
}

/// <summary>Outlook .msg: reads the MAPI property streams of the compound file (subject, sender, recipients, body)
/// with OpenMcdf (§21: replaces MsgReader, whose current versions pull Microsoft.Maui.Graphics and Win2D).</summary>
public sealed class MsgExtractor : IContentExtractor
{
    private static readonly string[] Properties =
    [
        "0037", // PR_SUBJECT
        "0C1A", // PR_SENDER_NAME
        "0E04", // PR_DISPLAY_TO
        "0E03", // PR_DISPLAY_CC
        "1000", // PR_BODY
    ];

    public string Name => "msgreader";

    public bool CanExtract(string extension) => extension.Equals("msg", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct) => Task.Run(() =>
    {
        using var root = RootStorage.OpenRead(path);
        var text = new StringBuilder();
        foreach (var property in Properties)
        {
            ct.ThrowIfCancellationRequested();
            var value = ReadString(root, property);
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.Append(value.TrimEnd('\0')).Append('\n');
            }
        }

        var result = text.ToString().Trim();
        return new ExtractionResult(ExtractStatus.Ok, result.Length > maxChars ? result[..maxChars] : result, Name);
    }, ct);

    private static string? ReadString(RootStorage root, string propertyId)
    {
        foreach (var (type, encoding) in new[] { ("001F", Encoding.Unicode), ("001E", Encoding.Latin1) })
        {
            var name = "__substg1.0_" + propertyId + type;
            if (!root.TryOpenStream(name, out var stream))
            {
                continue;
            }

            using (stream)
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return encoding.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            }
        }

        return null;
    }
}

/// <summary>Any other type with a registered Windows IFilter (e.g. .rtf, .doc, .odt).</summary>
public sealed class IFilterExtractor(IFilterTextReader reader) : IContentExtractor
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "sys", "zip", "7z", "rar", "iso", "msi", "jpg", "jpeg", "png", "gif", "bmp", "mp3", "mp4", "mkv", "avi", "wav", "flac",
    };

    public string Name => "ifilter";

    public bool CanExtract(string extension) => extension.Length > 0 && !Excluded.Contains(extension);

    public async Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct)
    {
        var text = await reader.ReadAsync(path, maxChars, ct).ConfigureAwait(false);
        return text is null ? ExtractionResult.Unsupported(Name) : new ExtractionResult(ExtractStatus.Ok, text, Name);
    }
}
