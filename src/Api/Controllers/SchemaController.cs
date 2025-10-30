using Microsoft.AspNetCore.Mvc;
using Api.Services.Abstractions;
using Api.DTOs;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using System.Text;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SchemaController : ControllerBase
    {
        private readonly ISchemaService _schemaService;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ILlmService _llmService;
        private readonly ISlicedSchemaCache _cache;
        private readonly IWebHostEnvironment _env;

        public SchemaController(
            ISchemaService schemaService,
            IPromptBuilder promptBuilder,
            ILlmService llmService,
            ISlicedSchemaCache cache,
            IWebHostEnvironment env)
        {
            _schemaService = schemaService;
            _promptBuilder = promptBuilder;
            _llmService = llmService;
            _cache = cache;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> GetSchema(CancellationToken ct)
        {
            try
            {
                var schema = await _schemaService.GetSchemaAsync(ct);
                return Ok(schema);
            }
            catch (Exception ex)
            {
                return Problem(title: "/api/schema failed", detail: ex.Message);
            }
        }

        [HttpPost("save-json")]
        public async Task<IActionResult> SaveSchemaJson(CancellationToken ct)
        {
            try
            {
                var schema = await _schemaService.GetSchemaAsync(ct);

                // serialize as proper JSON
                var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                var appData = Path.Combine(_env.ContentRootPath, "App_Data");
                Directory.CreateDirectory(appData);

               
                var path = Path.Combine(appData, "Schema.json");

                if (System.IO.File.Exists(path))
                {
                    var info = new FileInfo(path);
                    return Ok(new
                    {
                        created = false,
                        exists = true,
                        path,
                        bytes = info.Length
                    });
                }

                await System.IO.File.WriteAllTextAsync(path, json, Encoding.UTF8, ct);

                var createdInfo = new FileInfo(path);
                return Ok(new
                {
                    created = true,
                    path,
                    bytes = createdInfo.Length
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error = "Failed to save schema",
                    detail = ex.Message
                });
            }
        }


        [HttpPost("slice/llm")]
        public async Task<IActionResult> SliceWithLlm(CancellationToken ct)
        {
            var schema = await _schemaService.GetSchemaAsync(ct);
            var prompt = _promptBuilder.BuildSchemaSlicingPrompt(schema);
            var json = await _llmService.GetRawJsonAsync(prompt, ct);

            SliceResultDto? sliced;
            try
            {
                sliced = JsonSerializer.Deserialize<SliceResultDto>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new InvalidOperationException("Failed to parse LLM sliced schema JSON.");
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "JSON parse failed", detail = ex.Message, raw = json });
            }

            _cache.Set(sliced);
            return Ok(new { cached = true, packs = sliced.Packs.Count });
        }

        [HttpPost("slice/chunked")]
        public async Task<IActionResult> SliceChunkedWithLlm(CancellationToken ct)
        {
            var schema = await _schemaService.GetSchemaAsync(ct);
            var groupedTables = GroupTablesByPrefix(schema.Tables);
            var allPacks = new List<PackDto>();
            const int maxTablesPerChunk = 40; // Tune as needed for your LLM/context

            foreach (var group in groupedTables)
            {
                var tableChunks = ChunkTables(group.Value, maxTablesPerChunk);
                int chunkIndex = 0;
                foreach (var chunk in tableChunks)
                {
                    var groupSchema = new SchemaDto(
                        SchemaName: $"Group_{group.Key}_{chunkIndex}",
                        Tables: chunk
                    );
                    chunkIndex++;

                    var prompt = _promptBuilder.BuildSchemaSlicingPrompt(groupSchema);
                    // Optionally: Add a prompt length/token estimation here as a safeguard

                    SliceResultDto? result = null;
                    try
                    {
                        var json = await _llmService.GetRawJsonAsync(prompt, ct);
                        result = JsonSerializer.Deserialize<SliceResultDto>(json, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (result != null && result.Packs != null)
                        allPacks.AddRange(result.Packs);
                }
            }

            var aggregated = new SliceResultDto(
                schema.SchemaName + "_Chunked",
                allPacks
            );
            _cache.Set(aggregated);
            return Ok(new { cached = true, packs = allPacks.Count });
        }

        // Helper to group tables by prefix (post-dbo stripping)
        private static Dictionary<string, List<TableDto>> GroupTablesByPrefix(IReadOnlyList<TableDto> tables)
        {
            var dict = new Dictionary<string, List<TableDto>>();
            foreach (var t in tables)
            {
                var name = t.Name.StartsWith("dbo.") ? t.Name.Substring(4) : t.Name;
                var prefix = name.Length >= 2 ? name.Substring(0, 2).ToLowerInvariant() : name.ToLowerInvariant();
                if (!dict.TryGetValue(prefix, out var list))
                {
                    list = new List<TableDto>();
                    dict[prefix] = list;
                }
                list.Add(t);
            }
            return dict;
        }

        // Break a table list into smaller chunks (for big groups)
        private static List<List<TableDto>> ChunkTables(List<TableDto> tables, int maxPerChunk)
        {
            var chunks = new List<List<TableDto>>();
            for (int i = 0; i < tables.Count; i += maxPerChunk)
            {
                chunks.Add(tables.GetRange(i, Math.Min(maxPerChunk, tables.Count - i)));
            }
            return chunks;
        }

        [HttpGet("slice/cached")]
        public IActionResult GetSliceCache()
        {
            if (_cache.TryGet(out var s) && s != null)
            {
                return Ok(s);
            }
            return NotFound(new { error = "No cached sliced schema. Run POST /api/schema/slice/llm first." });
        }
    }
}
