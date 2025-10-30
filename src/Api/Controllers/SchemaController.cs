using Microsoft.AspNetCore.Mvc;
using Api.Services.Abstractions;
using Api.DTOs;
using System.Text.Json;

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

        public SchemaController(
            ISchemaService schemaService,
            IPromptBuilder promptBuilder,
            ILlmService llmService,
            ISlicedSchemaCache cache)
        {
            _schemaService = schemaService;
            _promptBuilder = promptBuilder;
            _llmService = llmService;
            _cache = cache;
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
