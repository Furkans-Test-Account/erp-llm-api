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

        // --------- Keyword sets ----------
        private static readonly string[] DepotTerms = {
            "depo","ambar","stok","raf","barkod","sayım","sayim",
            "ürün çıkışı","urun cikisi","ürün girişi","urun girisi",
            "depolar arası","depolar arasi"
        };

        private static readonly string[] LogisticsTerms = {
            "sevkiyat","sevk","irsaliye","nakliye","taşıyıcı","tasiyici",
            "kargo","dispatcher","dispatch","shipment","transfer","taşıma","tasima","taşınan","tasinan"
        };

        private static readonly string[] HrTerms = {
            "ik","i̇k","insan kaynak","personel","maaş","ucret","ücret","bordro",
            "işe giriş","ise giris","sgk","izin","mesai","performans","mülakat","mulakat","payroll"
        };

        private static readonly string[] SalesFinanceTerms = {
            "satış","satis","fatura","ciro","gelir","hasılat","hasilat",
            "tahsilat","alacak","borç","borc","pos","kasa fişi","kasa fis"
        };

        private static readonly string[] CariTerms = {
            "cari","müşteri","musteri","tedarikçi","tedarikci","supplier","vendor","bakiye","ekstre","hesap ekstresi"
        };

        private static readonly string[] OrgTerms = {
            "mağaza","magaza","ofis","şube","sube","kasa","ekip","departman","organizasyon","store","office","team"
        };

        private static readonly string[] SozlukParamTerms = {
            "kod","sözlük","sozluk","tanım","tanim","referans","parametre","ayar","konfig","config","dil","language"
        };

        // --------- Helpers (routing score) ----------
        private static int CountKeywordHits(string text, IEnumerable<string> terms)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var t = text.ToLowerInvariant();
            int score = 0;
            foreach (var raw in terms)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var k = Regex.Escape(raw.ToLowerInvariant());
                var pattern = $@"(?<!\w){k}(?!\w)";
                score += Regex.Matches(t, pattern, RegexOptions.CultureInvariant).Count;
            }
            return score;
        }

        private static int ScoreForCategory(string catId, string question) =>
            catId.ToLowerInvariant() switch
            {
                "depo_stok" => CountKeywordHits(question, DepotTerms),
                "lojistik" => CountKeywordHits(question, LogisticsTerms),
                "insan_kaynaklari" => CountKeywordHits(question, HrTerms),
                "satis_finans" => CountKeywordHits(question, SalesFinanceTerms),
                "cari_hesap" => CountKeywordHits(question, CariTerms),
                "organizasyon" => CountKeywordHits(question, OrgTerms),
                "sistem_parametre" => CountKeywordHits(question, SozlukParamTerms),
                "sozluk_kod" => CountKeywordHits(question, SozlukParamTerms),
                _ => 0
            };

        private static bool LooksLikeHR(string q) => CountKeywordHits(q, HrTerms) > 0;
        private static bool LooksLikeLogistics(string q) => CountKeywordHits(q, LogisticsTerms) > 0;

        // --------- Lightweight reflection helpers (to coerce DepartmentPackDto -> PackDto) ----------
        private static object? GetProp(object o, string name)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return p?.GetValue(o);
        }

        private static IEnumerable<object>? AsObjects(object? v)
        {
            if (v is null) return null;
            if (v is IEnumerable<object> eo) return eo;
            if (v is System.Collections.IEnumerable en)
            {
                var list = new List<object>();
                foreach (var x in en) if (x != null) list.Add(x);
                return list;
            }
            return null;
        }

        private static List<FkEdgeDto> CoerceFkEdges(object? seqVal)
        {
            var list = new List<FkEdgeDto>();
            var seq = AsObjects(seqVal);
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

        private static List<BridgeRefDto> CoerceBridgeRefs(object? seqVal)
        {
            var list = new List<BridgeRefDto>();
            var seq = AsObjects(seqVal);
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
                var via = AsObjects(viaObj)?.Select(x => x.ToString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
                list.Add(new BridgeRefDto(toCategory, via));
            }
            return list;
        }

        // DepartmentPackDto (unknown at compile-time here) -> PackDto
        private static PackDto CoerceToPackDto(object deptPack)
        {
            var categoryId = GetProp(deptPack, "CategoryId")?.ToString() ?? "";
            var name = GetProp(deptPack, "Name")?.ToString() ?? categoryId;

            var core = (AsObjects(GetProp(deptPack, "TablesCore")) ?? Array.Empty<object>()).Select(o => o.ToString()!).ToList();
            var sat = (AsObjects(GetProp(deptPack, "TablesSatellite")) ?? Array.Empty<object>()).Select(o => o.ToString()!).ToList();

            var fk = CoerceFkEdges(GetProp(deptPack, "FkEdges"));
            var br = CoerceBridgeRefs(GetProp(deptPack, "BridgeRefs"));

            return new PackDto(
                categoryId,
                name,
                core,
                sat,
                fk,
                br,
                Summary: string.Empty,
                Grain: string.Empty
            );
        }

        private sealed record RoutePick(string selectedCategoryId, List<string> candidateCategoryIds, string reason);

        [HttpPost("llm")]
        public async Task<IActionResult> RouteWithLlm([FromBody] RouteRequestDto req, CancellationToken ct)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Question))
                return BadRequest(new { error = "Question is required" });

            if (!_cache.TryGetDepartment(out var dept) || dept == null)
                return BadRequest(new { error = "Department slice not loaded. Upload & activate first." });

            // dept.Packs: DepartmentPackDto list (runtime type) — görünürlüğü filtrele
            var visibleDeptPacks = dept.Packs
                .Where(p =>
                {
                    var cat = (GetProp(p, "CategoryId")?.ToString() ?? "");
                    return !cat.Equals("insan_kaynaklari", StringComparison.OrdinalIgnoreCase) || LooksLikeHR(req.Question);
                })
                .ToList();

            if (visibleDeptPacks.Count == 0)
                return BadRequest(new { error = "No available packs after filtering. (Check department slice.)" });

            // Skorla
            var scored = visibleDeptPacks
                .Select(p =>
                {
                    var cat = GetProp(p, "CategoryId")?.ToString() ?? "";
                    return new { DeptPack = p, Cat = cat, Score = ScoreForCategory(cat, req.Question) };
                })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Cat, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var top = scored.First();
            var second = scored.Skip(1).FirstOrDefault();
            var margin = top.Score - (second?.Score ?? 0);

            string selectedCategoryId;
            if (LooksLikeLogistics(req.Question) &&
                scored.Any(s => s.Cat.Equals("lojistik", StringComparison.OrdinalIgnoreCase)))
            {
                selectedCategoryId = "lojistik";
            }
            else if (top.Score >= 2 && margin >= 1)
            {
                selectedCategoryId = top.Cat;
            }
            else
            {
                // LLM routing — önce görünür paketleri PackDto'ya coerçe et
                var visiblePacksAsPackDto = visibleDeptPacks.Select(CoerceToPackDto).ToList();
                var filtered = new SliceResultDto(dept.SchemaName, visiblePacksAsPackDto);

                var routingPrompt = _promptBuilder.BuildRoutingPrompt(filtered, req.Question, topK: 3);

                RoutePick? llmPick = null;
                try
                {
                    var json = await _llm.GetRawJsonAsync(routingPrompt, ct);
                    llmPick = JsonSerializer.Deserialize<RoutePick>(json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LLM routing parse failed; falling back to rule top.");
                }

                selectedCategoryId =
                    (!string.IsNullOrWhiteSpace(llmPick?.selectedCategoryId) &&
                     visibleDeptPacks.Any(p => (GetProp(p, "CategoryId")?.ToString() ?? "")
                         .Equals(llmPick.selectedCategoryId, StringComparison.OrdinalIgnoreCase)))
                    ? llmPick!.selectedCategoryId
                    : top.Cat;
            }

            var selectedDeptPack = visibleDeptPacks.First(p =>
                (GetProp(p, "CategoryId")?.ToString() ?? "")
                    .Equals(selectedCategoryId, StringComparison.OrdinalIgnoreCase));

            var selectedPackDto = CoerceToPackDto(selectedDeptPack);

            // SQL üretimi (PackDto overload) — konumsal argüman kullan
            var fullSchema = await _schema.GetSchemaAsync(ct);
            var prompt = _promptBuilder.BuildPromptForPack(
                req.Question,
                selectedPackDto,
                fullSchema,
                null,                    // adjacent packs yok
                "SQL Server (T-SQL)",
                true,                    // requireSingleSelect
                true,                    // forbidDml
                true                     // preferAnsi
            );

            if (string.IsNullOrWhiteSpace(prompt))
                return StatusCode(500, new { error = "Prompt generation returned empty string." });

            _logger.LogInformation("Routing selected pack: {cat} - {name}", selectedPackDto.CategoryId, selectedPackDto.Name);

            var sql = await _llm.GetSqlAsync(prompt, ct);

            return Ok(new
            {
                selected = selectedPackDto.CategoryId,
                packName = selectedPackDto.Name,
                sql
            });
        }
    }
}
