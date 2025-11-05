// File: src/Api/Services/LlmService.cs
#nullable enable
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using Api.Services.Abstractions;

namespace Api.Services
{
    public class LlmService : ILlmService
    {
        private readonly HttpClient _client;
        private readonly IConfiguration _cfg;
        private readonly ILlmAudit _audit;

        public LlmService(HttpClient client, IConfiguration cfg, ILlmAudit audit)
        {
            _client = client;
            _cfg = cfg;
            _audit = audit;
        }

        // ======================================================
        // 1) First SQL generation (pack-scoped)
        // ======================================================
        public async Task<string> GetSqlAsync(string prompt, CancellationToken ct)
        {
            var apiKey = GetApiKeyOrThrow();
            var (model, temperature, maxTokens) = GetModelConfig();

            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var system = string.Join(' ', new[]
            {
                "You are an expert SQL generator for Microsoft SQL Server (T-SQL).",
                "Return ONLY one safe SELECT statement (no comments, no markdown).",
                "Do NOT use DML/DDL (no CREATE/ALTER/DROP/INSERT/UPDATE/DELETE/TRUNCATE).",
                "Use explicit JOINs. Use TOP N instead of LIMIT.",
                "If any table/column/alias equals a reserved word (e.g., USER, ORDER, GROUP, KEY, ROLE, VALUE, VALUES, TABLE, INDEX), wrap it in [brackets].",
                "Output MUST start with SELECT."
            });

            var body = new ChatCompletionsRequest
            {
                model = model,
                temperature = temperature,
                max_tokens = maxTokens,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = system },
                    new ChatMessage { role = "user", content = prompt }
                }
            };

            // AUDIT
            if (_audit.Enabled)
            {
                var reqJson = JsonSerializer.Serialize(body, SerializerOptions);
                await _audit.SaveAsync("pack-sql", prompt, reqJson, ct);
            }

