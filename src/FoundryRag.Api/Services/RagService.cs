using System.Text;
using FoundryRag.Api.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace FoundryRag.Api.Services;

/// <summary>
/// Semantic Kernel üzerinden Foundry Local'ın OpenAI-uyumlu endpoint'ine
/// bağlanır; RAG cevaplama ve özetleme akışlarını yürütür.
/// </summary>
public sealed class RagService
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly FoundryService _foundry;
    private readonly EmbeddingService _embeddings;
    private readonly VectorStore _db;
    private readonly int _topK;
    private readonly int _maxContextChars;
    private readonly object _kernelLock = new();
    private Kernel? _kernel;

    public RagService(FoundryService foundry, EmbeddingService embeddings, VectorStore db, IConfiguration cfg)
    {
        _foundry = foundry;
        _embeddings = embeddings;
        _db = db;
        _topK = cfg.GetValue("Rag:TopK", 5);
        _maxContextChars = cfg.GetValue("Rag:MaxContextChars", 6500);
    }

    public bool IsReady => _foundry.IsReady;

    /// <summary>SK çekirdeğini tembel kurar (model kimliği ancak yükleme bitince belli olur).</summary>
    public Kernel GetKernel()
    {
        if (_kernel is not null) return _kernel;
        lock (_kernelLock)
        {
            if (_kernel is not null) return _kernel;
            if (!_foundry.IsReady || _foundry.ChatModelId is null)
                throw new InvalidOperationException("Yerel model henüz hazır değil.");

            var builder = Kernel.CreateBuilder();
            builder.AddOpenAIChatCompletion(
                modelId: _foundry.ChatModelId,
                endpoint: new Uri(_foundry.InferenceBaseUrl),
                apiKey: "not-needed",
                httpClient: SharedHttpClient);
            _kernel = builder.Build();
            return _kernel;
        }
    }

    /// <summary>Tek atımlık sohbet tamamlama (sistem + geçmiş + kullanıcı).</summary>
    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userMessage,
        List<ChatTurn>? history = null,
        double temperature = 0.2,
        int maxTokens = 900,
        CancellationToken ct = default)
    {
        var kernel = GetKernel();
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(systemPrompt);
        if (history is not null)
        {
            foreach (var turn in history.TakeLast(8))
            {
                if (turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                    chatHistory.AddAssistantMessage(turn.Content);
                else
                    chatHistory.AddUserMessage(turn.Content);
            }
        }
        chatHistory.AddUserMessage(userMessage);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = temperature,
            MaxTokens = maxTokens
        };

        var result = await chat.GetChatMessageContentAsync(chatHistory, settings, kernel, ct);
        return result.Content?.Trim() ?? "";
    }

    /// <summary>RAG cevabı: sorguyu embed et → en ilgili parçaları bul → bağlamla cevapla.</summary>
    public async Task<(string Answer, List<SourceRef> Sources)> AnswerAsync(
        string question,
        List<ChatTurn>? history,
        CancellationToken ct = default)
    {
        var (_, chunkCount) = await _db.GetCountsAsync();
        if (chunkCount == 0)
        {
            return ("Henüz işlenmiş belge yok. Önce **Belgeler** sekmesinden dosya yükleyin; " +
                    "ben de sorularınızı o belgelere dayanarak cevaplayayım.", []);
        }

        var queryVector = await _embeddings.EmbedOneAsync(question, ct);
        var hits = await _db.SearchAsync(queryVector, _topK);

        var context = BuildContext(hits);
        var systemPrompt = $"""
            Sen "Foundry RAG Ajanı" adlı, tamamen yerel çalışan bir belge asistanısın.
            Görevin: SADECE aşağıdaki BAĞLAM'daki bilgileri kullanarak Türkçe cevap vermek.
            Kurallar:
            - Bağlamda cevap yoksa açıkça "Bu bilgi yüklü belgelerde bulunmuyor." de; asla tahmin etme, uydurma.
            - Cevapta yararlandığın kaynak dosya adlarını köşeli parantezle belirt, örn: [rapor.docx].
            - Kısa, net ve düzenli yaz; gerekirse madde işareti kullan.

            BAĞLAM:
            {context}
            """;

        var answer = await CompleteAsync(systemPrompt, question, history, temperature: 0.2, maxTokens: 900, ct: ct);

        var sources = hits
            .Select(h => new SourceRef(
                h.DocumentId, h.FileName, h.ChunkIndex, Math.Round(h.Score, 4),
                h.Text.Length > 220 ? h.Text[..220] + "…" : h.Text))
            .ToList();

        return (answer, sources);
    }

    /// <summary>Konuya göre bağlam toplar (rapor üretimi için, sohbetten daha geniş).</summary>
    public async Task<(string Context, List<SearchHit> Hits)> CollectContextAsync(
        string topic, int multiplier = 2, CancellationToken ct = default)
    {
        var queryVector = await _embeddings.EmbedOneAsync(topic, ct);
        var hits = await _db.SearchAsync(queryVector, _topK * multiplier);
        return (BuildContext(hits, _maxContextChars * 2), hits);
    }

    /// <summary>Belgeyi özetler; uzun belgelerde map-reduce (parça özetleri → birleşik özet).</summary>
    public async Task<string> SummarizeDocumentAsync(long documentId, CancellationToken ct = default)
    {
        var doc = await _db.GetDocumentAsync(documentId)
            ?? throw new KeyNotFoundException("Belge bulunamadı.");
        if (doc.Status != "ready")
            throw new InvalidOperationException($"Belge henüz işlenmedi (durum: {doc.Status}).");

        var chunkTexts = await _db.GetChunkTextsAsync(documentId);
        var groups = GroupByBudget(chunkTexts, 6000);

        string summary;
        if (groups.Count == 1)
        {
            summary = await CompleteAsync(
                SummaryPrompt(doc.FileName, final: true),
                groups[0], temperature: 0.3, maxTokens: 800, ct: ct);
        }
        else
        {
            var partials = new List<string>();
            for (var i = 0; i < groups.Count; i++)
            {
                var partial = await CompleteAsync(
                    SummaryPrompt(doc.FileName, final: false),
                    $"(Bölüm {i + 1}/{groups.Count})\n\n{groups[i]}",
                    temperature: 0.3, maxTokens: 400, ct: ct);
                partials.Add(partial);
            }
            summary = await CompleteAsync(
                SummaryPrompt(doc.FileName, final: true),
                "Aşağıdaki bölüm özetlerini tek ve tutarlı bir özete dönüştür:\n\n" +
                string.Join("\n\n---\n\n", partials),
                temperature: 0.3, maxTokens: 800, ct: ct);
        }

        await _db.SetSummaryAsync(documentId, summary);
        return summary;
    }

    private static string SummaryPrompt(string fileName, bool final) => final
        ? $"""
           Sen bir belge özetleme uzmanısın. "{fileName}" adlı belgenin içeriği verilecek.
           Türkçe, iyi yapılandırılmış bir özet yaz:
           - İlk satır: belgenin tek cümlelik ana fikri
           - Ardından en önemli noktaları madde işaretiyle sırala (5-10 madde)
           - Varsa sayısal verileri ve tarihleri koru
           Sadece verilen içeriğe dayan; yorum ekleme.
           """
        : $"""
           Sen bir belge özetleme uzmanısın. "{fileName}" adlı belgenin BİR BÖLÜMÜ verilecek.
           Bu bölümün kilit bilgilerini 3-6 maddede Türkçe özetle. Sayısal verileri koru.
           Sadece verilen içeriğe dayan.
           """;

    private string BuildContext(IReadOnlyList<SearchHit> hits, int? budget = null)
    {
        var limit = budget ?? _maxContextChars;
        var sb = new StringBuilder();
        var index = 1;
        foreach (var hit in hits)
        {
            var entry = $"[{index}] Kaynak: {hit.FileName} (parça {hit.ChunkIndex + 1})\n{hit.Text}\n\n";
            if (sb.Length + entry.Length > limit) break;
            sb.Append(entry);
            index++;
        }
        return sb.Length > 0 ? sb.ToString() : "(ilgili içerik bulunamadı)";
    }

    private static List<string> GroupByBudget(IReadOnlyList<string> texts, int budget)
    {
        var groups = new List<string>();
        var sb = new StringBuilder();
        foreach (var text in texts)
        {
            if (sb.Length + text.Length > budget && sb.Length > 0)
            {
                groups.Add(sb.ToString());
                sb.Clear();
            }
            sb.AppendLine(text);
        }
        if (sb.Length > 0) groups.Add(sb.ToString());
        return groups;
    }
}
