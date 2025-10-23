using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services;

// #FIXME calismiyor , workflow hatali , chat 
//context prompta gonderilse bile son sorgu tekrardan db ye atiliyor bu yuzden context kayboluyor 
public class ChatHistorySqlite : IChatHistoryStore
{
    private readonly string _cs;

    public ChatHistorySqlite(IHostEnvironment env)
    {
        Directory.CreateDirectory(Path.Combine(env.ContentRootPath, "App_Data"));
        _cs = $"Data Source={Path.Combine(env.ContentRootPath, "App_Data", "chat.db")}";
        Init();
    }

    private void Init()
    {
        using var con = new SqliteConnection(_cs);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ChatThread (
  Id TEXT PRIMARY KEY,
  CreatedAtUtc TEXT NOT NULL,
  Title TEXT
);
CREATE TABLE IF NOT EXISTS ChatMessage (
  Id TEXT PRIMARY KEY,
  ThreadId TEXT NOT NULL,
  Role TEXT NOT NULL,
  Content TEXT NULL,
  Sql TEXT NULL,
  Error TEXT NULL,
  CreatedAtUtc TEXT NOT NULL,
  FOREIGN KEY(ThreadId) REFERENCES ChatThread(Id)
);
-- NEW: working set per thread
CREATE TABLE IF NOT EXISTS ChatWorkingSet (
  ThreadId     TEXT PRIMARY KEY,
  Entity       TEXT NOT NULL,
  IdColumn     TEXT NOT NULL,
  IdsJson      TEXT NOT NULL,  -- JSON array of IDs
  UpdatedAtUtc TEXT NOT NULL,
  FOREIGN KEY(ThreadId) REFERENCES ChatThread(Id)
);
";
        cmd.ExecuteNonQuery();
    }

    public async Task<string> EnsureThreadAsync(string? threadId, CancellationToken ct)
    {
        var id = string.IsNullOrWhiteSpace(threadId) ? Guid.NewGuid().ToString("N") : threadId!;
        await using var con = new SqliteConnection(_cs);
        await con.OpenAsync(ct);

        var existsCmd = con.CreateCommand();
        existsCmd.CommandText = "SELECT 1 FROM ChatThread WHERE Id=@id LIMIT 1";
        existsCmd.Parameters.AddWithValue("@id", id);
        var exists = await existsCmd.ExecuteScalarAsync(ct);
        if (exists is not null) return id;

        var ins = con.CreateCommand();
        ins.CommandText = "INSERT INTO ChatThread(Id, CreatedAtUtc) VALUES(@id, @ts)";
        ins.Parameters.AddWithValue("@id", id);
        ins.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
        await ins.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task AppendAsync(ChatMessageDto msg, CancellationToken ct)
    {
        await using var con = new SqliteConnection(_cs);
        await con.OpenAsync(ct);
        var cmd = con.CreateCommand();
        cmd.CommandText = @"INSERT INTO ChatMessage(Id, ThreadId, Role, Content, Sql, Error, CreatedAtUtc)
VALUES(@id,@thread,@role,@content,@sql,@error,@ts)";
        cmd.Parameters.AddWithValue("@id", msg.Id);
        cmd.Parameters.AddWithValue("@thread", msg.ThreadId);
        cmd.Parameters.AddWithValue("@role", msg.Role);
        cmd.Parameters.AddWithValue("@content", (object?)msg.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sql", (object?)msg.Sql ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)msg.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", msg.CreatedAtUtc.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ChatMessageDto>> GetMessagesAsync(string threadId, CancellationToken ct)
    {
        var list = new List<ChatMessageDto>();
        await using var con = new SqliteConnection(_cs);
        await con.OpenAsync(ct);
        var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT Id, ThreadId, Role, Content, Sql, Error, CreatedAtUtc
FROM ChatMessage WHERE ThreadId=@t ORDER BY CreatedAtUtc ASC";
        cmd.Parameters.AddWithValue("@t", threadId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ChatMessageDto(
                r.GetString(0),                          // Id
                r.GetString(1),                          // ThreadId
                r.GetString(2),                          // Role
                r.IsDBNull(3) ? null : r.GetString(3),   // Content
                r.IsDBNull(4) ? null : r.GetString(4),   // Sql
                r.IsDBNull(5) ? null : r.GetString(5),   // Error
                DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)
            ));
        }
        return list;
    }

    public async Task<IReadOnlyList<ChatThreadDto>> ListThreadsAsync(int take, CancellationToken ct)
    {
        var list = new List<ChatThreadDto>();
        await using var con = new SqliteConnection(_cs);
        await con.OpenAsync(ct);
        var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT Id, CreatedAtUtc, Title FROM ChatThread ORDER BY CreatedAtUtc DESC LIMIT @t";
        cmd.Parameters.AddWithValue("@t", take <= 0 ? 20 : take);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ChatThreadDto(
                r.GetString(0),
                DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.IsDBNull(2) ? null : r.GetString(2)
            ));
        }
        return list;
    }

    public async Task SetTitleIfEmptyAsync(string threadId, string title, CancellationToken ct)
    {
        await using var con = new SqliteConnection(_cs);
        await con.OpenAsync(ct);

        var clean = (title ?? string.Empty).Trim();
        if (clean.Length > 120) clean = clean[..120];

        var cmd = con.CreateCommand();
        cmd.CommandText = @"UPDATE ChatThread
                            SET Title = COALESCE(NULLIF(Title, ''), @title)
                            WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", threadId);
        cmd.Parameters.AddWithValue("@title", clean);
        await cmd.ExecuteNonQueryAsync(ct);
    }



    public async Task SetWorkingSetAsync(string threadId, string entity, string idColumn, IReadOnlyList<string> ids, CancellationToken ct)
    {
        await using var con = new SqliteConnection(_cs);
        await con.OpenAsync(ct);


        var idsJson = JsonSerializer.Serialize(ids ?? Array.Empty<string>());

        var cmd = con.CreateCommand();
        cmd.CommandText = @"
INSERT INTO ChatWorkingSet(ThreadId, Entity, IdColumn, IdsJson, UpdatedAtUtc)
VALUES(@threadId, @entity, @idColumn, @idsJson, @ts)
ON CONFLICT(ThreadId) DO UPDATE SET
  Entity       = excluded.Entity,
  IdColumn     = excluded.IdColumn,
  IdsJson      = excluded.IdsJson,
  UpdatedAtUtc = excluded.UpdatedAtUtc
";
        cmd.Parameters.AddWithValue("@threadId", threadId);
        cmd.Parameters.AddWithValue("@entity", entity);
        cmd.Parameters.AddWithValue("@idColumn", idColumn);
        cmd.Parameters.AddWithValue("@idsJson", idsJson);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string Entity, string IdColumn, IReadOnlyList<string> Ids)?> GetWorkingSetAsync(string threadId, CancellationToken ct)
    {
        await using var con = new SqliteConnection(_cs);
        await con.OpenAsync(ct);

        var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT Entity, IdColumn, IdsJson FROM ChatWorkingSet WHERE ThreadId=@t LIMIT 1";
        cmd.Parameters.AddWithValue("@t", threadId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        var entity = r.GetString(0);
        var idColumn = r.GetString(1);
        var idsJson = r.GetString(2);

        IReadOnlyList<string> ids;
        try
        {
            ids = JsonSerializer.Deserialize<List<string>>(idsJson) ?? new List<string>();
        }
        catch
        {
            ids = new List<string>();
        }

        return (entity, idColumn, ids);
    }
}
