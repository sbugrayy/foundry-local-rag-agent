using System.Text.Json;
using FoundryRag.Api.Models;

namespace FoundryRag.Api.Services;

/// <summary>
/// Agentic katman: kullanıcı mesajını küçük bir yönlendirme (router) istemiyle
/// sınıflandırır ve uygun aracı çağırır — belge arama (RAG), özetleme, rapor
/// üretme veya belge listeleme.
///
/// Not: Küçük yerel modellerde native function-calling desteği Foundry Local
/// tarafında henüz güvenilir olmadığı için araç seçimi JSON tabanlı router ile
/// yapılır; araçların kendisi Semantic Kernel üzerinden yürür. Model desteği
/// geldiğinde FunctionChoiceBehavior.Auto()'ya geçiş tek noktadan yapılabilir.
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly RagService _rag;
    private readonly VectorStore _db;
    private readonly ReportService _reports;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(RagService rag, VectorStore db, ReportService reports, ILogger<AgentOrchestrator> logger)
    {
        _rag = rag;
        _db = db;
        _reports = reports;
        _logger = logger;
    }

    public async Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
    {
        if (!_rag.IsReady)
        {
            return new ChatResponse(
                "Yerel model henüz hazırlanıyor (ilk açılışta model indirme birkaç dakika sürebilir). " +
                "**Durum** sekmesinden ilerlemeyi izleyebilirsin.",
                [], "not_ready");
        }

        var route = await RouteAsync(request.Message, ct);
        _logger.LogInformation("Ajan yönlendirmesi: {Action} (belge: {Doc}, format: {Fmt})",
            route.Action, route.Document, route.Format);

        try
        {
            return route.Action switch
            {
                "summarize" => await HandleSummarizeAsync(route, request, ct),
                "report" => await HandleReportAsync(route, request, ct),
                "list_docs" => await HandleListDocsAsync(),
                _ => await HandleChatAsync(request, ct)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ajan eylemi başarısız: {Action}", route.Action);
            return new ChatResponse($"İşlem sırasında hata oluştu: {ex.Message}", [], "error");
        }
    }

    // ---------- Yönlendirme ----------

    private sealed record Route(string Action, string? Document, string? Format, string? Topic);

    private async Task<Route> RouteAsync(string message, CancellationToken ct)
    {
        var docs = await _db.ListDocumentsAsync();
        var docList = docs.Count == 0
            ? "(yüklü belge yok)"
            : string.Join(", ", docs.Select(d => $"\"{d.FileName}\""));

        var system = $$"""
            Sen bir yönlendirme ajanısın. Kullanıcı mesajını sınıflandırıp SADECE tek satır JSON döndürürsün.
            Başka hiçbir metin yazma.

            JSON şeması:
            {"action":"chat|summarize|report|list_docs","document":"<belge adı veya null>","format":"docx|xlsx|pdf|null","topic":"<konu veya null>"}

            Eylem seçimi:
            - "summarize": kullanıcı belirli BİR belgenin özetini istiyorsa (örn. "X dosyasını özetle").
            - "report": kullanıcı Word/Excel/PDF dosyası olarak rapor/döküman ÜRETMEK istiyorsa (örn. "... hakkında rapor hazırla", "Word raporu oluştur").
            - "list_docs": kullanıcı hangi belgelerin yüklü olduğunu soruyorsa.
            - "chat": diğer her şey — belgelerle ilgili soru-cevap dahil.

            format kuralı (sadece report için): "Word" → docx, "Excel" → xlsx, "PDF" → pdf, belirtilmemişse docx.
            document alanı yüklü belgelerden birebir seçilmeli. Yüklü belgeler: {{docList}}

            Örnekler:
            Kullanıcı: "satış raporunu özetler misin" → {"action":"summarize","document":"satis-raporu.pdf","format":null,"topic":null}
            Kullanıcı: "2025 gelirleri hakkında Excel raporu hazırla" → {"action":"report","document":null,"format":"xlsx","topic":"2025 gelirleri"}
            Kullanıcı: "projenin riskleri neler?" → {"action":"chat","document":null,"format":null,"topic":null}
            """;

        var raw = await _rag.CompleteAsync(system, message, temperature: 0, maxTokens: 200, ct: ct);
        var route = TryParseRoute(raw);
        if (route is null)
        {
            _logger.LogWarning("Router JSON çözümlenemedi, chat'e düşülüyor: {Raw}", raw);
            return new Route("chat", null, null, null);
        }
        return route;
    }

    private static Route? TryParseRoute(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var json = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = json.RootElement;
            string? Get(string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;

            var action = Get("action")?.ToLowerInvariant();
            if (action is not ("chat" or "summarize" or "report" or "list_docs"))
                action = "chat";

            return new Route(action, Get("document"), Get("format")?.ToLowerInvariant(), Get("topic"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- Eylemler ----------

    private async Task<ChatResponse> HandleChatAsync(ChatRequest request, CancellationToken ct)
    {
        var (answer, sources) = await _rag.AnswerAsync(request.Message, request.History, ct);
        return new ChatResponse(answer, sources, "chat");
    }

    private async Task<ChatResponse> HandleSummarizeAsync(Route route, ChatRequest request, CancellationToken ct)
    {
        var doc = await ResolveDocumentAsync(route.Document);
        if (doc is null)
        {
            // Belge çözülemedi → normal RAG cevabına düş
            return await HandleChatAsync(request, ct);
        }

        var summary = await _rag.SummarizeDocumentAsync(doc.Id, ct);
        return new ChatResponse(
            $"**{doc.FileName}** özeti:\n\n{summary}",
            [new SourceRef(doc.Id, doc.FileName, 0, 1.0, "Belgenin tamamı özetlendi")],
            "summarize");
    }

    private async Task<ChatResponse> HandleReportAsync(Route route, ChatRequest request, CancellationToken ct)
    {
        var format = route.Format is "docx" or "xlsx" or "pdf" ? route.Format : "docx";
        var topic = string.IsNullOrWhiteSpace(route.Topic) ? request.Message : route.Topic;

        var (report, sources) = await GenerateReportAsync(request.Message, format, topic, ct);

        var formatName = format switch { "xlsx" => "Excel", "pdf" => "PDF", _ => "Word" };
        return new ChatResponse(
            $"**{report.Title}** başlıklı {formatName} raporunu belgelerdeki içeriğe dayanarak oluşturdum. " +
            $"Aşağıdan indirebilir veya **Raporlar** sekmesinden yönetebilirsin.",
            sources, "report", report);
    }

    private async Task<ChatResponse> HandleListDocsAsync()
    {
        var docs = await _db.ListDocumentsAsync();
        if (docs.Count == 0)
            return new ChatResponse("Henüz yüklü belge yok. **Belgeler** sekmesinden dosya yükleyebilirsin.", [], "list_docs");

        var lines = docs.Select(d =>
            $"- **{d.FileName}** — {StatusTr(d.Status)}, {d.ChunkCount} parça, {FormatSize(d.SizeBytes)}");
        return new ChatResponse(
            $"Yüklü belgeler ({docs.Count}):\n\n{string.Join("\n", lines)}",
            [], "list_docs");
    }

    // ---------- Rapor üretimi (API'den doğrudan da çağrılır) ----------

    public async Task<(ReportInfo Report, List<SourceRef> Sources)> GenerateReportAsync(
        string instruction, string format, string? topic = null, CancellationToken ct = default)
    {
        if (!_rag.IsReady)
            throw new InvalidOperationException("Yerel model henüz hazır değil.");

        var (_, chunkCount) = await _db.GetCountsAsync();
        if (chunkCount == 0)
            throw new InvalidOperationException("Rapor üretmek için önce belge yüklemelisin.");

        var searchTopic = string.IsNullOrWhiteSpace(topic) ? instruction : topic;
        var (context, hits) = await _rag.CollectContextAsync(searchTopic, multiplier: 2, ct);

        ReportInfo report;
        if (format == "xlsx")
        {
            report = await GenerateExcelReportAsync(instruction, context, ct);
        }
        else
        {
            var markdown = await _rag.CompleteAsync(
                $"""
                Sen profesyonel bir rapor yazarı yapay zekasısın. Aşağıdaki BAĞLAM'daki bilgileri kullanarak
                kullanıcının istediği raporu Türkçe ve Markdown biçiminde yaz.
                Kurallar:
                - İlk satır "# " ile başlayan kısa ve açıklayıcı rapor başlığı olsun.
                - "## " ile bölümler kullan (örn. Özet, Bulgular, Detaylar, Sonuç ve Öneriler).
                - Uygun yerlerde madde işareti ve Markdown tablosu kullan.
                - Sadece bağlamdaki bilgilere dayan; bağlamda olmayan bilgi için "belgelerde yer almıyor" de.
                - Raporun sonunda "## Kaynaklar" bölümünde yararlandığın dosya adlarını listele.

                BAĞLAM:
                {context}
                """,
                instruction, temperature: 0.3, maxTokens: 1600, ct: ct);

            var title = ExtractTitle(markdown, instruction);
            report = await _reports.CreateFromMarkdownAsync(title, markdown, format, instruction);
        }

        var sources = hits
            .Take(6)
            .Select(h => new SourceRef(h.DocumentId, h.FileName, h.ChunkIndex, Math.Round(h.Score, 4),
                h.Text.Length > 160 ? h.Text[..160] + "…" : h.Text))
            .ToList();

        return (report, sources);
    }

    private async Task<ReportInfo> GenerateExcelReportAsync(string instruction, string context, CancellationToken ct)
    {
        var raw = await _rag.CompleteAsync(
            $$"""
            Sen veri analisti bir yapay zekasısın. Aşağıdaki BAĞLAM'dan, kullanıcının isteğine uygun
            Excel çalışma kitabı içeriğini çıkar ve SADECE şu şemada JSON döndür (başka metin yazma):
            {"title":"<rapor başlığı>","sheets":[{"name":"<sayfa adı>","headers":["..."],"rows":[["..."],["..."]]}]}

            Kurallar:
            - Sadece bağlamdaki verileri kullan, veri uydurma.
            - Sayısal değerleri olduğu gibi aktar.
            - En az 1 sayfa, her sayfada en az 1 satır olsun. Uygunsa "Özet" sayfası ekle.

            BAĞLAM:
            {{context}}
            """,
            instruction, temperature: 0.1, maxTokens: 1600, ct: ct);

        var (title, tables) = ParseExcelSpec(raw, instruction);
        return await _reports.CreateXlsxAsync(title, tables, instruction);
    }

    private static (string Title, List<ReportTable> Tables) ParseExcelSpec(string raw, string fallbackTitle)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var json = JsonDocument.Parse(raw[start..(end + 1)]);
                var root = json.RootElement;
                var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()! : Truncate(fallbackTitle, 60);

                var tables = new List<ReportTable>();
                if (root.TryGetProperty("sheets", out var sheets) && sheets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sheet in sheets.EnumerateArray())
                    {
                        var name = sheet.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString()! : $"Sayfa{tables.Count + 1}";
                        var headers = sheet.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Array
                            ? h.EnumerateArray().Select(x => x.ToString()).ToList() : [];
                        var rows = new List<List<string>>();
                        if (sheet.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var row in r.EnumerateArray())
                            {
                                if (row.ValueKind == JsonValueKind.Array)
                                    rows.Add(row.EnumerateArray().Select(x => x.ToString()).ToList());
                            }
                        }
                        if (headers.Count > 0 || rows.Count > 0)
                            tables.Add(new ReportTable(name, headers, rows));
                    }
                }
                if (tables.Count > 0)
                    return (title, tables);
            }
            catch (JsonException)
            {
                // JSON bozuksa aşağıdaki yedek plana düş
            }
        }

        // Yedek plan: model tablo üretemedi → ham metni tek sütunlu sayfaya koy
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => new List<string> { l.Trim() })
            .Where(l => l[0].Length > 0)
            .Take(200)
            .ToList();
        return (Truncate(fallbackTitle, 60),
            [new ReportTable("İçerik", ["Satır"], lines)]);
    }

    // ---------- Yardımcılar ----------

    private async Task<DocumentDto?> ResolveDocumentAsync(string? name)
    {
        var docs = await _db.ListDocumentsAsync();
        if (docs.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(name))
            return docs.Count == 1 ? docs[0] : null;

        var exact = docs.FirstOrDefault(d => d.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var partial = docs.FirstOrDefault(d =>
            d.FileName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(Path.GetFileNameWithoutExtension(d.FileName), StringComparison.OrdinalIgnoreCase));
        return partial ?? (docs.Count == 1 ? docs[0] : null);
    }

    private static string ExtractTitle(string markdown, string fallback)
    {
        var firstLine = markdown.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# "));
        return firstLine is not null
            ? firstLine.TrimStart('#', ' ')
            : Truncate(fallback, 60);
    }

    private static string Truncate(string s, int len) =>
        s.Length <= len ? s : s[..len].TrimEnd() + "…";

    private static string StatusTr(string status) => status switch
    {
        "ready" => "hazır",
        "processing" => "işleniyor",
        "queued" => "kuyrukta",
        "error" => "hata",
        _ => status
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
