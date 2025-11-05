// File: src/Api/Controllers/RouteController.cs
#nullable enable
using Microsoft.AspNetCore.Mvc;
using Api.Services.Abstractions;
using Api.DTOs;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/route")]
    public sealed class RouteController : ControllerBase
    {
        private readonly ILogger<RouteController> _logger;
        private readonly ISlicedSchemaCache _cache;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ISchemaService _schema;
        private readonly ILlmService _llm;

        public RouteController(
            ILogger<RouteController> logger,
            ISlicedSchemaCache cache,
            IPromptBuilder promptBuilder,
            ISchemaService schema,
            ILlmService llm)
        {
            _logger = logger;
            _cache = cache;
            _promptBuilder = promptBuilder;
            _schema = schema;
            _llm = llm;
        }

        [HttpPost("llm")]
        public async Task<IActionResult> RouteWithLlm([FromBody] RouteRequestDto req, CancellationToken ct)
        {
            if (!_cache.TryGetDepartment(out var dept) || dept == null)
                return BadRequest(new { error = "Department slice not loaded. Upload & activate first." });

            var fullSchema = await _schema.GetSchemaAsync(ct);

            // 1) Basit heuristics veya mevcut router ile en uygun categoryId’yi bul
            // (Mevcudu koruyalım; istersen LLM routing prompt’unu da kullanabilirsin.)
            var best = dept.Packs.FirstOrDefault()
                       ?? throw new InvalidOperationException("No packs in department slice");

            // 2) İstersen adjacent category ids belirle (opsiyonel)
            var adjacent = Array.Empty<string>();

            // 3) ✅ Department overload ile prompt üret (AllowedRefViews + FkEdges/BridgeRefs görünür!)
            var prompt = _promptBuilder.BuildPromptForCategory(
                userQuestion: req.Question,
                deptSlice: dept,
                categoryId: best.CategoryId,
                fullSchema: fullSchema,
                adjacentCategoryIds: adjacent.ToList()
            );

            // Debug amaçlı prompt’u diske yazmak istersen:
            // await _llm.SaveDebugPromptAsync("route-sql", prompt, ct);

            // 4) SQL al
            var sql = await _llm.GetSqlAsync(prompt, ct);

            return Ok(new
            {
                selected = best.CategoryId,
                packName = best.Name,
                sql
            });
        }
    }
}
