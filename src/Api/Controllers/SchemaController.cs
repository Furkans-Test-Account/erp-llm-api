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
        private readonly ISlicedSchemaCache _cache;
        private readonly IWebHostEnvironment _env;

        public SchemaController(
            ISchemaService schemaService,
            ISlicedSchemaCache cache,
            IWebHostEnvironment env)
        {
            _schemaService = schemaService;
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


        

        [HttpGet("slice/cached")]
        public IActionResult GetSliceCache()
        {
            if (_cache.TryGetDepartment(out var dept) && dept != null)
            {
                return Ok(new
                {
                    cached = true,
                    kind = "department",
                    schemaName = dept.SchemaName,
                    packCount = dept.Packs?.Count ?? 0,
                    packs = (dept.Packs ?? new List<DepartmentPackDto>())
                        .Select(p => new
                        {
                            p.CategoryId,
                            p.Name,
                            coreCount = p.TablesCore?.Count ?? 0,
                            satCount = p.TablesSatellite?.Count ?? 0
                        })
                        .ToList()
                });
            }
            return NotFound(new { error = "No department slice cached. Upload & activate via /api/schema/dept endpoints." });
        }
    }
}
