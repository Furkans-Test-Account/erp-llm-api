// File: src/Api/Services/PromptBuilder.cs
#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    public class PromptBuilder : IPromptBuilder
    {
        // (Removed) LLM-based schema slicing prompt is no longer needed

        // 2) Routing prompt (soru -> pack)
        public string BuildRoutingPrompt(SliceResultDto sliced, string userQuestion, int topK = 3)
        {
            if (topK < 1) topK = 1;
            if (topK > 6) topK = 6;

            var sb = new StringBuilder();
            sb.AppendLine("You are a routing assistant.");
            sb.AppendLine("Task: Given a list of packs and a user question, choose the single best pack that should be used to answer the question.");
            sb.AppendLine($"Return up to top {topK} candidates as well.");
            sb.AppendLine();
            sb.AppendLine("Output STRICT JSON ONLY with this exact shape:");
            sb.AppendLine("{");
            sb.AppendLine("  \"selectedCategoryId\": string,");
            sb.AppendLine("  \"candidateCategoryIds\": [string],");
            sb.AppendLine("  \"reason\": string");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("Heuristics (TR):");
            sb.AppendLine("- 'cari', 'müşteri', 'tedarikçi' -> cari_hesap");
            sb.AppendLine("- 'sipariş', 'fatura', 'satış', 'ciro' -> satis_finans / lojistik");
            sb.AppendLine("- 'depo', 'stok', 'sayım', 'barkod' -> depo_stok");
            sb.AppendLine("- 'mağaza', 'ofis', 'şube', 'kasa', 'ekip' -> organizasyon");
            sb.AppendLine("- 'parametre', 'tanim', 'dil' -> sistem_parametre / sozluk_kod");
            sb.AppendLine("- Never invent category IDs; choose from provided list only.");
            sb.AppendLine();
            sb.AppendLine("Available packs:");
            foreach (var p in sliced.Packs.OrderBy(x => x.Name))
            {
                var core = p.TablesCore ?? new List<string>();
                var sat = p.TablesSatellite ?? new List<string>();
                var shortCore = string.Join(", ", core.Take(10));
                var shortSat = string.Join(", ", sat.Take(8));
                sb.AppendLine($"- id: {p.CategoryId}");
                sb.AppendLine($"  name: {p.Name}");
                if (!string.IsNullOrWhiteSpace(p.Summary))
                    sb.AppendLine($"  summary: {SanitizeInline(p.Summary)}");
                if (!string.IsNullOrWhiteSpace(p.Grain))
                    sb.AppendLine($"  grain: {SanitizeInline(p.Grain)}");
                if (core.Count > 0) sb.AppendLine($"  tablesCore: {shortCore}");
                if (sat.Count > 0) sb.AppendLine($"  tablesSatellite: {shortSat}");
            }
            sb.AppendLine();
            sb.AppendLine($"UserQuestion: {userQuestion}");
            sb.AppendLine("Return ONLY the JSON. No commentary.");
            return sb.ToString();
        }

        // 3) PACK-SCOPED SQL PROMPT — KOLON TİPLERİ + LOOKUP
        public string BuildPromptForPack(
            string userQuestion,
            PackDto pack,
            SchemaDto fullSchema,
            IReadOnlyList<PackDto>? adjacentPacks = null,
            string sqlDialect = "SQL Server (T-SQL)",
            bool requireSingleSelect = true,
            bool forbidDml = true,
            bool preferAnsi = true)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"You are a SQL generator for {sqlDialect}.");
            if (preferAnsi) sb.AppendLine("Prefer ANSI SQL when possible; use T-SQL specifics only if necessary.");
            if (forbidDml) sb.AppendLine("CRITICAL: DML/DDL is forbidden. Do NOT use INSERT/UPDATE/DELETE/MERGE/TRUNCATE/CREATE/ALTER/DROP.");
            if (requireSingleSelect) sb.AppendLine("Return ONLY one single SELECT statement without comments or markdown fences.");

            sb.AppendLine();
            sb.AppendLine("Scope control:");
            sb.AppendLine("- Use ONLY the tables listed below from this pack.");
            sb.AppendLine("- You may use allowed adjacent pack tables only if explicitly listed.");
            sb.AppendLine("- Use the provided FK edges to build joins; LEFT JOIN if FK can be NULL.");
            sb.AppendLine();

            sb.AppendLine($"Pack: {pack.Name} ({pack.CategoryId})");
            if (!string.IsNullOrWhiteSpace(pack.Grain))
                sb.AppendLine($"Grain: {SanitizeInline(pack.Grain)}");

            var core = pack.TablesCore ?? new List<string>();
            var sat = pack.TablesSatellite ?? new List<string>();

            if (core.Count > 0) sb.AppendLine("Tables (core): " + string.Join(", ", core));
            if (sat.Count > 0) sb.AppendLine("Tables (satellite): " + string.Join(", ", sat));

            if (pack.FkEdges?.Count > 0)
            {
                sb.AppendLine("Joins (FK edges):");
                foreach (var e in pack.FkEdges)
                    if (!string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To))
                        sb.AppendLine($" - {e.From} -> {e.To}");
            }

            if (pack.BridgeRefs?.Count > 0)
            {
                sb.AppendLine("Bridges (to other packs):");
                foreach (var br in pack.BridgeRefs)
                    sb.AppendLine($" - via {string.Join(",", br.ViaTables ?? new List<string>())} -> {br.ToCategory}");
            }

            // Adjacent pack’ler: yalnız core tablolar izinli
            var allowedAdjacentTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (adjacentPacks != null && adjacentPacks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Adjacent packs allowed (core tables only, if necessary):");
                foreach (var ap in adjacentPacks)
                {
                    var coreList = ap.TablesCore ?? new List<string>();
                    foreach (var t in coreList) allowedAdjacentTables.Add(t);
                    if (coreList.Count > 0)
                        sb.AppendLine($" * {ap.Name}: {string.Join(", ", coreList)}");
                }
            }

            // Pack şema detayları — KOLON + TİP
            sb.AppendLine();
            sb.AppendLine("Pack schema details (with types):");
            var allowedMain = new HashSet<string>(core.Concat(sat), StringComparer.OrdinalIgnoreCase);

            foreach (var t in fullSchema.Tables.Where(t => allowedMain.Contains(t.Name)))
            {
                sb.AppendLine($"- {t.Name}:");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    sb.AppendLine($"  desc: {SanitizeInline(t.Description)}");

                if (t.Columns?.Count > 0)
                {
                    var rendered = string.Join(", ", t.Columns.Select(RenderColumnSignature));
                    sb.AppendLine("  columns: " + rendered);
                }
            }

            if (allowedAdjacentTables.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Adjacent schema details (core only, with types):");
                foreach (var t in fullSchema.Tables.Where(t => allowedAdjacentTables.Contains(t.Name)))
                {
                    sb.AppendLine($"- {t.Name}:");
                    if (!string.IsNullOrWhiteSpace(t.Description))
                        sb.AppendLine($"  desc: {SanitizeInline(t.Description)}");
                    if (t.Columns?.Count > 0)
                        sb.AppendLine("  columns: " + string.Join(", ", t.Columns.Select(RenderColumnSignature)));
                }
            }

            // Type-safety guardrails
            sb.AppendLine();
            sb.AppendLine("Type-safety rules:");
            sb.AppendLine("- NEVER compare string literals to numeric ID columns.");
            sb.AppendLine("- When user filters by a TEXT field (name/title), JOIN the appropriate lookup (or allowed ref view) and filter on its TEXT/VARCHAR column.");
            sb.AppendLine("- Compare ID-to-ID, TEXT-to-TEXT.");
            sb.AppendLine("- Use explicit joins to dimension/lookup tables when filtering by human-readable fields.");

            // Domain hint
            sb.AppendLine();
            sb.AppendLine("Domain hint (Nebim-like ERP):");
            sb.AppendLine("- 'Cari hesap' bilgilerinde CurrAcc*/cdCurrAcc*/prCurrAcc* kullanılabilir.");
            sb.AppendLine("- Satır tablolarında ItemCode, Qty1, Price, VatRate, DiscountRate, CurrencyCode, WarehouseCode gibi alanlar bulunur.");
            sb.AppendLine("- Mağaza/Ofis/Depo/Personel için StoreCode, OfficeCode, WarehouseCode, Salesperson* alanları kullanılır.");
            sb.AppendLine("- Döviz için CurrencyCode/ExchangeRate alanları ayrı tablolarda olabilir.");

            sb.AppendLine();
            sb.AppendLine($"UserQuestion: {userQuestion}");
            sb.AppendLine("Output requirements:");
            sb.AppendLine("- Generate ONE efficient SELECT statement.");
            sb.AppendLine("- Use TOP N (not LIMIT) for SQL Server.");
            sb.AppendLine("- Use [square_brackets] for reserved identifiers if needed.");
            sb.AppendLine("- No comments, no markdown fences.");
            return sb.ToString();
        }

        // 3.b) DepartmentPackDto overload (FK/Bridges/AllowedRefViews korunur)
        public string BuildPromptForPack(
            string userQuestion,
            DepartmentPackDto deptPack,
            SchemaDto fullSchema,
            IReadOnlyList<DepartmentPackDto>? adjacentPacks = null,
            string sqlDialect = "SQL Server (T-SQL)",
            bool requireSingleSelect = true,
            bool forbidDml = true,
            bool preferAnsi = true)
        {
            // deptPack.FkEdges / BridgeRefs bazı projelerde List<object> olabilir
            var fkEdges = CoerceFkEdges(GetAsObjectEnumerable(GetProp(deptPack, "FkEdges")));
            var bridges = CoerceBridgeRefs(GetAsObjectEnumerable(GetProp(deptPack, "BridgeRefs")));

            var name = GetProp(deptPack, "Name") ?? deptPack.CategoryId;
            var tablesCore = (deptPack.TablesCore ?? new List<string>()).ToList();
            var tablesSat = (deptPack.TablesSatellite ?? new List<string>()).ToList();

            var pack = new PackDto(
                deptPack.CategoryId,
                name,
                tablesCore,
                tablesSat,
                fkEdges,
                bridges,
                Summary: string.Empty,
                Grain: string.Empty
            );

            IReadOnlyList<PackDto>? adjacent = null;
            if (adjacentPacks != null && adjacentPacks.Count > 0)
            {
                var list = new List<PackDto>();
                foreach (var ap in adjacentPacks)
                {
                    var apFk = CoerceFkEdges(GetAsObjectEnumerable(GetProp(ap, "FkEdges")));
                    var apBr = CoerceBridgeRefs(GetAsObjectEnumerable(GetProp(ap, "BridgeRefs")));
                    var apName = GetProp(ap, "Name") ?? ap.CategoryId;

                    list.Add(new PackDto(
                        ap.CategoryId,
                        apName,
                        ap.TablesCore ?? new List<string>(),
                        ap.TablesSatellite ?? new List<string>(),
                        apFk,
                        apBr,
                        Summary: string.Empty,
                        Grain: string.Empty
                    ));
                }
                adjacent = list;
            }

            var basePrompt = BuildPromptForPack(userQuestion, pack, fullSchema, adjacent, sqlDialect, requireSingleSelect, forbidDml, preferAnsi);

            // AllowedRefViews (List<string> ya da List<object> olabilir) → string list
            var allowedRefViews = ToStringList(GetAsObjectEnumerable(GetProp(deptPack, "AllowedRefViews")));
            if (allowedRefViews.Count == 0)
                return basePrompt;

            var append = new StringBuilder();
            append.AppendLine();
            append.AppendLine("Lookup tables you MAY join (only from this list):");
            append.AppendLine(string.Join(", ", allowedRefViews));

            // lookup join hints
            var lookupHints = BuildLookupHintsForDept(deptPack, fullSchema, allowedRefViews);
            if (lookupHints.Count > 0)
            {
                append.AppendLine("Lookup join hints (keys):");
                foreach (var h in lookupHints) append.AppendLine($" - {h}");
            }

            // Lookup şema detayları
            var allowedLookup = new HashSet<string>(allowedRefViews, StringComparer.OrdinalIgnoreCase);
            var lookups = fullSchema.Tables.Where(t => allowedLookup.Contains(t.Name)).ToList();
            if (lookups.Count > 0)
            {
                append.AppendLine();
                append.AppendLine("Lookup schema details (with types):");
                foreach (var t in lookups)
                {
                    append.AppendLine($"- {t.Name}:");
                    if (!string.IsNullOrWhiteSpace(t.Description))
                        append.AppendLine($"  desc: {SanitizeInline(t.Description)}");
                    if (t.Columns?.Count > 0)
                        append.AppendLine("  columns: " + string.Join(", ", t.Columns.Select(RenderColumnSignature)));
                }
            }

            return basePrompt + Environment.NewLine + append.ToString();
        }

        // 4) categoryId ile prompt üret (DepartmentSliceResultDto)
        public string BuildPromptForCategory(
            string userQuestion,
            DepartmentSliceResultDto deptSlice,
            string categoryId,
            SchemaDto fullSchema,
            IReadOnlyList<string>? adjacentCategoryIds = null,
            string sqlDialect = "SQL Server (T-SQL)")
        {
            var deptPack = deptSlice.Packs.FirstOrDefault(p =>
                string.Equals(p.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Pack not found for categoryId: {categoryId}");

            IReadOnlyList<DepartmentPackDto>? adjacent = null;
            if (adjacentCategoryIds != null && adjacentCategoryIds.Count > 0)
            {
                adjacent = deptSlice.Packs
                    .Where(p => adjacentCategoryIds.Contains(p.CategoryId, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            return BuildPromptForPack(userQuestion, deptPack, fullSchema, adjacent, sqlDialect);
        }

        // ------------------------------- Helpers -------------------------------
        private static string SanitizeInline(string s)
        {
            var one = Regex.Replace(s, @"\s+", " ").Trim();
            one = one.Replace("\"", "'");
            return one;
        }

        private static string RenderColumnSignature(ColumnDto c)
        {
            var colType = GetProp(c, "Type") ?? GetProp(c, "DataType") ?? GetProp(c, "SqlType") ?? GetProp(c, "ColumnType") ?? GetProp(c, "DbType");
            var isNullStr = (GetProp(c, "IsNullable") ?? GetProp(c, "Nullable"))?.ToLowerInvariant();
            var len = GetProp(c, "MaxLength") ?? GetProp(c, "Length") ?? GetProp(c, "Size");

            var typePart = string.IsNullOrWhiteSpace(colType) ? "" : colType!;
            var nullPart = (isNullStr == "true" || isNullStr == "1") ? "?" : "";
            var lenPart = !string.IsNullOrWhiteSpace(len) ? $"({len})" : "";

            return $"{c.Name}:{typePart}{lenPart}{nullPart}".TrimEnd(':');
        }

        private static string? GetProp(object o, string name)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null) return null;
            var v = p.GetValue(o);
            return v?.ToString(); // <- CS0266 fix: daima string? döndür
        }

        private static IEnumerable<object>? GetAsObjectEnumerable(object? value)
        {
            if (value is null) return null;
            if (value is IEnumerable<object> eo) return eo;
            if (value is System.Collections.IEnumerable en)
            {
                var list = new List<object>();
                foreach (var x in en) if (x != null) list.Add(x);
                return list;
            }
            return null;
        }

        private static List<string> ToStringList(IEnumerable<object>? seq)
        {
            var list = new List<string>();
            if (seq == null) return list;
            foreach (var o in seq)
            {
                if (o == null) continue;
                var s = o.ToString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            return list;
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
                var viaList = ToStringList(GetAsObjectEnumerable(viaObj));

                list.Add(new BridgeRefDto(toCategory, viaList));
            }

            return list;
        }

        // Basit heuristic: Pack’teki *Code alanlarını AllowedRefViews ile eşleştir
        private static List<string> BuildLookupHintsForDept(DepartmentPackDto pack, SchemaDto schema, IReadOnlyList<string> allowedRefViews)
        {
            var hints = new List<string>();
            if (allowedRefViews.Count == 0) return hints;

            var allowed = new HashSet<string>(allowedRefViews, StringComparer.OrdinalIgnoreCase);
            var packTables = new HashSet<string>(
                (pack.TablesCore ?? new List<string>()).Concat(pack.TablesSatellite ?? new List<string>()),
                StringComparer.OrdinalIgnoreCase
            );

            var codeCols = schema.Tables
                .Where(t => packTables.Contains(t.Name))
                .SelectMany(t => t.Columns.Select(c => (Table: t.Name, Column: c.Name)))
                .Where(tc => tc.Column.EndsWith("Code", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var (tbl, col) in codeCols)
            {
                var baseName = col.Substring(0, col.Length - "Code".Length);
                var candidates = new List<string?>
                {
                    "cd" + baseName,
                    "cd" + baseName + "Desc"
                };
                if (baseName.StartsWith("SGK", StringComparison.OrdinalIgnoreCase))
                    candidates.Add("cd" + baseName);

                var hit = candidates
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .FirstOrDefault(s => allowed.Contains(s!)); // <- CS8622 fix: non-null garanti
                if (hit != null) hints.Add($"{tbl}.{col} -> {hit}.{col}");
            }

            return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
