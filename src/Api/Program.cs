using Serilog;
using Api.DTOs;
using Api.Services;
using Api.Services.Abstractions;
using System.Text.Json;
using System.Collections;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------
// Serilog
// -----------------------------
builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .WriteTo.Console()
       .Enrich.FromLogContext();
});

// -----------------------------
// CORS (Vite dev server için 5173)
// -----------------------------
const string CorsPolicy = "DevCors";
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: CorsPolicy, policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// -----------------------------
// DI
// -----------------------------
builder.Services.AddScoped<ISchemaService, SchemaServiceSqlServer>();
builder.Services.AddScoped<ISqlValidator, SqlValidator>();
builder.Services.AddScoped<ISqlExecutor, SqlExecutor>();
builder.Services.AddSingleton<IPromptBuilder, PromptBuilder>();

// Chat history store (SQLite)
builder.Services.AddSingleton<IChatHistoryStore, ChatHistorySqlite>();

// Sliced schema cache
builder.Services.AddSingleton<ISlicedSchemaCache, SlicedSchemaCache>();

// self-heal runner
builder.Services.AddScoped<SelfHealingSqlRunner>();

// -----------------------------
// OpenAI LLM (HttpClient)
// -----------------------------
builder.Services.AddHttpClient<ILlmService, LlmService>(client =>
{
    var baseUrl = builder.Configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// -----------------------------
// Swagger
// -----------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// -----------------------------
// Middleware Pipeline
// -----------------------------
app.UseSerilogRequestLogging();
app.UseCors(CorsPolicy);
app.UseSwagger();
app.UseSwaggerUI();

// -----------------------------
// Endpoints
// -----------------------------

// health
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// raw schema
app.MapGet("/api/schema", async (ISchemaService schemaSvc, CancellationToken ct) =>
{
    try
    {
        var schema = await schemaSvc.GetSchemaAsync(ct);
        return Results.Ok(schema);
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "/api/schema failed", detail: ex.Message);
    }
});

// ==========================================================
// 1) LLM ile packleme (schema slicing) → cache et
// ==========================================================
app.MapPost("/api/schema/slice/llm", async (
    ISchemaService schemaSvc,
    IPromptBuilder promptBuilder,
    ILlmService llm,
    ISlicedSchemaCache cache,
    CancellationToken ct) =>
{
    var schema = await schemaSvc.GetSchemaAsync(ct);
    var prompt = promptBuilder.BuildSchemaSlicingPrompt(schema);

    var json = await llm.GetRawJsonAsync(prompt, ct);

    SliceResultDto sliced;
    try
    {
        sliced = JsonSerializer.Deserialize<SliceResultDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse LLM sliced schema JSON.");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "JSON parse failed", detail = ex.Message, raw = json });
    }

    cache.Set(sliced);
    return Results.Ok(new { cached = true, packs = sliced.Packs.Count });
});

// cache’i oku
app.MapGet("/api/schema/slice/cached", (ISlicedSchemaCache cache) =>
{
    return cache.TryGet(out var s) && s != null
        ? Results.Ok(s)
        : Results.NotFound(new { error = "No cached sliced schema. Run POST /api/schema/slice/llm first." });
});

// ==========================================================
// 2) LLM ile route (hangi pack?)
// ==========================================================
app.MapPost("/api/route/llm", async (
    RouteRequestDto req,
    ISlicedSchemaCache cache,
    IPromptBuilder promptBuilder,
    ILlmService llm,
    CancellationToken ct) =>
{
    if (!cache.TryGet(out var sliced) || sliced == null)
        return Results.BadRequest(new { error = "No cached sliced schema. Run /api/schema/slice/llm first." });

    var prompt = promptBuilder.BuildRoutingPrompt(sliced, req.Question);
    var json = await llm.GetRawJsonAsync(prompt, ct);

    RouteResponseDto routed;
    try
    {
        routed = JsonSerializer.Deserialize<RouteResponseDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse LLM routing JSON.");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "LLM routing JSON parse failed", detail = ex.Message, raw = json });
    }

    return Results.Ok(routed);
});

// ==========================================================
// CHAT HISTORY ENDPOINTLERI
// ==========================================================

// Yeni sohbet başlat (conversationId oluşturur)
app.MapPost("/api/chat/start", async (IChatHistoryStore store, CancellationToken ct) =>
{
    var id = await store.EnsureThreadAsync(null, ct);
    return Results.Ok(new { conversationId = id });
});

// Belirli sohbetin mesajlarını getir
app.MapGet("/api/chat/{id}/messages", async (string id, IChatHistoryStore store, CancellationToken ct) =>
{
    var msgs = await store.GetMessagesAsync(id, ct);
    return Results.Ok(msgs);
});

// Son N sohbeti listele (sidebar için)
app.MapGet("/api/chat/list", async (int? take, IChatHistoryStore store, CancellationToken ct) =>
{
    var items = await store.ListThreadsAsync((take ?? 20) <= 0 ? 20 : take!.Value, ct);
    return Results.Ok(items);
});

// ==========================================================
// 3) /api/query  (SQL üret + self-heal + execute)
//    + history logging
//    + title = first user message
//    + LLM prompt’a geçmiş bağlamı ekleme
//    + Assistant content = sonuçtan özet (artık "Done." değil)
// ==========================================================
app.MapPost("/api/query", async (
    QueryRequest req,
    ISchemaService schemaSvc,
    ISlicedSchemaCache cache,
    IPromptBuilder promptBuilder,
    ILlmService llm,
    ISqlValidator validator,
    ISqlExecutor exec,
    SelfHealingSqlRunner runner,
    IChatHistoryStore history,
    CancellationToken ct) =>
{
    if (!cache.TryGet(out var sliced) || sliced == null)
        return Results.BadRequest(new { error = "No cached sliced schema. Run POST /api/schema/slice/llm first." });

    // ---- Thread + user mesajını kaydet
    var threadId = await history.EnsureThreadAsync(req.ConversationId, ct);
    await history.AppendAsync(new ChatMessageDto(
        Id: Guid.NewGuid().ToString("N"),
        ThreadId: threadId,
        Role: "user",
        Content: req.Question,
        Sql: null,
        Error: null,
        CreatedAtUtc: DateTime.UtcNow
    ), ct);

    // Başlığı ilk user mesajıyla bir kez ayarla
    await history.SetTitleIfEmptyAsync(threadId, req.Question ?? string.Empty, ct);

    // ---- Geçmişi topla ve kısa snippet oluştur (son 6 mesaj)
    static string BuildHistorySnippet(IEnumerable<ChatMessageDto> all, int maxChars = 2500)
    {
        var last = all.OrderBy(m => m.CreatedAtUtc).TakeLast(6).ToList();

        var sb = new System.Text.StringBuilder();
        foreach (var m in last)
        {
            var content = (m.Content ?? "").Trim();
            var hasUsefulText =
                !string.IsNullOrWhiteSpace(content) &&
                !string.Equals(content, "Returned result.", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(content, "No rows.", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(content, "Error", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(content, "Done.", StringComparison.OrdinalIgnoreCase);

            if (m.Role == "user")
            {
                if (hasUsefulText) sb.AppendLine($"User: {content}");
            }
            else // assistant
            {
                if (!string.IsNullOrWhiteSpace(m.Sql))
                {
                    var sql = m.Sql!;
                    if (sql.Length > 500) sql = sql[..500] + " ...";
                    sb.AppendLine("Assistant (sql):");
                    sb.AppendLine($"```sql\n{sql}\n```");
                }
                if (!string.IsNullOrWhiteSpace(m.Error))
                {
                    sb.AppendLine($"Assistant (error): {m.Error!.Trim()}");
                }
                else if (hasUsefulText)
                {
                    sb.AppendLine($"Assistant: {content}");
                }
            }
        }

        var s = sb.ToString();
        if (s.Length > maxChars) s = s[..maxChars] + " ...";
        return s.Trim();
    }

    var allMsgs = await history.GetMessagesAsync(threadId, ct);
    var convoSnippet = BuildHistorySnippet(allMsgs);

    var userQuestionWithContext = string.IsNullOrWhiteSpace(convoSnippet)
        ? req.Question ?? string.Empty
        : $"Context from previous turns:\n{convoSnippet}\n\nCurrent request: {req.Question ?? string.Empty}";

    // ---- Tam şema (kolon/desc için)
    var schema = await schemaSvc.GetSchemaAsync(ct);

    // ---- Route (bağlamlı soru ile)
    var routePrompt = promptBuilder.BuildRoutingPrompt(sliced, userQuestionWithContext);
    var routeJson = await llm.GetRawJsonAsync(routePrompt, ct);

    RouteResponseDto route;
    try
    {
        route = JsonSerializer.Deserialize<RouteResponseDto>(
            routeJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? throw new InvalidOperationException("Failed to parse LLM route JSON.");
    }
    catch (Exception ex)
    {
        await history.AppendAsync(new ChatMessageDto(
            Id: Guid.NewGuid().ToString("N"),
            ThreadId: threadId,
            Role: "assistant",
            Content: $"Error while routing: {ex.Message}",
            Sql: null,
            Error: $"LLM route JSON parse failed: {ex.Message}",
            CreatedAtUtc: DateTime.UtcNow
        ), ct);

        return Results.BadRequest(new { error = "LLM route JSON parse failed", detail = ex.Message, raw = routeJson });
    }

    var pack = sliced.Packs.FirstOrDefault(p => p.CategoryId == route.SelectedCategoryId);
    if (pack == null)
    {
        await history.AppendAsync(new ChatMessageDto(
            Id: Guid.NewGuid().ToString("N"),
            ThreadId: threadId,
            Role: "assistant",
            Content: "Selected pack not found.",
            Sql: null,
            Error: "Selected pack not found in sliced schema",
            CreatedAtUtc: DateTime.UtcNow
        ), ct);

        return Results.BadRequest(new { error = "Selected pack not found in sliced schema", route });
    }

    // İsteğe bağlı: ikinci en iyi adaydan 1 komşu
    var adjacent = sliced.Packs
        .Where(p => route.CandidateCategoryIds.Contains(p.CategoryId) && p.CategoryId != pack.CategoryId)
        .Take(1)
        .ToList();

    // ---- Guardrails
    var guardrails = @"
HARD ERROR if a string literal is compared to a numeric *Id* column.
If filtering by a human-readable name, JOIN to the lookup table and filter by its TEXT column.
Bad: WHERE Orders.ShipperId = 'Tuzla Mamul Depo'
Good:
  JOIN Shippers sh ON sh.ShipperId = o.ShipperId
  WHERE sh.CompanyName = 'Tuzla Mamul Depo'
";

    // ---- SQL prompt (bağlamlı soru ile)
    var sqlPrompt = promptBuilder.BuildPromptForPack(
        userQuestion: userQuestionWithContext,
        pack: pack,
        fullSchema: schema,
        adjacentPacks: adjacent,
        sqlDialect: "SQL Server (T-SQL)",
        requireSingleSelect: true,
        forbidDml: true,
        preferAnsi: true
    );

    // izinli tablolar (pack + satellite + komşu core)
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var t in pack.TablesCore) allowed.Add(t);
    foreach (var t in pack.TablesSatellite ?? Array.Empty<string>()) allowed.Add(t);
    foreach (var ap in adjacent)
        foreach (var t in (ap.TablesCore ?? Array.Empty<string>())) allowed.Add(t);

    string finalSql;
    object? data;

    try
    {
        (finalSql, data) = await runner.RunAsync(
            promptForPack: sqlPrompt,
            userQuestion: userQuestionWithContext,
            allowedTables: allowed,
            guardrailsPrompt: guardrails,
            maxRetries: 2,
            ct: ct
        );
    }
    catch (Exception ex)
    {
        await history.AppendAsync(new ChatMessageDto(
            Id: Guid.NewGuid().ToString("N"),
            ThreadId: threadId,
            Role: "assistant",
            Content: $"Error while executing: {ex.Message}",
            Sql: null,
            Error: ex.Message,
            CreatedAtUtc: DateTime.UtcNow
        ), ct);

        return Results.BadRequest(new { error = "Query pipeline failed", detail = ex.Message });
    }

    // ---- SQL sonucundan KISA DOĞAL DİL ÖZET (rows as arrays OR objects)
    static string BuildAssistantContent(object? result, int maxRows = 5, int maxChars = 1200)
    {
        if (result is null) return "No rows.";

        // Try to pull "columns" as list of strings
        static List<string>? TryGetColumns(object obj)
        {
            // reflection friendly
            object? cols = obj.GetType().GetProperty("Columns")?.GetValue(obj)
                         ?? obj.GetType().GetProperty("columns")?.GetValue(obj);

            if (cols is string[] sa) return sa.ToList();
            if (cols is IEnumerable<string> se) return se.ToList();

            if (cols is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in je.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String) list.Add(el.GetString()!);
                return list;
            }

            return null;
        }

        // Try to pull "rows"
        static object? TryGetRows(object obj)
        {
            return obj.GetType().GetProperty("Rows")?.GetValue(obj)
                ?? obj.GetType().GetProperty("rows")?.GetValue(obj);
        }

        var columns = TryGetColumns(result);
        var rowsObj = TryGetRows(result);

        // Normalize rows into list of dictionaries (string -> object?) if possible
        List<Dictionary<string, object?>>? rowDicts = null;
        List<object[]?>? rowArrays = null;

        if (rowsObj is IEnumerable<object[]> asArrays)
        {
            rowArrays = asArrays.ToList();
        }
        else if (rowsObj is IEnumerable enumerable)
        {
            // rows are objects (e.g., Dictionary or anonymous) or JsonElement array
            var tmpDicts = new List<Dictionary<string, object?>>();
            foreach (var item in enumerable)
            {
                if (item is IDictionary dict)
                {
                    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (DictionaryEntry kv in dict)
                        d[kv.Key.ToString()!] = kv.Value;
                    tmpDicts.Add(d);
                }
                else if (item is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in je.EnumerateObject())
                        d[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.ToString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => prop.Value.ToString()
                        };
                    tmpDicts.Add(d);
                }
                else
                {
                    // last resort: reflect anonymous object into dict
                    var props = item?.GetType().GetProperties();
                    if (props is { Length: > 0 })
                    {
                        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in props)
                            d[p.Name] = p.GetValue(item);
                        tmpDicts.Add(d);
                    }
                }
            }
            if (tmpDicts.Count > 0) rowDicts = tmpDicts;
        }
        else if (rowsObj is JsonElement jeRows && jeRows.ValueKind == JsonValueKind.Array)
        {
            var tmp = new List<Dictionary<string, object?>>();
            foreach (var el in jeRows.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                {
                    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in el.EnumerateObject())
                        d[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.ToString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => prop.Value.ToString()
                        };
                    tmp.Add(d);
                }
            }
            if (tmp.Count > 0) rowDicts = tmp;
        }

        // If columns were not provided, try to infer from first row object
        if ((columns == null || columns.Count == 0) && rowDicts is { Count: > 0 })
        {
            columns = rowDicts[0].Keys.ToList();
        }

        var sb = new System.Text.StringBuilder();
        int printed = 0;

        if (rowArrays is { Count: > 0 } && columns is { Count: > 0 })
        {
            foreach (var arr in rowArrays)
            {
                if (arr is null) continue;
                if (printed >= maxRows) break;
                printed++;

                var parts = new List<string>();
                for (int i = 0; i < columns.Count && i < arr.Length; i++)
                {
                    var v = arr[i] is null ? "NULL" : arr[i]?.ToString();
                    parts.Add($"{columns[i]}: {v}");
                }
                sb.AppendLine("• " + string.Join(", ", parts));
            }
        }
        else if (rowDicts is { Count: > 0 })
        {
            foreach (var d in rowDicts)
            {
                if (printed >= maxRows) break;
                printed++;

                IEnumerable<string> keys = columns is { Count: > 0 } ? columns : d.Keys;
                var parts = new List<string>();
                foreach (var k in keys)
                {
                    d.TryGetValue(k, out var val);
                    parts.Add($"{k}: {(val is null ? "NULL" : val.ToString())}");
                }
                sb.AppendLine("• " + string.Join(", ", parts));
            }
        }

        var text = sb.ToString().Trim();
        if (string.IsNullOrEmpty(text)) text = "No rows.";
        if (text.Length > maxChars) text = text[..maxChars] + " ...";
        return text;
    }

    var assistantText = BuildAssistantContent(data);

   
    await history.AppendAsync(new ChatMessageDto(
        Id: Guid.NewGuid().ToString("N"),
        ThreadId: threadId,
        Role: "assistant",
        Content: assistantText,
        Sql: finalSql,
        Error: null,
        CreatedAtUtc: DateTime.UtcNow
    ), ct);

    return Results.Ok(new
    {
        sql = finalSql,
        result = data,
        pack = pack.CategoryId,
        candidates = route.CandidateCategoryIds,
        conversationId = threadId
    });
});

app.Run();