            var sql = await SendAndExtractSqlAsync(body, ct);
            return SqlServerNormalize(sql);
        }

        // ======================================================
        // 2) Self-heal (refine), max 2 tries external orchestrator tarafından çağrılır
        // ======================================================
        public async Task<string> RefineSqlAsync(
            string userQuestion,
            string previousSql,
            string errorMessage,
            IEnumerable<string> allowedTables,
            string guardrailsPrompt,
            CancellationToken ct)
        {
            var apiKey = GetApiKeyOrThrow();
            var (model, temperature, maxTokens) = GetModelConfig();

            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var system = string.Join(' ', new[]
            {
                "You are an expert SQL fixer for Microsoft SQL Server (T-SQL).",
                "You will receive a failed SQL, an error message, and constraints.",
                "Return ONLY one corrected SELECT statement (no comments, no markdown).",
                "No DML/DDL (no CREATE/ALTER/DROP/INSERT/UPDATE/DELETE/TRUNCATE).",
                "Use explicit JOINs; prefer TOP instead of LIMIT; escape reserved identifiers using [brackets].",
                "Never compare string literals to numeric ID columns."
            });

            var refinePrompt = BuildRefinePrompt(userQuestion, previousSql, errorMessage, allowedTables, guardrailsPrompt);

            var body = new ChatCompletionsRequest
            {
                model = model,
                temperature = temperature,
                max_tokens = maxTokens,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = system },
                    new ChatMessage { role = "user", content = refinePrompt }
                }
            };

            // AUDIT
            if (_audit.Enabled)
            {
                var reqJson = JsonSerializer.Serialize(body, SerializerOptions);
                await _audit.SaveAsync("refine", refinePrompt, reqJson, ct);
            }

            var sql = await SendAndExtractSqlAsync(body, ct);
            return SqlServerNormalize(sql);
        }

        // ======================================================
        // 3) JSON (routing/slicing vs.) – strict JSON döndür
        // ======================================================
        public async Task<string> GetRawJsonAsync(string prompt, CancellationToken ct)
        {
            var apiKey = GetApiKeyOrThrow();
            var (model, temperature, maxTokens) = GetModelConfig();

            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var system = "You output ONLY strict, valid JSON.";

            var framedPrompt = new StringBuilder()
                .AppendLine(prompt)
                .AppendLine()
                .AppendLine("Return ONLY valid JSON (no comments, no trailing commas).")
                .AppendLine("Wrap the JSON exactly between these lines:")
                .AppendLine("###JSON_START###")
                .AppendLine("{ ... }")
                .AppendLine("###JSON_END###")
                .ToString();

            var body = new
            {
                model,
                temperature,
                max_tokens = Math.Max(maxTokens, 4000),
                response_format = new { type = "json_object" },
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = framedPrompt }
                }
            };

            var reqJson = JsonSerializer.Serialize(body, SerializerOptions);

            // AUDIT
            if (_audit.Enabled)
            {
                await _audit.SaveAsync("route", framedPrompt, reqJson, ct);
            }

            var endpoint = BuildUri(); // absolute URI (BaseUrl + ChatPath)
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
            };

            using var resp = await _client.SendAsync(req, ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"OpenAI HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {payload}");

            var parsed = JsonSerializer.Deserialize<ChatCompletionsResponse>(payload, SerializerOptions)
                         ?? throw new InvalidOperationException("Empty or invalid OpenAI response.");

            var content = parsed.choices?.FirstOrDefault()?.message?.content?.Trim()
                          ?? throw new InvalidOperationException("OpenAI returned empty content.");

            var between = ExtractBetween(content, "###JSON_START###", "###JSON_END###");
            var json = string.IsNullOrWhiteSpace(between) ? content : between;

            if (TryParseJson(json, out _)) return json;

            var repaired = TryRepairJson(json);
            if (TryParseJson(repaired, out _)) return repaired;

            throw new InvalidOperationException("LLM JSON is invalid and could not be repaired.");
        }

        // ======================================================
        // Helpers
        // ======================================================

        private string GetApiKeyOrThrow()
        {
            return Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                   ?? _cfg["OpenAI:ApiKey"]
                   ?? throw new InvalidOperationException("OpenAI API key is missing. Set OPENAI_API_KEY or OpenAI:ApiKey.");
        }

        private (string model, double temperature, int maxTokens) GetModelConfig()
        {
            var model = _cfg["OpenAI:Model"] ?? "gpt-4o-mini";
            var temperature = _cfg.GetValue<double?>("OpenAI:Temperature") ?? 0.0;
            var maxTokens = _cfg.GetValue<int?>("OpenAI:MaxTokens") ?? 800;
            return (model, temperature, maxTokens);
        }

        /// <summary>
        /// OpenAI:BaseUrl (ABSOLUTE, e.g. https://api.openai.com/) + OpenAI:ChatPath (default v1/chat/completions)
        /// </summary>
        private Uri BuildUri()
        {
            var baseUrl = _cfg["OpenAI:BaseUrl"] ?? "https://api.openai.com/";
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                throw new InvalidOperationException("OpenAI:BaseUrl must be an absolute URI, e.g. https://api.openai.com/");

            var path = _cfg["OpenAI:ChatPath"] ?? "v1/chat/completions";
            return new Uri(baseUri, path);
        }

        private async Task<string> SendAndExtractSqlAsync(ChatCompletionsRequest body, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(body, SerializerOptions);
            var endpoint = BuildUri();

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var resp = await _client.SendAsync(req, ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"OpenAI HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {payload}");
            }

            var parsed = JsonSerializer.Deserialize<ChatCompletionsResponse>(payload, SerializerOptions)
                        ?? throw new InvalidOperationException("Empty or invalid OpenAI response.");

            var content = parsed.choices?.FirstOrDefault()?.message?.content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("OpenAI returned empty content.");

            var sql = ExtractSql(content);
            if (string.IsNullOrWhiteSpace(sql))
                throw new InvalidOperationException("Failed to extract a valid SELECT statement from the model response.");

            return sql;
        }

        private string BuildRefinePrompt(
            string userQuestion,
            string previousSql,
            string errorMessage,
            IEnumerable<string> allowedTables,
            string guardrailsPrompt)
        {
            var allowed = string.Join(", ", allowedTables);

            var sb = new StringBuilder();
            sb.AppendLine("The previous SQL failed. Fix it and return ONE single SELECT only (no comments, no markdown).");
            sb.AppendLine();
            sb.AppendLine("User question:");
            sb.AppendLine(userQuestion);
            sb.AppendLine();
            sb.AppendLine("Previous SQL (failed):");
            sb.AppendLine(previousSql);
            sb.AppendLine();
            sb.AppendLine("Error / validator feedback:");
            sb.AppendLine(errorMessage);
            sb.AppendLine();
            sb.AppendLine("Constraints:");
            sb.AppendLine($"- Only use these tables: {allowed}");
            sb.AppendLine("- No DML/DDL; SELECT only. Use explicit JOINs.");
            sb.AppendLine("- If filtering by a human-readable name, JOIN to the lookup table and filter by its TEXT column; NEVER compare a string literal to a numeric *Id* column.");
            sb.AppendLine("- Use TOP instead of LIMIT; escape reserved identifiers with [brackets].");
            if (!string.IsNullOrWhiteSpace(guardrailsPrompt))
            {
                sb.AppendLine();
                sb.AppendLine("Additional guardrails:");
                sb.AppendLine(guardrailsPrompt);
            }
            sb.AppendLine();
            sb.AppendLine("Return the corrected single SELECT now:");
            return sb.ToString();
        }

        // ---------------- SQL extraction & normalization ----------------

        private static string ExtractSql(string text)
        {
            var fence = Regex.Match(text, "```sql\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);
            if (fence.Success) text = fence.Groups[1].Value;

            text = text.Replace("```", "").Trim();

            var idx = text.IndexOf("select", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) text = text.Substring(idx);

            var semi = text.IndexOf(';');
            if (semi > 0) text = text.Substring(0, semi + 1);

            text = text.Trim();

            if (!text.StartsWith("select", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return text;
        }

        /// <summary>
        /// T-SQL normalize:
        /// - LIMIT N -> TOP N
        /// - quoted identifiers -> [brackets]
        /// - FROM/JOIN sonrası reserved tablo isimlerini [bracket]
        /// - sonda ';'
        /// </summary>
        private static string SqlServerNormalize(string sql)
        {
            var s = sql.Trim();

            // backtick/double-quote → [brackets]
            s = Regex.Replace(s, @"`([^`]+)`", @"[$1]");
            s = Regex.Replace(s, @"""([^""]+)""", @"[$1]");

            // LIMIT N → TOP N
            var limitMatch = Regex.Match(s, @"\blimit\s+(\d+)\b", RegexOptions.IgnoreCase);
            if (limitMatch.Success && !Regex.IsMatch(s, @"\bselect\s+top\s+\d+", RegexOptions.IgnoreCase))
            {
                var n = limitMatch.Groups[1].Value;
                s = Regex.Replace(s, @"\blimit\s+\d+\b", "", RegexOptions.IgnoreCase).Trim();
                s = Regex.Replace(s, @"\bselect\b", $"SELECT TOP {n}", RegexOptions.IgnoreCase);
            }

            // RESERVED table names
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "USER","ORDER","GROUP","KEY","ROLE","VALUE","VALUES","TABLE","INDEX"
            };

            s = Regex.Replace(s, @"(?<=\bFROM\s+)(\w+)", m =>
            {
                var name = m.Groups[1].Value;
                return reserved.Contains(name) ? $"[{name}]" : name;
            }, RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"(?<=\bJOIN\s+)(\w+)", m =>
            {
                var name = m.Groups[1].Value;
                return reserved.Contains(name) ? $"[{name}]" : name;
            }, RegexOptions.IgnoreCase);

            if (!s.EndsWith(";")) s += ";";
            return s;
        }

        // ---------------- JSON helpers ----------------

        private static string ExtractBetween(string s, string start, string end)
        {
            var i = s.IndexOf(start, StringComparison.Ordinal);
            if (i < 0) return string.Empty;
            i += start.Length;
            var j = s.IndexOf(end, i, StringComparison.Ordinal);
            if (j < 0) return s.Substring(i).Trim();
            return s.Substring(i, j - i).Trim();
        }

        private static bool TryParseJson(string s, out JsonDocument? doc)
        {
            try { doc = JsonDocument.Parse(s); return true; }
            catch { doc = null; return false; }
        }

        private static string TryRepairJson(string s)
        {
            s = s.Replace("```json", "").Replace("```", "").Trim();

            int first = s.IndexOf('{');
            int last = s.LastIndexOf('}');
            if (first >= 0)
            {
                if (last < first) last = s.Length - 1;
                s = s.Substring(first, last - first + 1);
            }

            int openCurly = s.Count(c => c == '{');
            int closeCurly = s.Count(c => c == '}');
            int openSquare = s.Count(c => c == '[');
            int closeSquare = s.Count(c => c == ']');

            if (openSquare > closeSquare) s += new string(']', openSquare - closeSquare);
            if (openCurly > closeCurly) s += new string('}', openCurly - closeCurly);

            s = Regex.Replace(s, @",\s*(\]|\})", "$1");

            return s.Trim();
        }

        // ---------- OpenAI DTOs ----------

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private class ChatCompletionsRequest
        {
            public string model { get; set; } = default!;
            public double? temperature { get; set; }
            public int? max_tokens { get; set; }
            public ChatMessage[] messages { get; set; } = Array.Empty<ChatMessage>();
        }

        private class ChatMessage
        {
            public string role { get; set; } = default!;
            public string content { get; set; } = default!;
        }

        private class ChatCompletionsResponse
        {
            public Choice[]? choices { get; set; }
        }

        private class Choice
        {
            public ChatMessage? message { get; set; }
            public string? finish_reason { get; set; }
        }
    }
}
