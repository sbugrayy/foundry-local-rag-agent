namespace FoundryRag.Api.Models;

/// <summary>Sohbet isteği: kullanıcı mesajı + istemcide tutulan geçmiş.</summary>
public record ChatRequest(string Message, List<ChatTurn>? History);

/// <summary>Tek bir sohbet turu. Role: "user" | "assistant".</summary>
public record ChatTurn(string Role, string Content);

/// <summary>Ajanın cevabı: metin, kullanılan kaynaklar, yürütülen eylem ve varsa üretilen rapor.</summary>
public record ChatResponse(
    string Answer,
    List<SourceRef> Sources,
    string Action,
    ReportInfo? Report = null);

/// <summary>Cevaba dayanak olan belge parçası.</summary>
public record SourceRef(
    long DocumentId,
    string FileName,
    int ChunkIndex,
    double Score,
    string Snippet);

/// <summary>Yüklü belge bilgisi.</summary>
public record DocumentDto(
    long Id,
    string FileName,
    string Extension,
    long SizeBytes,
    string Status,
    int ChunkCount,
    DateTime UploadedAtUtc,
    string? Error,
    string? Summary);

/// <summary>Kalıcı sohbet mesajı (SQLite). Role: "user" | "assistant".</summary>
public record ChatMessageDto(
    long Id,
    string Role,
    string Content,
    List<SourceRef>? Sources,
    ReportInfo? Report,
    DateTime CreatedAtUtc);

/// <summary>Üretilmiş rapor kaydı.</summary>
public record ReportInfo(
    long Id,
    string Title,
    string Format,
    string FileName,
    string Instruction,
    DateTime CreatedAtUtc);

/// <summary>
/// Rapor üretme isteği. Format: "docx" | "xlsx" | "pdf".
/// DocumentId verilirse içerik yalnızca o belgeden toplanır.
/// </summary>
public record ReportRequest(string Instruction, string Format, long? DocumentId = null);

/// <summary>Vektör aramasından dönen isabet.</summary>
public record SearchHit(
    long ChunkId,
    long DocumentId,
    string FileName,
    int ChunkIndex,
    string Text,
    double Score);

/// <summary>Belgeden ayrıştırılan ham metin bloğu (bölüm bilgisiyle).</summary>
public record ParsedBlock(string Text, string? Section);

/// <summary>Gömme (embedding) üretilecek metin parçası.</summary>
public record ChunkPiece(string Text, string? Section);

/// <summary>Excel raporu için tablo tanımı.</summary>
public record ReportTable(string Name, List<string> Headers, List<List<string>> Rows);
