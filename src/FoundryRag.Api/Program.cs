using System.Text.Json;
using FoundryRag.Api.Models;
using FoundryRag.Api.Services;
using Microsoft.AspNetCore.Http.Features;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// Büyük belge yüklemelerine izin ver (200 MB)
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 200_000_000);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000);

builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<FoundryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FoundryService>());
builder.Services.AddSingleton<DocumentParserRegistry>();
builder.Services.AddSingleton<Chunker>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionService>());
builder.Services.AddSingleton<RagService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<AgentOrchestrator>();

var app = builder.Build();

await app.Services.GetRequiredService<VectorStore>().InitAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// ---------- Durum ----------

api.MapGet("/status", async (FoundryService foundry, VectorStore db) =>
{
    var (docCount, chunkCount) = await db.GetCountsAsync();
    var reports = await db.ListReportsAsync();
    return Results.Ok(new
    {
        state = foundry.State,
        message = foundry.StatusMessage,
        chatModel = new { alias = foundry.ChatModelAlias, id = foundry.ChatModelId, downloadPercent = foundry.ChatDownloadPercent },
        embeddingModel = new { alias = foundry.EmbeddingModelAlias, id = foundry.EmbeddingModelId, downloadPercent = foundry.EmbedDownloadPercent },
        ep = new { name = foundry.CurrentEp, percent = foundry.EpPercent },
        inferenceEndpoint = foundry.InferenceBaseUrl,
        documentCount = docCount,
        chunkCount,
        reportCount = reports.Count,
        dataDir = db.DataDir
    });
});

api.MapPost("/foundry/retry", (FoundryService foundry) =>
{
    foundry.RequestRetry();
    return Results.Ok(new { ok = true, message = "Yeniden deneme tetiklendi." });
});

// ---------- Belgeler ----------

api.MapGet("/documents", async (VectorStore db) =>
    Results.Ok(await db.ListDocumentsAsync()));

api.MapPost("/documents/upload", async (HttpRequest request, IngestionService ingestion) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data bekleniyor." });

    var form = await request.ReadFormAsync();
    if (form.Files.Count == 0)
        return Results.BadRequest(new { error = "Dosya seçilmedi." });

    var results = new List<object>();
    foreach (var file in form.Files)
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var doc = await ingestion.EnqueueAsync(file.FileName, ms.ToArray());
            results.Add(new { ok = true, document = doc });
        }
        catch (NotSupportedException ex)
        {
            results.Add(new { ok = false, fileName = file.FileName, error = ex.Message });
        }
    }
    return Results.Ok(results);
});

api.MapDelete("/documents/{id:long}", async (long id, VectorStore db, IngestionService ingestion) =>
{
    var doc = await db.GetDocumentAsync(id);
    if (doc is null) return Results.NotFound(new { error = "Belge bulunamadı." });

    await db.DeleteDocumentAsync(id);
    var path = ingestion.GetUploadPath(id, doc.Extension);
    if (File.Exists(path)) File.Delete(path);
    return Results.Ok(new { ok = true });
});

api.MapPost("/documents/{id:long}/summarize", async (long id, RagService rag) =>
{
    if (!rag.IsReady)
        return Results.Json(new { error = "Yerel model henüz hazır değil." }, statusCode: 503);
    try
    {
        var summary = await rag.SummarizeDocumentAsync(id);
        return Results.Ok(new { summary });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---------- Sohbet (agentic) ----------

api.MapPost("/chat", async (ChatRequest request, AgentOrchestrator agent, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "Mesaj boş olamaz." });
    var response = await agent.HandleAsync(request, ct);
    return Results.Ok(response);
});

// SSE akışlı sohbet: her olay `data: {json}\n\n` satırı olarak yazılır.
// Olay tipleri: status | sources | delta | report | done | error
var sseJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
api.MapPost("/chat/stream", async (HttpContext http, ChatRequest request, AgentOrchestrator agent) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsJsonAsync(new { error = "Mesaj boş olamaz." });
        return;
    }

    http.Response.ContentType = "text/event-stream; charset=utf-8";
    http.Response.Headers.CacheControl = "no-cache";
    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

    var ct = http.RequestAborted;
    async Task Emit(object payload)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, sseJson)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await agent.HandleStreamAsync(request, Emit, ct);
    }
    catch (OperationCanceledException)
    {
        // İstemci bağlantıyı kapattı
    }
});

// ---------- Raporlar ----------

api.MapGet("/reports", async (VectorStore db) =>
    Results.Ok(await db.ListReportsAsync()));

api.MapPost("/reports", async (ReportRequest request, AgentOrchestrator agent, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Instruction))
        return Results.BadRequest(new { error = "Rapor talimatı boş olamaz." });

    var format = request.Format?.ToLowerInvariant() is "xlsx" or "pdf" or "docx"
        ? request.Format.ToLowerInvariant()
        : "docx";

    try
    {
        var scope = request.DocumentId is long docId ? new[] { docId } : null;
        var (report, sources) = await agent.GenerateReportAsync(
            request.Instruction, format, documentIds: scope, ct: ct);
        return Results.Ok(new { report, sources });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 503);
    }
});

api.MapGet("/reports/{id:long}/download", async (long id, VectorStore db, ReportService reports) =>
{
    var report = await db.GetReportAsync(id);
    if (report is null) return Results.NotFound(new { error = "Rapor bulunamadı." });

    var path = reports.GetReportPath(report);
    if (!File.Exists(path)) return Results.NotFound(new { error = "Rapor dosyası diskte yok." });

    return Results.File(path, ReportService.GetContentType(report.Format), report.FileName);
});

api.MapDelete("/reports/{id:long}", async (long id, VectorStore db, ReportService reports) =>
{
    var report = await db.GetReportAsync(id);
    if (report is null) return Results.NotFound(new { error = "Rapor bulunamadı." });

    await db.DeleteReportAsync(id);
    var path = reports.GetReportPath(report);
    if (File.Exists(path)) File.Delete(path);
    return Results.Ok(new { ok = true });
});

app.Run();
