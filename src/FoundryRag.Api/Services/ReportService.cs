using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FoundryRag.Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FoundryRag.Api.Services;

/// <summary>
/// LLM'in ürettiği Markdown/JSON içerikten Word (OpenXML), Excel (ClosedXML)
/// ve PDF (QuestPDF) dosyaları üretir; kayıtları SQLite'ta tutar.
/// </summary>
public sealed class ReportService
{
    private readonly VectorStore _db;

    public ReportService(VectorStore db) => _db = db;

    // ---------- Ortak giriş noktaları ----------

    public async Task<ReportInfo> CreateFromMarkdownAsync(string title, string markdown, string format, string instruction)
    {
        var fileName = BuildFileName(title, format);
        var path = Path.Combine(_db.ReportsDir, fileName);

        if (format == "pdf")
            CreatePdf(path, title, markdown);
        else
            CreateDocx(path, title, markdown);

        var id = await _db.InsertReportAsync(title, format, fileName, instruction);
        return new ReportInfo(id, title, format, fileName, instruction, DateTime.UtcNow);
    }

    public async Task<ReportInfo> CreateXlsxAsync(string title, List<ReportTable> tables, string instruction)
    {
        var fileName = BuildFileName(title, "xlsx");
        var path = Path.Combine(_db.ReportsDir, fileName);
        CreateXlsxFile(path, title, tables);
        var id = await _db.InsertReportAsync(title, "xlsx", fileName, instruction);
        return new ReportInfo(id, title, "xlsx", fileName, instruction, DateTime.UtcNow);
    }

    public string GetReportPath(ReportInfo report) => Path.Combine(_db.ReportsDir, report.FileName);

