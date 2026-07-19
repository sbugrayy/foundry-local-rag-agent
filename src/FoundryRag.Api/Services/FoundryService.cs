using Microsoft.AI.Foundry.Local;

namespace FoundryRag.Api.Services;

/// <summary>
/// Foundry Local SDK'sının tüm yaşam döngüsünü yönetir:
/// SDK başlatma, donanım hızlandırma (EP) indirme, model indirme/yükleme,
/// OpenAI-uyumlu yerel çıkarım servisini açma ve embedding istemcisi.
/// SDK'ya dokunan TEK sınıf budur; geri kalan kod yalnızca bu servisin
/// sunduğu string/float[] arayüzünü kullanır.
/// </summary>
public sealed class FoundryService : BackgroundService
{
    private readonly ILogger<FoundryService> _logger;
    private readonly string _chatAlias;
    private readonly string _embedAlias;
    private readonly string _inferenceUrl;

    // Embedding istemcisinin somut tipini alanda tutmamak için delege kullanıyoruz.
    private Func<string[], Task<float[][]>>? _embedFn;
    private readonly SemaphoreSlim _embedLock = new(1, 1);

    public string State { get; private set; } = "starting";
    public string StatusMessage { get; private set; } = "Başlatılıyor...";
    public double EpPercent { get; private set; }
    public string? CurrentEp { get; private set; }
    public double ChatDownloadPercent { get; private set; }
    public double EmbedDownloadPercent { get; private set; }
    public string? ChatModelId { get; private set; }
    public string? EmbeddingModelId { get; private set; }

    public string ChatModelAlias => _chatAlias;
    public string EmbeddingModelAlias => _embedAlias;

    /// <summary>Semantic Kernel'in bağlanacağı OpenAI-uyumlu yerel endpoint.</summary>
    public string InferenceBaseUrl => $"{_inferenceUrl}/v1";

    public bool IsReady => State == "ready";

    public FoundryService(IConfiguration cfg, ILogger<FoundryService> logger)
    {
        _logger = logger;
        _chatAlias = cfg["Foundry:ChatModelAlias"] ?? "phi-4-mini";
        _embedAlias = cfg["Foundry:EmbeddingModelAlias"] ?? "qwen3-embedding-0.6b";
        _inferenceUrl = (cfg["Foundry:InferenceUrl"] ?? "http://127.0.0.1:8744").TrimEnd('/');
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            SetState("initializing", "Foundry Local SDK başlatılıyor...");
            var config = new Configuration
            {
                AppName = "foundry-local-rag-agent",
                LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Information,
                Web = new Configuration.WebService { Urls = _inferenceUrl }
            };
            await FoundryLocalManager.CreateAsync(config, _logger);
            var mgr = FoundryLocalManager.Instance;

            SetState("initializing", "Donanım hızlandırma bileşenleri denetleniyor (ilk seferde indirilir)...");
            await mgr.DownloadAndRegisterEpsAsync((epName, percent) =>
            {
                CurrentEp = epName;
                EpPercent = percent;
            });

            var catalog = await mgr.GetCatalogAsync();

            var chatModel = await catalog.GetModelAsync(_chatAlias)
                ?? throw new InvalidOperationException($"'{_chatAlias}' modeli katalogda bulunamadı.");
            SetState("downloading", $"Sohbet modeli indiriliyor: {_chatAlias} (önbellekteyse atlanır)");
            await chatModel.DownloadAsync(p => ChatDownloadPercent = p);

            var embedModel = await catalog.GetModelAsync(_embedAlias)
                ?? throw new InvalidOperationException($"'{_embedAlias}' modeli katalogda bulunamadı.");
            SetState("downloading", $"Embedding modeli indiriliyor: {_embedAlias} (önbellekteyse atlanır)");
            await embedModel.DownloadAsync(p => EmbedDownloadPercent = p);

            SetState("loading", $"Modeller belleğe yükleniyor: {_chatAlias} + {_embedAlias}");
            await chatModel.LoadAsync();
            await embedModel.LoadAsync();
            ChatModelId = chatModel.Id;
            EmbeddingModelId = embedModel.Id;

            var embeddingClient = await embedModel.GetEmbeddingClientAsync();
            _embedFn = async texts =>
            {
                var response = await embeddingClient.GenerateEmbeddingsAsync(texts);
                return response.Data
                    .Select(d => d.Embedding.Select(v => (float)v).ToArray())
                    .ToArray();
            };

            SetState("loading", "Yerel çıkarım servisi başlatılıyor...");
            await mgr.StartWebServiceAsync();

            SetState("ready", $"Hazır — {_chatAlias} & {_embedAlias} yüklü, endpoint: {InferenceBaseUrl}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foundry Local başlatılamadı");
            SetState("error", $"Foundry Local başlatılamadı: {ex.Message}");
        }
    }

    /// <summary>Metinleri vektöre çevirir. Model hazır değilse hata fırlatır.</summary>
    public async Task<float[][]> GenerateEmbeddingsAsync(string[] texts, CancellationToken ct = default)
    {
        if (_embedFn is null)
            throw new InvalidOperationException("Embedding modeli henüz hazır değil.");

        await _embedLock.WaitAsync(ct);
        try
        {
            return await _embedFn(texts);
        }
        finally
        {
            _embedLock.Release();
        }
    }

    private void SetState(string state, string message)
    {
        State = state;
        StatusMessage = message;
        _logger.LogInformation("Foundry durumu: {State} — {Message}", state, message);
    }
}
