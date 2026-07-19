using System.Threading.Channels;
using FoundryRag.Api.Models;

namespace FoundryRag.Api.Services;

/// <summary>
/// Belge içe aktarma hattı: yüklenen dosyayı diske kaydeder, kuyruğa alır;
/// arka plan işçisi model hazır olduğunda ayrıştırır → parçalar → embedding
/// üretir → SQLite'a yazar. Uygulama yeniden başlarsa yarım kalanları sürdürür.
/// </summary>
public sealed class IngestionService : BackgroundService
{
    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>();
    private readonly VectorStore _db;
    private readonly DocumentParserRegistry _parsers;
    private readonly Chunker _chunker;
    private readonly EmbeddingService _embeddings;
    private readonly FoundryService _foundry;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        VectorStore db,
        DocumentParserRegistry parsers,
        Chunker chunker,
        EmbeddingService embeddings,
        FoundryService foundry,
        ILogger<IngestionService> logger)
    {
        _db = db;
        _parsers = parsers;
        _chunker = chunker;
        _embeddings = embeddings;
        _foundry = foundry;
        _logger = logger;
    }

    /// <summary>Dosyayı kaydeder ve işleme kuyruğuna ekler.</summary>
    public async Task<DocumentDto> EnqueueAsync(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!_parsers.IsSupported(ext))
            throw new NotSupportedException(
                $"'{ext}' desteklenmiyor. Desteklenen türler: {string.Join(", ", DocumentParserRegistry.SupportedExtensions)}");

        var docId = await _db.InsertDocumentAsync(fileName, ext, content.LongLength);
        var path = GetUploadPath(docId, ext);
        await File.WriteAllBytesAsync(path, content);
        _queue.Writer.TryWrite(docId);

        return (await _db.GetDocumentAsync(docId))!;
    }

    public string GetUploadPath(long docId, string ext) =>
        Path.Combine(_db.UploadsDir, $"{docId}{ext}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yarım kalanları yeniden kuyruğa al
        foreach (var id in await _db.GetPendingDocumentIdsAsync())
            _queue.Writer.TryWrite(id);

        await foreach (var docId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(docId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Belge {DocId} işlenemedi", docId);
                await _db.UpdateDocumentStatusAsync(docId, "error", ex.Message);
            }
        }
    }

    private async Task ProcessAsync(long docId, CancellationToken ct)
    {
        var doc = await _db.GetDocumentAsync(docId);
        if (doc is null) return;

        // Model hazır olana kadar bekle (indirme sürüyor olabilir)
        while (!_foundry.IsReady)
        {
            if (_foundry.State == "error")
            {
                await _db.UpdateDocumentStatusAsync(docId, "error",
                    "Yerel model başlatılamadı — Durum sekmesine bakın.");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        _logger.LogInformation("Belge işleniyor: {Name} (#{Id})", doc.FileName, docId);
        await _db.UpdateDocumentStatusAsync(docId, "processing");

        var path = GetUploadPath(docId, doc.Extension);
        var content = await File.ReadAllBytesAsync(path, ct);

        var blocks = _parsers.Parse(content, doc.FileName);
        if (blocks.Count == 0)
        {
            await _db.UpdateDocumentStatusAsync(docId, "error", "Belgeden metin çıkarılamadı (boş veya taranmış görüntü olabilir).");
            return;
        }

        var pieces = _chunker.Chunk(blocks);
        var vectors = await _embeddings.EmbedAsync(pieces.Select(p => p.Text).ToList(), ct);
        await _db.InsertChunksAsync(docId, pieces, vectors);
        await _db.UpdateDocumentStatusAsync(docId, "ready", chunkCount: pieces.Count);

        _logger.LogInformation("Belge hazır: {Name} — {Count} parça", doc.FileName, pieces.Count);
    }
}
