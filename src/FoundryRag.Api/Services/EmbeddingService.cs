namespace FoundryRag.Api.Services;

/// <summary>
/// Foundry Local embedding modelini kullanarak metinleri normalize edilmiş
/// vektörlere çevirir. Vektörler normalize edildiği için arama tarafında
/// nokta çarpımı doğrudan kosinüs benzerliği verir.
/// </summary>
public sealed class EmbeddingService
{
    private const int BatchSize = 8;
    private readonly FoundryService _foundry;

    public EmbeddingService(FoundryService foundry) => _foundry = foundry;

    public async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = texts.Skip(i).Take(BatchSize).ToArray();
            var vectors = await _foundry.GenerateEmbeddingsAsync(batch, ct);
            results.AddRange(vectors.Select(Normalize));
        }
        return results;
    }

    public async Task<float[]> EmbedOneAsync(string text, CancellationToken ct = default)
    {
        var vectors = await _foundry.GenerateEmbeddingsAsync([text], ct);
        return Normalize(vectors[0]);
    }

    private static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var norm = Math.Sqrt(sum);
        if (norm < 1e-9) return v;
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
            result[i] = (float)(v[i] / norm);
        return result;
    }
}