    public static string GetContentType(string format) => format switch
    {
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    // ---------- Markdown ayrıştırma (hafif) ----------

    private abstract record MdElement;
    private sealed record MdHeading(int Level, string Text) : MdElement;
    private sealed record MdParagraph(string Text) : MdElement;
    private sealed record MdBullet(string Text) : MdElement;
    private sealed record MdTable(List<string> Headers, List<List<string>> Rows) : MdElement;

    private static List<MdElement> ParseMarkdown(string markdown)
    {
        var elements = new List<MdElement>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) { i++; continue; }

            if (line.StartsWith('#'))
            {
                var level = line.TakeWhile(c => c == '#').Count();
                elements.Add(new MdHeading(Math.Min(level, 3), Clean(line.TrimStart('#').Trim())));
                i++;
            }
            else if (line.StartsWith("- ") || line.StartsWith("* ") || Regex.IsMatch(line, @"^\d+[.)]\s"))
            {
                var text = Regex.Replace(line, @"^(-|\*|\d+[.)])\s+", "");
                elements.Add(new MdBullet(Clean(text)));
                i++;
            }
            else if (line.StartsWith('|') && i + 1 < lines.Length && lines[i + 1].Trim().StartsWith('|'))
            {
                // Markdown tablosu: | a | b | + ayraç satırı + veri satırları
                var headers = SplitTableRow(line);
                i++; // ayraç satırını (|---|---|) atla
                if (i < lines.Length && Regex.IsMatch(lines[i].Trim(), @"^\|[\s:\-|]+\|?$")) i++;

                var rows = new List<List<string>>();
                while (i < lines.Length && lines[i].Trim().StartsWith('|'))
                {
                    rows.Add(SplitTableRow(lines[i].Trim()));
                    i++;
                }
                elements.Add(new MdTable(headers, rows));
            }
            else
            {
                elements.Add(new MdParagraph(Clean(line)));
                i++;
            }
        }
        return elements;
    }

    private static List<string> SplitTableRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => Clean(c.Trim())).ToList();

    /// <summary>Satır içi Markdown işaretlerini temizle (kalın/italik/kod).</summary>
    private static string Clean(string text) =>
        Regex.Replace(text, @"(\*\*|__|\*|`)", "");

    // ---------- Word (docx) ----------

    private static void CreateDocx(string path, string title, string markdown)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());
        var body = main.Document.Body!;

        // Başlık
        body.AppendChild(MakeParagraph(title, bold: true, halfPointSize: 40, color: "1F3864", spacingAfter: 240));
        body.AppendChild(MakeParagraph($"Oluşturulma: {DateTime.Now:dd.MM.yyyy HH:mm} — Foundry Local RAG Agent",
            bold: false, halfPointSize: 18, color: "808080", spacingAfter: 360));

        foreach (var element in ParseMarkdown(markdown))
        {
            switch (element)
            {
                case MdHeading h when h.Level == 1 && h.Text == title:
                    break; // Başlığı zaten yazdık
                case MdHeading h:
                    var size = h.Level switch { 1 => 34, 2 => 28, _ => 24 };
                    body.AppendChild(MakeParagraph(h.Text, bold: true, halfPointSize: size,
                        color: "2E5395", spacingBefore: 240, spacingAfter: 120));
                    break;
                case MdBullet b:
                    body.AppendChild(MakeParagraph("• " + b.Text, bold: false, halfPointSize: 22,
                        color: null, spacingAfter: 60, indent: 360));
                    break;
                case MdTable t:
                    body.AppendChild(MakeDocxTable(t));
                    body.AppendChild(MakeParagraph("", false, 22, null, spacingAfter: 120));
                    break;
                case MdParagraph p:
                    body.AppendChild(MakeParagraph(p.Text, bold: false, halfPointSize: 22,
                        color: null, spacingAfter: 120));
                    break;
            }
        }
        main.Document.Save();
    }

    private static W.Paragraph MakeParagraph(
        string text, bool bold, int halfPointSize, string? color,
        int spacingBefore = 0, int spacingAfter = 0, int indent = 0)
    {
        var runProps = new W.RunProperties();
        if (bold) runProps.AppendChild(new W.Bold());
        runProps.AppendChild(new W.FontSize { Val = halfPointSize.ToString() });
        if (color is not null) runProps.AppendChild(new W.Color { Val = color });

        var paraProps = new W.ParagraphProperties();
        if (spacingBefore > 0 || spacingAfter > 0)
            paraProps.AppendChild(new W.SpacingBetweenLines
            {
                Before = spacingBefore.ToString(),
                After = spacingAfter.ToString()
            });
        if (indent > 0)
            paraProps.AppendChild(new W.Indentation { Left = indent.ToString() });

        var run = new W.Run(runProps, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new W.Paragraph(paraProps, run);
    }

    private static W.Table MakeDocxTable(MdTable table)
    {
        var docxTable = new W.Table();

        var borders = new W.TableBorders(
            new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
            new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
            new W.LeftBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
            new W.RightBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
            new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
            new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" });
        docxTable.AppendChild(new W.TableProperties(
            borders,
            new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct }));

        if (table.Headers.Count > 0)
        {
            var headerRow = new W.TableRow();
            foreach (var header in table.Headers)
            {
                headerRow.AppendChild(new W.TableCell(
                    new W.TableCellProperties(new W.Shading
                    {
                        Val = W.ShadingPatternValues.Clear,
                        Fill = "DEEBF7"
                    }),
                    MakeParagraph(header, bold: true, halfPointSize: 21, color: "1F3864")));
            }
            docxTable.AppendChild(headerRow);
        }

        foreach (var row in table.Rows)
        {
            var docxRow = new W.TableRow();
            var cellCount = Math.Max(row.Count, table.Headers.Count);
            for (var c = 0; c < cellCount; c++)
            {
                var value = c < row.Count ? row[c] : "";
                docxRow.AppendChild(new W.TableCell(
                    MakeParagraph(value, bold: false, halfPointSize: 21, color: null)));
            }
            docxTable.AppendChild(docxRow);
        }
        return docxTable;
    }

    // ---------- PDF (QuestPDF) ----------

    private static void CreatePdf(string path, string title, string markdown)
    {
        var elements = ParseMarkdown(markdown);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10.5f).FontFamily("Segoe UI"));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).FontSize(19).Bold().FontColor("#1F3864");
                    col.Item().PaddingBottom(4).Text($"Oluşturulma: {DateTime.Now:dd.MM.yyyy HH:mm} — Foundry Local RAG Agent")
                        .FontSize(8.5f).FontColor("#808080");
                    col.Item().PaddingBottom(10).LineHorizontal(0.8f).LineColor("#2E5395");
                });

                page.Content().Column(col =>
                {
                    col.Spacing(6);
                    foreach (var element in elements)
                    {
                        switch (element)
                        {
                            case MdHeading h when h.Level == 1 && h.Text == title:
                                break;
                            case MdHeading h:
                                var size = h.Level switch { 1 => 16f, 2 => 13.5f, _ => 11.5f };
                                col.Item().PaddingTop(8).Text(h.Text).FontSize(size).Bold().FontColor("#2E5395");
                                break;
                            case MdBullet b:
                                col.Item().Row(row =>
                                {
                                    row.ConstantItem(14).Text("•");
                                    row.RelativeItem().Text(b.Text);
                                });
                                break;
                            case MdTable t:
                                col.Item().Table(pdfTable =>
                                {
                                    var columnCount = Math.Max(t.Headers.Count,
                                        t.Rows.Count > 0 ? t.Rows.Max(r => r.Count) : 1);
                                    pdfTable.ColumnsDefinition(columns =>
                                    {
                                        for (var c = 0; c < columnCount; c++)
                                            columns.RelativeColumn();
                                    });

                                    if (t.Headers.Count > 0)
                                    {
                                        foreach (var header in PadRow(t.Headers, columnCount))
                                        {
                                            pdfTable.Cell().Background("#DEEBF7").Border(0.5f).BorderColor("#BFBFBF")
                                                .Padding(4).Text(header).Bold().FontSize(9.5f);
                                        }
                                    }
                                    foreach (var row in t.Rows)
                                    {
                                        foreach (var cell in PadRow(row, columnCount))
                                        {
                                            pdfTable.Cell().Border(0.5f).BorderColor("#BFBFBF")
                                                .Padding(4).Text(cell).FontSize(9.5f);
                                        }
                                    }
                                });
                                break;
                            case MdParagraph p:
                                col.Item().Text(p.Text);
                                break;
                        }
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8.5f).FontColor("#808080"));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        }).GeneratePdf(path);
    }

    private static IEnumerable<string> PadRow(List<string> row, int count)
    {
        for (var i = 0; i < count; i++)
            yield return i < row.Count ? row[i] : "";
    }

    // ---------- Excel (xlsx) ----------

    private static void CreateXlsxFile(string path, string title, List<ReportTable> tables)
    {
        using var workbook = new XLWorkbook();
        var usedNames = new HashSet<string>();

        foreach (var table in tables)
        {
            var sheetName = SanitizeSheetName(table.Name, usedNames);
            var sheet = workbook.AddWorksheet(sheetName);

            var rowIndex = 1;
            sheet.Cell(rowIndex, 1).Value = title;
            sheet.Cell(rowIndex, 1).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(XLColor.FromHtml("#1F3864"));
            rowIndex += 2;

            var headerCount = Math.Max(table.Headers.Count, 1);
            if (table.Headers.Count > 0)
            {
                for (var c = 0; c < table.Headers.Count; c++)
                {
                    var cell = sheet.Cell(rowIndex, c + 1);
                    cell.Value = table.Headers[c];
                    cell.Style.Font.SetBold();
                    cell.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#DEEBF7"));
                    cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                }
                rowIndex++;
            }

            foreach (var row in table.Rows)
            {
                for (var c = 0; c < row.Count; c++)
                {
                    var cell = sheet.Cell(rowIndex, c + 1);
                    // Sayı gibi görünenleri sayı olarak yaz (Excel'de hesaplanabilir olsun)
                    if (double.TryParse(row[c], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var number)
                        && row[c].Length < 15)
                        cell.Value = number;
                    else
                        cell.Value = row[c];
                    cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Hair);
                }
                rowIndex++;
            }

            sheet.Columns(1, Math.Max(headerCount, 8)).AdjustToContents(5, 60);
        }

        if (workbook.Worksheets.Count == 0)
            workbook.AddWorksheet("Rapor").Cell(1, 1).Value = title;

        workbook.SaveAs(path);
    }

    private static string SanitizeSheetName(string name, HashSet<string> used)
    {
        var clean = Regex.Replace(name, @"[\[\]\*\?/\\:]", " ").Trim();
        if (clean.Length == 0) clean = "Sayfa";
        if (clean.Length > 28) clean = clean[..28];

        var candidate = clean;
        var counter = 2;
        while (!used.Add(candidate))
            candidate = $"{clean}{counter++}";
        return candidate;
    }

    // ---------- Dosya adı ----------

    private static string BuildFileName(string title, string format)
    {
        var safe = Regex.Replace(title, @"[^\w\sğüşıöçĞÜŞİÖÇ-]", "")
            .Trim()
            .Replace(' ', '-')
            .ToLowerInvariant();
        if (safe.Length > 50) safe = safe[..50].TrimEnd('-');
        if (safe.Length == 0) safe = "rapor";
        return $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.{format}";
    }
}
