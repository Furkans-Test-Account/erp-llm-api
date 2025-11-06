// File: src/Api/Controllers/RouteController.cs
#nullable enable
using Microsoft.AspNetCore.Mvc;
using Api.Services.Abstractions;
using Api.DTOs;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Reflection;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/route")]
    public sealed class RouteController : ControllerBase
    {
        private readonly ILogger<RouteController> _logger;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ILlmService _llm;

        public RouteController(
            ILogger<RouteController> logger,
            IPromptBuilder promptBuilder,
            ILlmService llm)
        {
            _logger = logger;
            _promptBuilder = promptBuilder;
            _llm = llm;
        }

        // Removed routing helpers & slice coercion utilities

        

        [HttpPost("llm")]
        public async Task<IActionResult> RouteWithLlm([FromBody] RouteRequestDto req, CancellationToken ct)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Question))
                return BadRequest(new { error = "Question is required" });

            // Template-based prompt: ignore routing and schema slicing.
            var emptyPack = new PackDto(
                CategoryId: string.Empty,
                Name: string.Empty,
                TablesCore: new List<string>(),
                TablesSatellite: new List<string>(),
                FkEdges: new List<FkEdgeDto>(),
                BridgeRefs: new List<BridgeRefDto>(),
                Summary: string.Empty,
                Grain: string.Empty
            );
            var emptySchema = new SchemaDto("dbo", new List<TableDto>());

            var prompt = _promptBuilder.BuildPromptForPack(
                req.Question,
                emptyPack,
                emptySchema,
                null,
                "SQL Server (T-SQL)",
                true,
                true,
                true
            );

            if (string.IsNullOrWhiteSpace(prompt))
                return StatusCode(500, new { error = "Prompt generation returned empty string." });

            var sql = await _llm.GetSqlAsync(prompt, ct);
            return Ok(new { sql });
        }
    }
}
