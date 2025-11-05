// File: src/Api/Controllers/DepartmentSchemaController.cs
#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq; // LINQ
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/schema/dept")]
    public sealed class DepartmentSchemaController : ControllerBase
    {
        private readonly ILogger<DepartmentSchemaController> _logger;
        private readonly ISchemaService _schemaService;
        private readonly IStrictDepartmentSlicer _deptSlicer;
        private readonly DepartmentSliceOptions _deptOptions;
        private readonly ISlicedSchemaCache _cache; // cache zorunlu
        private readonly IHostEnvironment _env;

        public DepartmentSchemaController(
            ILogger<DepartmentSchemaController> logger,
            ISchemaService schemaService,
            IStrictDepartmentSlicer deptSlicer,
            IOptions<DepartmentSliceOptions> deptOptions,
            IHostEnvironment env,
            ISlicedSchemaCache cache
        )
        {
            _logger = logger;
            _schemaService = schemaService;
            _deptSlicer = deptSlicer;
            _deptOptions = deptOptions.Value;
            _env = env;
            _cache = cache;
        }

        // ------------------------------------------------------------
        // 1) Upload Schema.json, slice et, diske kaydet, CACHE'E SET ET
        // ------------------------------------------------------------
        [HttpPost("slice/upload")]
        public async Task<IActionResult> UploadAndSliceSchemaAsync(
            IFormFile schemaFile,
            [FromQuery] string subFolder = "Slices",
            [FromQuery] bool overwrite = false,
            [FromQuery] bool indented = true,
            CancellationToken ct = default)
        {
            if (schemaFile == null || schemaFile.Length == 0)
                return BadRequest(new { error = "No file uploaded." });

            try
            {
                // 1) JSON oku (UTF-8, BOM'suz önerilir)
                using var ms = new MemoryStream();
                await schemaFile.CopyToAsync(ms, ct);
                var schemaJson = Encoding.UTF8.GetString(ms.ToArray());
                var schema = JsonSerializer.Deserialize<SchemaDto>(schemaJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (schema == null)
                    return BadRequest(new { error = "Failed to deserialize schema JSON." });

                // 2) Slice
                var deptSlice = await _deptSlicer.SliceAsync(schema, _deptOptions, ct);

                // 3) Diske kaydet
                var fileInfo = SaveJsonToAppData(
                    obj: deptSlice,
                    subFolder: subFolder,
                    fileNameWithoutExt: $"{schema.SchemaName}_dept-slice_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                    overwrite: overwrite,
                    indented: indented
                );

                // 4-A) Department-aware cache’e set et (AllowedRefViews/FkEdges/BridgeRefs korunur)
                _cache.SetDepartment(deptSlice);

                // 4-B) ✅ ADAPTER: DepartmentSliceResultDto -> SliceResultDto dönüştür ve legacy cache’e de set et
                TrySetLegacyCacheAdapter(deptSlice);

                return Ok(new
                {
                    saved = true,
                    path = fileInfo.Path,
                    bytes = fileInfo.Bytes,
                    fileName = fileInfo.FileName,
                    schemaName = schema.SchemaName,
                    cached = true
                });
            }
            catch (OperationCanceledException)
            {
                return Problem(statusCode: 499, title: "Request cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process and slice the schema file.");
                return StatusCode(500, new { error = "Failed to process and slice the schema file.", detail = ex.Message });
            }
        }

        // ------------------------------------------------------------
        // 2) (Opsiyonel) Diske kaydedilmiş bir slice dosyasını CACHE'E AKTİF ET
        //    Ör: POST /api/schema/dept/cache/activate?fileName=dbo_dept-slice_20251103_141034.json
        // ------------------------------------------------------------
        [HttpPost("cache/activate")]
        public IActionResult ActivateCacheFromFile(
            [FromQuery] string fileName,
            [FromQuery] string subFolder = "Slices")
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest(new { error = "fileName is required" });

            try
            {
                var folder = Path.Combine(_env.ContentRootPath, "App_Data", subFolder);
                var fullPath = Path.Combine(folder, fileName);
                if (!System.IO.File.Exists(fullPath))
                    return NotFound(new { error = "File not found", fullPath });

                var json = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                // Önce DepartmentSliceResultDto dene
                var dept = JsonSerializer.Deserialize<DepartmentSliceResultDto>(json, opts);
                if (dept != null)
                {
                    _cache.SetDepartment(dept);         // department-aware cache
                    TrySetLegacyCacheAdapter(dept);     // legacy adapter cache
                    return Ok(new { activated = true, from = fullPath, type = "DepartmentSliceResultDto" });
                }

                // Olmadıysa SliceResultDto dene (legacy)
                var legacy = JsonSerializer.Deserialize<SliceResultDto>(json, opts);
                if (legacy != null)
                {
                    _cache.Set(legacy);
                    return Ok(new { activated = true, from = fullPath, type = "SliceResultDto" });
                }

                return BadRequest(new { error = "Unsupported slice JSON shape" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activate cache from file failed");
                return StatusCode(500, new { error = "Activate cache failed", detail = ex.Message });
            }
        }

        // ------------------------------------------------------------
        // 3) Cache'te ne var? Hızlı kontrol
        // ------------------------------------------------------------
        [HttpGet("cache")]
        public IActionResult GetCacheInfo()
        {
            // Önce department-aware cache’i göster
            if (_cache.TryGetDepartment(out var dept) && dept != null)
            {
                var deptPacks = dept.Packs ?? new List<DepartmentPackDto>();
                return Ok(new
                {
                    cached = true,
                    kind = "department",
                    schemaName = dept.SchemaName,
                    packCount = deptPacks.Count,
                    packs = deptPacks
                        .Select(p => new
                        {
                            p.CategoryId,
                            p.Name,
                            coreCount = p.TablesCore?.Count ?? 0,
                            satCount = p.TablesSatellite?.Count ?? 0,
                            lookupCount = p.AllowedRefViews?.Count
                        })
                        .ToList()
                });
            }

            // Legacy varsa onu dön
            if (_cache.TryGet(out var sliced) && sliced != null)
            {
                return Ok(new
                {
                    cached = true,
                    kind = "legacy",
                    schemaName = sliced.SchemaName,
                    packCount = sliced.Packs?.Count ?? 0,
                    packs = (sliced.Packs ?? Array.Empty<PackDto>())
                        .Select(p => new { p.CategoryId, p.Name })
                        .ToList()
                });
            }

            return NotFound(new { cached = false });
        }

        // ------------------------------------------------------------
        // Helper: JSON'u App_Data altına kaydet
        // ------------------------------------------------------------
        private (string Path, long Bytes, string FileName) SaveJsonToAppData(
            object obj,
            string subFolder,
            string? fileNameWithoutExt,
            bool overwrite,
            bool indented)
        {
            var appData = Path.Combine(_env.ContentRootPath, "App_Data", subFolder);
            Directory.CreateDirectory(appData);

            var baseName = string.IsNullOrWhiteSpace(fileNameWithoutExt)
                ? $"dept-slice_{DateTime.UtcNow:yyyyMMdd_HHmmss}"
                : fileNameWithoutExt.Trim();

            var fileName = $"{baseName}.json";
            var fullPath = Path.Combine(appData, fileName);

            if (System.IO.File.Exists(fullPath) && !overwrite)
            {
                fileName = $"{baseName}_{DateTime.UtcNow:ffff}.json";
                fullPath = Path.Combine(appData, fileName);
            }

            var json = JsonSerializer.Serialize(
                obj,
                new JsonSerializerOptions
                {
                    WriteIndented = indented,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

            System.IO.File.WriteAllText(fullPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var info = new FileInfo(fullPath);
            return (fullPath, info.Length, fileName);
        }

        // ------------------------------------------------------------
        // ✅ ADAPTER: DepartmentSliceResultDto -> SliceResultDto + legacy cache set
        //   (Tür uyuşmazlıklarını giderir: List<string> vs string[], List<object> vs List<FkEdgeDto> vb.)
        // ------------------------------------------------------------
        private void TrySetLegacyCacheAdapter(DepartmentSliceResultDto deptSlice)
        {
            try
            {
                var deptPacks = deptSlice.Packs ?? new List<DepartmentPackDto>();

                // DepartmentPackDto -> PackDto dönüşümü: FK/Bridge korunur; Summary/Grain boş bırakılır
                var packs = new List<PackDto>(deptPacks.Count);
                foreach (var p in deptPacks)
                {
                    var tablesCore = p.TablesCore ?? new List<string>();
                    var tablesSatellite = p.TablesSatellite ?? new List<string>();

                    // FkEdges ve BridgeRefs projeden projeye farklı tiplerde gelebilir -> normalize et
                    var fkEdges = CoerceFkEdges(GetEnumerableObject(GetProp(p, "FkEdges")));
                    var bridgeRefs = CoerceBridgeRefs(GetEnumerableObject(GetProp(p, "BridgeRefs")));

                    packs.Add(new PackDto(
                        CategoryId: p.CategoryId,
                        Name: p.Name ?? p.CategoryId,
                        TablesCore: tablesCore,                // IReadOnlyList<string>
                        TablesSatellite: tablesSatellite,      // IReadOnlyList<string>
                        FkEdges: fkEdges,                      // List<FkEdgeDto>
                        BridgeRefs: bridgeRefs,                // IReadOnlyList<BridgeRefDto>
                        Summary: string.Empty,
                        Grain: string.Empty
                    ));
                }

                var legacy = new SliceResultDto(
                    SchemaName: deptSlice.SchemaName,
                    Packs: packs
                );

                _cache.Set(legacy);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Legacy cache adapter failed; skipping legacy cache write.");
            }
        }

        // ------------------------------------------------------------
        // Adapter helpers (reflection ile gevşek tipleri normalize eder)
        // ------------------------------------------------------------
        private static object? GetProp(object o, string name)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return p?.GetValue(o);
        }

        private static IEnumerable<object>? GetEnumerableObject(object? value)
        {
            if (value is null) return null;
            if (value is IEnumerable<object> eo) return eo;
            if (value is IEnumerable en)
            {
                var list = new List<object>();
                foreach (var x in en) if (x != null) list.Add(x);
                return list;
            }
            return null;
        }

        private static List<FkEdgeDto> CoerceFkEdges(IEnumerable<object>? seq)
        {
            var list = new List<FkEdgeDto>();
            if (seq == null) return list;

            foreach (var o in seq)
            {
                if (o is FkEdgeDto fk)
                {
                    if (!string.IsNullOrWhiteSpace(fk.From) && !string.IsNullOrWhiteSpace(fk.To))
                        list.Add(fk);
                    continue;
                }

                var t = o.GetType();
                var from = t.GetProperty("From", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o)?.ToString();
                var to = t.GetProperty("To", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o)?.ToString();

                if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
                    list.Add(new FkEdgeDto(from!, to!));
            }
            return list;
        }

        private static List<BridgeRefDto> CoerceBridgeRefs(IEnumerable<object>? seq)
        {
            var list = new List<BridgeRefDto>();
            if (seq == null) return list;

            foreach (var o in seq)
            {
                if (o is BridgeRefDto br)
                {
                    list.Add(br);
                    continue;
                }

                var t = o.GetType();
                var toCategory = t.GetProperty("ToCategory", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o)?.ToString() ?? string.Empty;
                var viaObj = t.GetProperty("ViaTables", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o);
                var via = ToStringList(GetEnumerableObject(viaObj));
                list.Add(new BridgeRefDto(toCategory, via));
            }
            return list;
        }

        private static IReadOnlyList<string> ToStringList(IEnumerable<object>? seq)
        {
            if (seq == null) return new List<string>();
            var list = new List<string>();
            foreach (var o in seq)
            {
                var s = o?.ToString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            return list;
        }
        [HttpGet("cache/pack")]
        public IActionResult GetDeptPack([FromQuery] string categoryId)
        {
            if (!_cache.TryGetDepartment(out var dept) || dept is null)
                return NotFound("No department slice");

            var p = dept.Packs.FirstOrDefault(x =>
                string.Equals(x.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));

            if (p is null) return NotFound(new { error = "Pack not found", categoryId });

            return Ok(new
            {
                p.CategoryId,
                p.Name,
                core = p.TablesCore,
                sat = p.TablesSatellite,
                fkEdges = p.FkEdges,           // FK’ler burada görünmeli
                bridges = p.BridgeRefs,        // Köprü referansları
                lookups = p.AllowedRefViews    // Sözlük/görünüm listesi
            });
        }

    }

}
