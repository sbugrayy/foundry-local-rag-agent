using System.Text;
using FoundryRag.Api.Models;

namespace FoundryRag.Api.Services;

/// <summary>
/// Ayrıştırılmış blokları, hedef karakter boyutunda ve bindirmeli (overlap)
/// parçalara böler. Blok sınırlarına saygı duyar; aşırı uzun blokları cümle
/// sınırlarından ayırır.
/// </summary>
public sealed class Chunker
{
    private readonly int _chunkSize;
    private readonly int _overlap;

    public Chunker(IConfiguration cfg)
    {
        _chunkSize = cfg.GetValue("Rag:ChunkSize", 1100);
        _overlap = cfg.GetValue("Rag:ChunkOverlap", 180);
    }

    public List<ChunkPiece> Chunk(IReadOnlyList<ParsedBlock> blocks)
    {
        var pieces = new List<ChunkPiece>();
        var current = new StringBuilder();
        string? currentSection = null;

        void Emit()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0)
                pieces.Add(new ChunkPiece(text, currentSection));

            // Bindirme: son kısmı bir sonraki parçanın başına taşı
            if (text.Length > _overlap)
            {
                var tail = text[^_overlap..];
                var sentenceStart = tail.IndexOfAny(['.', '!', '?', '\n']);
                if (sentenceStart >= 0 && sentenceStart < tail.Length - 1)
                    tail = tail[(sentenceStart + 1)..].TrimStart();
                current = new StringBuilder(tail.Length > 0 ? tail + "\n" : "");
            }
            else
            {
                current = new StringBuilder();
            }
        }

        foreach (var block in blocks)
        {
            var text = block.Text.Trim();
            if (text.Length == 0) continue;

            if (block.Section != currentSection && current.Length > _chunkSize / 2)
                Emit();
            currentSection = block.Section ?? currentSection;

            foreach (var part in SplitLongText(text, _chunkSize))
            {
                if (current.Length + part.Length + 1 > _chunkSize && current.Length > 0)
                    Emit();
                current.AppendLine(part);
            }
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
            pieces.Add(new ChunkPiece(last, currentSection));

        return pieces;
    }

    /// <summary>Tek başına chunk boyutunu aşan metinleri cümle sınırlarından böler.</summary>
    private static IEnumerable<string> SplitLongText(string text, int maxLen)
    {
        if (text.Length <= maxLen)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var len = Math.Min(maxLen, text.Length - start);
            if (start + len < text.Length)
            {
                // Cümle sonu ya da boşluk ara (geriye doğru)
                var window = text.Substring(start, len);
                var cut = window.LastIndexOfAny(['.', '!', '?', '\n']);
                if (cut < maxLen / 3) cut = window.LastIndexOf(' ');
                if (cut > maxLen / 3) len = cut + 1;
            }
            yield return text.Substring(start, len).Trim();
            start += len;
        }
    }
}
