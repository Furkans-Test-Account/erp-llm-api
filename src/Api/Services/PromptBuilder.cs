// File: src/Api/Services/PromptBuilder.cs
#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Linq;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    public class PromptBuilder : IPromptBuilder
    {
        // 1) Routing prompt (soru -> tek pack)  ✅ RE-ENABLED & HARDENED
        public string BuildRoutingPrompt(SliceResultDto sliced, string userQuestion, int topK = 3)
        {
            if (topK < 1) topK = 1;
            if (topK > 6) topK = 6;

            // Domain kelimeleri
            var depoTriggers = new[]
            {
                "depo","depolar arası","ambar","stok","raf","barkod","sayım",
                "irsaliye","transfer","taşıma","taşınan","sevkiyat","sevk",
                "çıkış","giriş","ürün çıkışı","ürün girişi","warehouse",
                "inventory","movement","transfer slip"
            };

            var ikTriggers = new[]
            {
                "ik","insan kaynak","bordro","maaş","ücret","personel","çalışan",
                "sgk","işe giriş","işe başlama","işten çıkış","işten ayrılış",
                "agı","agi","kıdem","ihbar","izin","sendika","işyeri","workplace","payroll"
            };

            var satisFinansLojistikTriggers = new[]
            {
                "sipariş","fatura","satış","ciro","tahsilat","alacak","borç",
                "irsaliye","kargo","müşteri","sevkiyat","dispatch","shipment","invoice","order"
            };

            var cariTriggers = new[]
            {
                "cari","müşteri","tedarikçi","bakiye","hesap ekstresi","supplier","customer","vendor"
            };

            var orgTriggers = new[]
            {
                "mağaza","ofis","şube","kasa","ekip","departman","organizasyon","store","office","team","pos"
            };

            var sozlukParamTriggers = new[]
            {
                "kod","sözlük","tanım","parametre","ayarl","code list","lookup","dictionary","config","settings"
            };

            // Pack'leri anahtar kelime torbalarıyla hazırlayalım
            var packBags = new List<(string Id, string Name, string Summary, string[] Keywords)>();
            foreach (var p in sliced.Packs.OrderBy(x => x.Name))
            {
                var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var t in (p.TablesCore ?? new List<string>()).Concat(p.TablesSatellite ?? new List<string>()))
                {
                    var w = Regex.Replace(t ?? "", @"[^a-z0-9ğüşöçıİĞÜŞÖÇ]+", " ", RegexOptions.IgnoreCase).ToLowerInvariant();
                    foreach (var token in w.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        words.Add(token);
                }

                var idL = (p.CategoryId ?? string.Empty).ToLowerInvariant();
                void add(params string[] ks) { foreach (var k in ks) words.Add(k); }

                if (idL.Contains("depo") || idL.Contains("stok"))
                    add(depoTriggers);
                if (idL.Contains("insan") || idL.Contains("kaynak") || idL.Contains("ik"))
                    add(ikTriggers);
                if (idL.Contains("satis") || idL.Contains("finans") || idL.Contains("lojistik"))
                    add(satisFinansLojistikTriggers);
                if (idL.Contains("cari"))
                    add(cariTriggers);
                if (idL.Contains("organizasyon"))
                    add(orgTriggers);
                if (idL.Contains("sozluk") || idL.Contains("parametre"))
                    add(sozlukParamTriggers);

                var id = p.CategoryId ?? string.Empty;
                var name = string.IsNullOrWhiteSpace(p.Name) ? id : p.Name!;
                var summary = SanitizeInline(p.Summary ?? string.Empty);
                packBags.Add((id, name, summary, words.ToArray()));
            }

            var sb = new StringBuilder();
            sb.AppendLine("You are a routing assistant.");
            sb.AppendLine("Task: From the list of packs below and a Turkish user question, pick the SINGLE best pack.");
            sb.AppendLine($"Also return up to top {topK} candidates.");
            sb.AppendLine();
            sb.AppendLine("Output STRICT JSON ONLY:");
            sb.AppendLine("{\"selectedCategoryId\":string,\"candidateCategoryIds\":[string],\"reason\":string}");
            sb.AppendLine();
            sb.AppendLine("Hard rules (language: TR):");
            sb.AppendLine("- Eğer soru {depo, depolar arası, ambar, stok, sayım, barkod, transfer, sevkiyat, çıkış, giriş} kelimelerini içeriyorsa yüksek öncelikle 'depo_stok' seç.");
            sb.AppendLine("- Eğer soru {ik, insan kaynak, bordro, maaş, sgk, çalışan, personel} kelimelerini içeriyorsa 'insan_kaynaklari' adayı güçlüdür.");
            sb.AppendLine("- Eğer soru {sipariş, fatura, satış, ciro, sevkiyat, kargo} içeriyorsa 'satis_finans' veya 'lojistik' adayı güçlüdür (mevcut paket IDs’ine bak).");
            sb.AppendLine("- Eğer soru {cari, müşteri, tedarikçi, bakiye} içeriyorsa 'cari_hesap' adayı güçlüdür.");
            sb.AppendLine("- Yine de nihai seçim, anahtar kelime eşleşmesi + paket özet/tables anahtar sözcükleri ile en iyi örtüşen tek pakettir.");
            sb.AppendLine("- Asla yeni kategori uydurma; sadece listeden seç.");
            sb.AppendLine();
            sb.AppendLine("Scoring guidance:");
            sb.AppendLine("- Case-insensitive keyword overlap between question and each pack’s keywords.");
            sb.AppendLine("- If a 'Hard rules' group matches, add a large bonus to those packs (e.g., +100).");
            sb.AppendLine("- Tie-break: prefer packs whose id/name appears in the question; then prefer more core-table keyword hits.");
            sb.AppendLine();
            sb.AppendLine("Available packs:");
            foreach (var (Id, Name, Summary, Keywords) in packBags)
            {
                sb.AppendLine($"- id: {Id}");
                sb.AppendLine($"  name: {Name}");
                if (!string.IsNullOrWhiteSpace(Summary)) sb.AppendLine($"  summary: {Summary}");
                if (Keywords.Length > 0) sb.AppendLine($"  keywords: {string.Join(", ", Keywords.Distinct().Take(100))}");
            }
            sb.AppendLine();
            sb.AppendLine($"UserQuestion: {userQuestion}");
            sb.AppendLine("Return ONLY the JSON. No commentary.");
            return sb.ToString();
        }

        // 2) PACK-SCOPED SQL PROMPT — department-only (no adjacent packs)
        public string BuildPromptForPack(
            string userQuestion,
            PackDto pack,
            SchemaDto fullSchema,
            IReadOnlyList<PackDto>? _ignoredAdjacentPacks = null, // kept for signature compat; ignored
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
                var nonEmpty = pack.BridgeRefs
                    .Where(br => br?.ViaTables != null && br.ViaTables.Count > 0)
                    .ToList();
                if (nonEmpty.Count > 0)
                {
                    sb.AppendLine("Bridges (to other packs):");
                    foreach (var br in nonEmpty)
                        sb.AppendLine($" - via {string.Join(",", br.ViaTables)} -> {br.ToCategory}");
                }
            }

            // Pack schema details — with column types
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

        // 2.b) DepartmentPackDto overload — department-only, with lookup whitelist details
        public string BuildPromptForPack(
            string userQuestion,
            DepartmentPackDto deptPack,
            SchemaDto fullSchema,
            IReadOnlyList<DepartmentPackDto>? _ignoredAdjacentPacks = null, // ignored
            string sqlDialect = "SQL Server (T-SQL)",
            bool requireSingleSelect = true,
            bool forbidDml = true,
            bool preferAnsi = true)
        {
            // Coerce FK/Bridge using RAW props (not ToString)
            var fkEdges = CoerceFkEdges(GetAsObjectEnumerable(GetPropRaw(deptPack, "FkEdges")));
            var bridges = CoerceBridgeRefs(GetAsObjectEnumerable(GetPropRaw(deptPack, "BridgeRefs")));

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

            // Build base prompt
            var basePrompt = BuildPromptForPack(userQuestion, pack, fullSchema, null, sqlDialect, requireSingleSelect, forbidDml, preferAnsi);

            // AllowedRefViews — RAW list of objects
            var refSpecs = GetAsObjectEnumerable(GetPropRaw(deptPack, "AllowedRefViews"));
            if (refSpecs == null) return basePrompt;

            var appendix = new StringBuilder();
            var allowedSourceTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            appendix.AppendLine();
            appendix.AppendLine("Lookup tables you MAY join (only from this list):");
            foreach (var o in refSpecs)
            {
                var n = GetProp(o, "Name");
                var src = GetProp(o, "SourceTable");
                var colsEnum = GetAsObjectEnumerable(GetPropRaw(o, "Columns"));
                var cols = ToStringList(colsEnum);

                if (!string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(src))
                {
                    appendix.AppendLine($" * {n}: {src} ({string.Join(", ", cols)})");
                    allowedSourceTables.Add(src!);
                }
            }

            var refSourceNames = refSpecs
                .Select(r => GetProp(r, "SourceTable"))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .ToList();

            var hints = BuildLookupHintsForDept(deptPack, fullSchema, refSourceNames);

            if (deptPack.CategoryId.Equals("insan_kaynaklari", StringComparison.OrdinalIgnoreCase))
            {
                void addHintIfAllowed(string srcTable, string srcCols, string refTable, string refCols)
                {
                    if (allowedSourceTables.Contains(refTable))
                        hints.Add($"{srcTable}.({srcCols}) -> {refTable}.({refCols})");
                }

                addHintIfAllowed("hrEmployeePayrollProfile", "CurrAccTypeCode, CurrAccCode", "cdCurrAcc", "CurrAccTypeCode, Code");
                addHintIfAllowed("hrEmployeeJobTitle", "JobTitleCode", "cdJobTitle", "JobTitleCode");
                addHintIfAllowed("hrEmployeeWorkPlace", "WorkPlaceCode", "cdWorkPlace", "WorkPlaceCode");
                addHintIfAllowed("hrEmployeeMonthlySum", "MissingWorkReasonCode", "cdMissingWorkReason", "MissingWorkReasonCode");
                addHintIfAllowed("hrEmployeeWage", "CurrencyCode", "cdCurrency", "CurrencyCode");
                addHintIfAllowed("hrEmployeeMonthlySumDetail", "JobDepartmentCode", "cdJobDepartment", "JobDepartmentCode");
                addHintIfAllowed("hrSGKMonthlyDocument", "EmploymentLawCode", "cdEmploymentLaw", "EmploymentLawCode");
            }

            if (hints.Count > 0)
            {
                appendix.AppendLine("Lookup join hints (keys):");
                foreach (var h in hints.Distinct(StringComparer.OrdinalIgnoreCase))
                    appendix.AppendLine($" - {h}");
            }

            var lookupTables = fullSchema.Tables.Where(t => allowedSourceTables.Contains(t.Name)).ToList();
            if (lookupTables.Count > 0)
            {
                appendix.AppendLine();
                appendix.AppendLine("Lookup schema details (with types):");
                foreach (var t in lookupTables)
                {
                    appendix.AppendLine($"- {t.Name}:");
                    if (!string.IsNullOrWhiteSpace(t.Description))
                        appendix.AppendLine($"  desc: {SanitizeInline(t.Description)}");
                    if (t.Columns?.Count > 0)
                        appendix.AppendLine("  columns: " + string.Join(", ", t.Columns.Select(RenderColumnSignature)));
                }
            }

            return basePrompt + Environment.NewLine + appendix.ToString();
        }

        // 3) categoryId -> prompt (department-only)
        public string BuildPromptForCategory(
            string userQuestion,
            DepartmentSliceResultDto deptSlice,
            string categoryId,
            SchemaDto fullSchema,
            IReadOnlyList<string>? _ignoredAdjacentCategoryIds = null, // ignored
            string sqlDialect = "SQL Server (T-SQL)")
        {
            var deptPack = deptSlice.Packs.FirstOrDefault(p =>
                string.Equals(p.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Pack not found for categoryId: {categoryId}");

            return BuildPromptForPack(userQuestion, deptPack, fullSchema, null, sqlDialect);
        }

        // ------------------------------- Helpers -------------------------------
        private static string SanitizeInline(string s)
        {
            var one = Regex.Replace(s, @"\\s+", " ").Trim();
            one = one.Replace("\"", "'"); // keep LLM JSON simple
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
            return v?.ToString();
        }

        private static object? GetPropRaw(object o, string name)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return p?.GetValue(o);
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

        private static List<string> BuildLookupHintsForDept(DepartmentPackDto pack, SchemaDto schema, IReadOnlyList<string> allowedLookupSourceTables)
        {
            var hints = new List<string>();
            if (allowedLookupSourceTables.Count == 0) return hints;

            var allowed = new HashSet<string>(allowedLookupSourceTables, StringComparer.OrdinalIgnoreCase);
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
                var guesses = new[] { "cd" + baseName, "df" + baseName, "bs" + baseName };
                var hit = guesses.FirstOrDefault(g => allowed.Contains(g));
                if (!string.IsNullOrWhiteSpace(hit))
                    hints.Add($"{tbl}.{col} -> {hit}.Code");
            }

            return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
