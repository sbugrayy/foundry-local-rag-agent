using System.Runtime.InteropServices;
using FoundryRag.Api.Models;
using Microsoft.Data.Sqlite;

namespace FoundryRag.Api.Services;

/// <summary>
/// SQLite üzerinde belge + parça (chunk) + embedding + rapor kayıtlarını tutar.
/// Embedding'ler normalize edilmiş float[] olarak BLOB kolonunda saklanır;
/// arama, tüm vektörler üzerinde nokta çarpımıyla (kosinüs eşdeğeri) yapılır.
/// Bu ölçek için (on binlerce parça) kaba kuvvet arama yeterlidir.
/// </summary>
public sealed class VectorStore
{
    private readonly string _connString;

    public string DataDir { get; }
    public string UploadsDir => Path.Combine(DataDir, "uploads");
    public string ReportsDir => Path.Combine(DataDir, "reports");
    public string DbPath { get; }

    public VectorStore(IConfiguration cfg, IWebHostEnvironment env)
    {
        DataDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, cfg["Storage:DataDir"] ?? "data"));
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(UploadsDir);
        Directory.CreateDirectory(ReportsDir);
        DbPath = Path.Combine(DataDir, "foundryrag.db");
        _connString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }

    public async Task InitAsync()
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS documents (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_name   TEXT NOT NULL,
                extension   TEXT NOT NULL,
                size_bytes  INTEGER NOT NULL,
                status      TEXT NOT NULL DEFAULT 'queued',
                error       TEXT,
                summary     TEXT,
                chunk_count INTEGER NOT NULL DEFAULT 0,
                uploaded_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chunks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                chunk_index INTEGER NOT NULL,
                section     TEXT,
                text        TEXT NOT NULL,
                embedding   BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id);

            CREATE TABLE IF NOT EXISTS reports (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                title       TEXT NOT NULL,
                format      TEXT NOT NULL,
                file_name   TEXT NOT NULL,
                instruction TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- Belgeler ----------

    public async Task<long> InsertDocumentAsync(string fileName, string extension, long sizeBytes)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (file_name, extension, size_bytes, status, uploaded_at)
            VALUES ($name, $ext, $size, 'queued', $at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$ext", extension);
        cmd.Parameters.AddWithValue("$size", sizeBytes);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        var id = (long)(await cmd.ExecuteScalarAsync())!;
        return id;
    }

    public async Task UpdateDocumentStatusAsync(long id, string status, string? error = null, int? chunkCount = null)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE documents
            SET status = $status,
                error = $error,
                chunk_count = COALESCE($chunks, chunk_count)
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$chunks", (object?)chunkCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetSummaryAsync(long id, string summary)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE documents SET summary = $s WHERE id = $id;";
        cmd.Parameters.AddWithValue("$s", summary);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<DocumentDto>> ListDocumentsAsync()
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_name, extension, size_bytes, status, chunk_count, uploaded_at, error, summary
            FROM documents ORDER BY id DESC;
            """;
        var list = new List<DocumentDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadDocument(reader));
        }
        return list;
    }

    public async Task<DocumentDto?> GetDocumentAsync(long id)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_name, extension, size_bytes, status, chunk_count, uploaded_at, error, summary
            FROM documents WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadDocument(reader) : null;
    }

    private static DocumentDto ReadDocument(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt64(3),
        r.GetString(4),
        r.GetInt32(5),
        DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8));

    public async Task DeleteDocumentAsync(long id)
    {
        using var conn = Open();
        var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Uygulama yeniden başladığında yarım kalmış belgeleri kuyruğa geri koymak için.</summary>
    public async Task<List<long>> GetPendingDocumentIdsAsync()
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM documents WHERE status IN ('queued','processing') ORDER BY id;";
        var ids = new List<long>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    // ---------- Parçalar (chunks) ----------

    public async Task InsertChunksAsync(long documentId, IReadOnlyList<ChunkPiece> pieces, IReadOnlyList<float[]> embeddings)
    {
        if (pieces.Count != embeddings.Count)
            throw new ArgumentException("Parça ve embedding sayıları eşleşmiyor.");

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO chunks (document_id, chunk_index, section, text, embedding)
            VALUES ($doc, $idx, $section, $text, $emb);
            """;
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Integer);
        var pIdx = cmd.Parameters.Add("$idx", SqliteType.Integer);
        var pSection = cmd.Parameters.Add("$section", SqliteType.Text);
        var pText = cmd.Parameters.Add("$text", SqliteType.Text);
        var pEmb = cmd.Parameters.Add("$emb", SqliteType.Blob);

        for (var i = 0; i < pieces.Count; i++)
        {
            pDoc.Value = documentId;
            pIdx.Value = i;
            pSection.Value = (object?)pieces[i].Section ?? DBNull.Value;
            pText.Value = pieces[i].Text;
            pEmb.Value = ToBlob(embeddings[i]);
            await cmd.ExecuteNonQueryAsync();
        }
        tx.Commit();
    }

    public async Task<List<string>> GetChunkTextsAsync(long documentId)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT text FROM chunks WHERE document_id = $id ORDER BY chunk_index;";
        cmd.Parameters.AddWithValue("$id", documentId);
        var list = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>
    /// Sorgu vektörüne en yakın parçaları döndürür (vektörler normalize → dot = cosine).
    /// <paramref name="documentIds"/> verilirse arama yalnızca o belgelere kapsamlanır.
    /// </summary>
    public async Task<List<SearchHit>> SearchAsync(
        float[] queryVector, int topK, IReadOnlyCollection<long>? documentIds = null)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();

        var scopeFilter = "";
        if (documentIds is { Count: > 0 })
        {
            var paramNames = new List<string>();
            var i = 0;
            foreach (var id in documentIds)
            {
                var name = $"$doc{i++}";
                paramNames.Add(name);
                cmd.Parameters.AddWithValue(name, id);
            }
            scopeFilter = $" AND c.document_id IN ({string.Join(", ", paramNames)})";
        }

        cmd.CommandText = $"""
            SELECT c.id, c.document_id, d.file_name, c.chunk_index, c.text, c.embedding
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE d.status = 'ready'{scopeFilter};
            """;
        var hits = new List<SearchHit>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var blob = (byte[])reader.GetValue(5);
            var score = Dot(queryVector, blob);
            hits.Add(new SearchHit(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                score));
        }
        return hits
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    public async Task<(int docCount, int chunkCount)> GetCountsAsync()
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM documents), (SELECT COUNT(*) FROM chunks);";
        using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    // ---------- Raporlar ----------

    public async Task<long> InsertReportAsync(string title, string format, string fileName, string instruction)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO reports (title, format, file_name, instruction, created_at)
            VALUES ($title, $format, $file, $instr, $at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$format", format);
        cmd.Parameters.AddWithValue("$file", fileName);
        cmd.Parameters.AddWithValue("$instr", instruction);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<List<ReportInfo>> ListReportsAsync()
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, format, file_name, instruction, created_at FROM reports ORDER BY id DESC;";
        var list = new List<ReportInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ReportInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return list;
    }

    public async Task<ReportInfo?> GetReportAsync(long id)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, format, file_name, instruction, created_at FROM reports WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ReportInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public async Task DeleteReportAsync(long id)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM reports WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- Vektör yardımcıları ----------

    private static byte[] ToBlob(float[] vector) =>
        MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    private static double Dot(float[] query, byte[] blob)
    {
        var stored = MemoryMarshal.Cast<byte, float>(blob);
        var len = Math.Min(query.Length, stored.Length);
        double sum = 0;
        for (var i = 0; i < len; i++)
            sum += query[i] * stored[i];
        return sum;
    }
}
