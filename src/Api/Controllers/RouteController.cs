using Microsoft.AspNetCore.Mvc;
using Api.DTOs;
using Api.Services.Abstractions;
using System.Text.Json;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RouteController : ControllerBase
    {
        private readonly ISlicedSchemaCache _cache;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ILlmService _llmService;

        public RouteController(ISlicedSchemaCache cache, IPromptBuilder promptBuilder, ILlmService llmService)
        {
            _cache = cache;
            _promptBuilder = promptBuilder;
            _llmService = llmService;
        }

        // POST /api/route/llm
        [HttpPost("llm")]
        public async Task<IActionResult> RouteWithLlm([FromBody] RouteRequestDto req, CancellationToken ct)
        {
            if (!_cache.TryGet(out var sliced) || sliced == null)
                return BadRequest(new { error = "No cached sliced schema. Run /api/schema/slice/llm first." });

            var prompt = _promptBuilder.BuildRoutingPrompt(sliced, req.Question);
            var json = await _llmService.GetRawJsonAsync(prompt, ct);

            RouteResponseDto? routed;
            try
            {
                routed = JsonSerializer.Deserialize<RouteResponseDto>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new InvalidOperationException("Failed to parse LLM routing JSON.");
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "LLM routing JSON parse failed", detail = ex.Message, raw = json });
            }

            return Ok(routed);
        }
    }
}
