using Microsoft.AspNetCore.Mvc;
using Api.DTOs;
using Api.Services.Abstractions;
using Api.Services;
using System.Text.Json;
using System.Collections.Generic;
using System.Collections;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class QueryController : ControllerBase
    {
        private readonly ISchemaService _schemaService;
        private readonly ISlicedSchemaCache _cache;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ILlmService _llmService;
        private readonly ISqlValidator _validator;
        private readonly ISqlExecutor _executor;
        private readonly SelfHealingSqlRunner _runner;
        private readonly IChatHistoryStore _history;

        public QueryController(
            ISchemaService schemaService,
            ISlicedSchemaCache cache,
            IPromptBuilder promptBuilder,
            ILlmService llmService,
            ISqlValidator validator,
            ISqlExecutor executor,
            SelfHealingSqlRunner runner,
            IChatHistoryStore history)
        {
            _schemaService = schemaService;
            _cache = cache;
            _promptBuilder = promptBuilder;
            _llmService = llmService;
            _validator = validator;
            _executor = executor;
            _runner = runner;
            _history = history;
        }

        // POST /api/query
        [HttpPost]
        public async Task<IActionResult> PostQuery([FromBody] QueryRequest req, CancellationToken ct)
        {
            if (!_cache.TryGet(out var sliced) || sliced == null)
                return BadRequest(new { error = "No cached sliced schema. Run POST /api/schema/slice/llm first." });

            var threadId = await _history.EnsureThreadAsync(req.ConversationId, ct);
            await _history.AppendAsync(new ChatMessageDto(
                Id: Guid.NewGuid().ToString("N"),
                ThreadId: threadId,
                Role: "user",
                Content: req.Question,
                Sql: null,
                Error: null,
                CreatedAtUtc: DateTime.UtcNow
            ), ct);
            await _history.SetTitleIfEmptyAsync(threadId, req.Question ?? string.Empty, ct);

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

            var allMsgs = await _history.GetMessagesAsync(threadId, ct);
            var convoSnippet = BuildHistorySnippet(allMsgs);

            var userQuestionWithContext = string.IsNullOrWhiteSpace(convoSnippet)
                ? req.Question ?? string.Empty
                : $"Context from previous turns:\n{convoSnippet}\n\nCurrent request: {req.Question ?? string.Empty}";

            var schema = await _schemaService.GetSchemaAsync(ct);
            var routePrompt = _promptBuilder.BuildRoutingPrompt(sliced, userQuestionWithContext);
            var routeJson = await _llmService.GetRawJsonAsync(routePrompt, ct);

            RouteResponseDto? route;
            try
            {
                route = JsonSerializer.Deserialize<RouteResponseDto>(routeJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidOperationException("Failed to parse LLM route JSON.");
            }
            catch (Exception ex)
            {
                await _history.AppendAsync(new ChatMessageDto(
                    Id: Guid.NewGuid().ToString("N"),
                    ThreadId: threadId,
                    Role: "assistant",
                    Content: $"Error while routing: {ex.Message}",
                    Sql: null,
                    Error: $"LLM route JSON parse failed: {ex.Message}",
                    CreatedAtUtc: DateTime.UtcNow
                ), ct);

                return BadRequest(new { error = "LLM route JSON parse failed", detail = ex.Message, raw = routeJson });
            }

            var pack = sliced.Packs.FirstOrDefault(p => p.CategoryId == route.SelectedCategoryId);
            if (pack == null)
            {
                await _history.AppendAsync(new ChatMessageDto(
                    Id: Guid.NewGuid().ToString("N"),
                    ThreadId: threadId,
                    Role: "assistant",
                    Content: "Selected pack not found.",
                    Sql: null,
                    Error: "Selected pack not found in sliced schema",
                    CreatedAtUtc: DateTime.UtcNow
                ), ct);

                return BadRequest(new { error = "Selected pack not found in sliced schema", route });
            }

            var adjacent = sliced.Packs
                .Where(p => route.CandidateCategoryIds.Contains(p.CategoryId) && p.CategoryId != pack.CategoryId)
                .Take(1)
                .ToList();

            var guardrails = @"HARD ERROR if a string literal is compared to a numeric *Id* column.
If filtering by a human-readable name, JOIN to the lookup table and filter by its TEXT column.
Bad: WHERE Orders.ShipperId = 'Tuzla Mamul Depo'
Good:
  JOIN Shippers sh ON sh.ShipperId = o.ShipperId
  WHERE sh.CompanyName = 'Tuzla Mamul Depo'\n";

            var sqlPrompt = _promptBuilder.BuildPromptForPack(
                userQuestion: userQuestionWithContext,
                pack: pack,
                fullSchema: schema,
                adjacentPacks: adjacent,
                sqlDialect: "SQL Server (T-SQL)",
                requireSingleSelect: true,
                forbidDml: true,
                preferAnsi: true
            );

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in pack.TablesCore) allowed.Add(t);
            foreach (var t in pack.TablesSatellite ?? Array.Empty<string>()) allowed.Add(t);
            foreach (var ap in adjacent)
                foreach (var t in (ap.TablesCore ?? Array.Empty<string>())) allowed.Add(t);

            string finalSql;
            object? data;

            try
            {
                (finalSql, data) = await _runner.RunAsync(
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
                await _history.AppendAsync(new ChatMessageDto(
                    Id: Guid.NewGuid().ToString("N"),
                    ThreadId: threadId,
                    Role: "assistant",
                    Content: $"Error while executing: {ex.Message}",
                    Sql: null,
                    Error: ex.Message,
                    CreatedAtUtc: DateTime.UtcNow
                ), ct);

                return BadRequest(new { error = "Query pipeline failed", detail = ex.Message });
            }

            static string BuildAssistantContent(object? result, int maxRows = 5, int maxChars = 1200)
            {
                if (result is null) return "No rows.";
                static List<string>? TryGetColumns(object obj)
                {
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
                static object? TryGetRows(object obj)
                {
                    return obj.GetType().GetProperty("Rows")?.GetValue(obj)
                        ?? obj.GetType().GetProperty("rows")?.GetValue(obj);
                }
                var columns = TryGetColumns(result);
                var rowsObj = TryGetRows(result);
                List<Dictionary<string, object?>>? rowDicts = null;
                List<object[]?>? rowArrays = null;
                if (rowsObj is IEnumerable<object[]> asArrays)
                {
                    rowArrays = asArrays.ToList();
                }
                else if (rowsObj is IEnumerable enumerable)
                {
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
            await _history.AppendAsync(new ChatMessageDto(
                Id: Guid.NewGuid().ToString("N"),
                ThreadId: threadId,
                Role: "assistant",
                Content: assistantText,
                Sql: finalSql,
                Error: null,
                CreatedAtUtc: DateTime.UtcNow
            ), ct);

            return Ok(new
            {
                sql = finalSql,
                result = data,
                pack = pack.CategoryId,
                candidates = route.CandidateCategoryIds,
                conversationId = threadId
            });
        }
    }
}
