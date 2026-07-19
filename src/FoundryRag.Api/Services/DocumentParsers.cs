using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using FoundryRag.Api.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FoundryRag.Api.Services;

/// <summary>
/// Desteklenen dosya türlerini (docx, pdf, xlsx, csv, txt, md) ortak
/// ParsedBlock listesine çevirir.
/// </summary>
public sealed class DocumentParserRegistry
{
    public static readonly string[] SupportedExtensions =
        [".docx", ".pdf", ".xlsx", ".csv", ".txt", ".md"];

    public bool IsSupported(string extension) =>
        SupportedExtensions.Contains(extension.ToLowerInvariant());

    public List<ParsedBlock> Parse(byte[] content, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".docx" => ParseDocx(content),
            ".pdf" => ParsePdf(content),
            ".xlsx" => ParseXlsx(content),
            ".csv" => ParseCsv(content),
            ".txt" or ".md" => ParseText(content),
            _ => throw new NotSupportedException($"Desteklenmeyen dosya türü: {ext}")
        };
    }

    // ---------- Word ----------

    private static List<ParsedBlock> ParseDocx(byte[] content)
    {
        var blocks = new List<ParsedBlock>();
        using var ms = new MemoryStream(content);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return blocks;

        string? section = null;
        foreach (var element in body.ChildElements)
        {
            if (element is W.Paragraph paragraph)
            {
                var text = paragraph.InnerText.Trim();
                if (text.Length == 0) continue;

                var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                // İngilizce Word'de "Heading1", Türkçe Word'de "Balk1" stil kimliği kullanılır
                var isHeading = styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                             || styleId.StartsWith("Balk", StringComparison.OrdinalIgnoreCase)
                             || styleId.Equals("Title", StringComparison.OrdinalIgnoreCase);
                if (isHeading)
                    section = text;

                blocks.Add(new ParsedBlock(text, section));
            }
            else if (element is W.Table table)
            {
                var sb = new StringBuilder();
                var rowCount = 0;
                foreach (var row in table.Elements<W.TableRow>())
                {
                    var cells = row.Elements<W.TableCell>()
                        .Select(c => c.InnerText.Trim())
                        .Where(t => t.Length > 0);
                    sb.AppendLine(string.Join(" | ", cells));
                    rowCount++;
                    if (rowCount % 20 == 0)
                    {
                        blocks.Add(new ParsedBlock(sb.ToString(), section));
                        sb.Clear();
                    }
                }
                if (sb.Length > 0)
                    blocks.Add(new ParsedBlock(sb.ToString(), section));
            }
        }
        return blocks;
    }

    // ---------- PDF ----------

    private static List<ParsedBlock> ParsePdf(byte[] content)
    {
        var blocks = new List<ParsedBlock>();
        using var pdf = PdfDocument.Open(content);
        foreach (var page in pdf.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var section = $"Sayfa {page.Number}";
            foreach (var para in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = para.Trim();
                if (trimmed.Length > 0)
                    blocks.Add(new ParsedBlock(trimmed, section));
            }
        }
        return blocks;
    }

    // ---------- Excel ----------

    private const int MaxRowsPerSheet = 2000;
    private const int RowsPerBlock = 12;

    private static List<ParsedBlock> ParseXlsx(byte[] content)
    {
        var blocks = new List<ParsedBlock>();
        using var ms = new MemoryStream(content);
        using var workbook = new XLWorkbook(ms);

        foreach (var sheet in workbook.Worksheets)
        {
            var range = sheet.RangeUsed();
            if (range is null) continue;

            var rows = range.RowsUsed().ToList();
            if (rows.Count == 0) continue;

            var headers = rows[0].Cells()
                .Select(c => c.GetFormattedString().Trim())
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Sayfa: {sheet.Name} — Sütunlar: {string.Join(", ", headers)}");

            var count = 0;
            foreach (var row in rows.Skip(1))
            {
                var cells = row.Cells().Select(c => c.GetFormattedString().Trim()).ToList();
                var pairs = new List<string>();
                for (var i = 0; i < cells.Count; i++)
                {
                    if (cells[i].Length == 0) continue;
                    var header = i < headers.Count && headers[i].Length > 0 ? headers[i] : $"Sütun{i + 1}";
                    pairs.Add($"{header}: {cells[i]}");
                }
                if (pairs.Count > 0)
                    sb.AppendLine(string.Join(" | ", pairs));

                count++;
                if (count % RowsPerBlock == 0)
                {
                    blocks.Add(new ParsedBlock(sb.ToString(), sheet.Name));
                    sb.Clear();
                }
                if (count >= MaxRowsPerSheet)
                {
                    sb.AppendLine($"... (sayfa {sheet.Name} çok uzun, ilk {MaxRowsPerSheet} satır alındı)");
                    break;
                }
            }
            if (sb.Length > 0)
                blocks.Add(new ParsedBlock(sb.ToString(), sheet.Name));
        }
        return blocks;
    }

    // ---------- CSV ----------

    private static List<ParsedBlock> ParseCsv(byte[] content)
    {
        var text = DecodeText(content);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();
        if (lines.Count == 0) return [];

        var separator = lines[0].Contains(';') && !lines[0].Contains(',') ? ';' : ',';
        var headers = lines[0].Split(separator).Select(h => h.Trim().Trim('"')).ToList();

        var blocks = new List<ParsedBlock>();
        var sb = new StringBuilder();
        sb.AppendLine($"Sütunlar: {string.Join(", ", headers)}");

        for (var i = 1; i < Math.Min(lines.Count, MaxRowsPerSheet); i++)
        {
            var cells = lines[i].Split(separator).Select(c => c.Trim().Trim('"')).ToList();
            var pairs = new List<string>();
            for (var j = 0; j < cells.Count; j++)
            {
                if (cells[j].Length == 0) continue;
                var header = j < headers.Count ? headers[j] : $"Sütun{j + 1}";
                pairs.Add($"{header}: {cells[j]}");
            }
            if (pairs.Count > 0)
                sb.AppendLine(string.Join(" | ", pairs));

            if (i % RowsPerBlock == 0)
            {
                blocks.Add(new ParsedBlock(sb.ToString(), null));
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            blocks.Add(new ParsedBlock(sb.ToString(), null));
        return blocks;
    }

    // ---------- Düz metin / Markdown ----------

    private static List<ParsedBlock> ParseText(byte[] content)
    {
        var text = DecodeText(content);
        var blocks = new List<ParsedBlock>();
        string? section = null;

        foreach (var rawPara in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var para = rawPara.Trim();
            if (para.Length == 0) continue;

            // Markdown başlıklarını bölüm olarak izle
            var firstLine = para.Split('\n')[0].Trim();
            if (firstLine.StartsWith('#'))
                section = firstLine.TrimStart('#', ' ');

            blocks.Add(new ParsedBlock(para, section));
        }
        return blocks;
    }

    private static string DecodeText(byte[] content)
    {
        // BOM varsa ona uy, yoksa UTF-8 dene
        using var ms = new MemoryStream(content);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
