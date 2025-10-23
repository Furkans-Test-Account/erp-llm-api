using System.Text;
using System.Text.RegularExpressions;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    public class PromptBuilder : IPromptBuilder
    {
        // =====================================================
        // 1) schema slice icin prompt #DONE
        // =====================================================
        public string BuildSchemaSlicingPrompt(SchemaDto schema, int maxPacks = 10)
        {
            if (maxPacks < 4) maxPacks = 4;
            if (maxPacks > 20) maxPacks = 20;

            var sb = new StringBuilder();

            sb.AppendLine("You are a senior data modeler and BI architect.");
            sb.AppendLine("Task: Read the database schema and propose a SMALL set of business-aligned categories (packs).");
            sb.AppendLine($"Target number of packs: 5–{maxPacks} (do NOT exceed {maxPacks}).");
            sb.AppendLine();

            sb.AppendLine("Hard rules:");
            sb.AppendLine("- Keep header/line tables and strong FK cliques in the SAME pack (e.g., Orders/OrderItems).");
            sb.AppendLine("- Do NOT emit empty objects in fkEdges. Use structured edges only: { \"from\": \"Table.Col\", \"to\": \"Table.Col\" }.");
            sb.AppendLine("- Prefer Turkish names for pack.name, but keep pack.categoryId as English/Turkish or snake case.");
            sb.AppendLine();

            // === Bu kisim balmy db icin fine tune edilmis prompt parcasi #IMPORTANT ===
            sb.AppendLine("Domain heuristics (very important):");
            sb.AppendLine("- Any table that starts with or clearly relates to production (e.g., UretimEmri*, AltUretimEmri*, Uretim*, Istasyon*, LotTakip*, Vardiya*) must belong to a 'production' pack.");
            sb.AppendLine("- Place `SiparisUretimEmri` under the 'sales' pack (if present), BUT create a bridgeRef from 'sales' to 'production' via key `UretimEmriNo` so questions about production numbers can reach production facts.");
            sb.AppendLine("- Logistics/Shipping (e.g., Irsaliye, Sevkiyat*) → 'logistics' pack.");
            sb.AppendLine("- Inventory/Warehouse (Depo*, DepoMov*, Sayim*, Stok*) → 'inventory' pack.");
            sb.AppendLine("- Customer master (Cari, Customer*) → 'customer_master' pack.");
            sb.AppendLine("- Product catalog (Products, Categories, StokKartlari, Barcodes, Mamul*) → 'catalog' pack.");
            sb.AppendLine();

            sb.AppendLine("For each pack:");
            sb.AppendLine("- tablesCore: main transactional/fact tables + tightly coupled heads/lines");
            sb.AppendLine("- tablesSatellite: small reference/lookup/log tables kept in the pack");
            sb.AppendLine("- fkEdges: in-pack join edges with explicit column pairs (from/to)");
            sb.AppendLine("- bridgeRefs: essential cross-pack links like { \"toCategory\": \"production\", \"viaTables\": [\"SiparisUretimEmri\"] }");
            sb.AppendLine("- summary: short Turkish summary (≤200 words)");
            sb.AppendLine("- grain: Turkish, e.g., \"Sipariş\", \"Üretim Emri\", \"Depo hareketi\"");
            sb.AppendLine();

            sb.AppendLine("Return STRICT JSON ONLY with this exact shape:");
            sb.AppendLine("{");
            sb.AppendLine("  \"schemaName\": string,");
            sb.AppendLine("  \"packs\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"categoryId\": string,");
            sb.AppendLine("      \"name\": string,");
            sb.AppendLine("      \"tablesCore\": [string],");
            sb.AppendLine("      \"tablesSatellite\": [string],");
            sb.AppendLine("      \"fkEdges\": [{ \"from\": string, \"to\": string }],");
            sb.AppendLine("      \"bridgeRefs\": [{ \"toCategory\": string, \"viaTables\": [string] }],");
            sb.AppendLine("      \"summary\": string,");
            sb.AppendLine("      \"grain\": string");
            sb.AppendLine("    }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine();

            // schema ozeti (tablolar + kolon isimleri + FK/giden-gelen)
            sb.AppendLine($"SchemaName: {schema.SchemaName}");
            sb.AppendLine("Tables:");
            foreach (var t in schema.Tables.OrderBy(t => t.Name))
            {
                sb.AppendLine($"- {t.Name}:");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    sb.AppendLine($"  desc: {SanitizeInline(t.Description)}");
                if (t.Columns?.Count > 0)
                    sb.AppendLine("  columns: " + string.Join(", ", t.Columns.Select(c => c.Name)));

                if (t.ForeignKeys?.Count > 0)
                {
                    sb.AppendLine("  foreignKeys:");
                    foreach (var fk in t.ForeignKeys)
                    {
                        var toTbl = string.IsNullOrWhiteSpace(fk.ToTable) ? "(unknown)" : fk.ToTable;
                        var fromCols = string.Join(",", fk.FromColumns);
                        var toCols = string.Join(",", fk.ToColumns);
                        sb.AppendLine($"    - {t.Name}.{fromCols} -> {toTbl}.{toCols}");
                    }
                }
                if (t.ReferencedBy?.Count > 0)
                {
                    sb.AppendLine("  referencedBy:");
                    foreach (var rb in t.ReferencedBy)
                    {
                        sb.AppendLine($"    - {rb.FromTable}.{string.Join(",", rb.FromColumns)} -> {t.Name}.{string.Join(",", rb.ToColumns)}");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("Return ONLY the JSON. No commentary.");
            return sb.ToString();
        }

        // =====================================================
        // 2) route promptu , hangi soru hangi packe ait #DONE
        // =====================================================
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

            // === ROUTING HEURISTICS (kalici) ===
            sb.AppendLine("Heuristics:");
            sb.AppendLine("- If the question contains any of: UretimEmri, ÜretimEmri, UretilecekUrunKodu, Üretilecek_Ürünkodu, Istasyon, Lot → prefer 'production'.");
            sb.AppendLine("- If it contains: Siparis, Sipariş, Teslimat, Satış → prefer 'sales'.");
            sb.AppendLine("- If it contains: Depo, Sayim, Stok, Barkod → prefer 'inventory'.");
            sb.AppendLine("- If it contains: Cari, Müşteri → prefer 'customer_master'.");
            sb.AppendLine("- If it contains: Ürün, Kategori → prefer 'catalog'.");
            sb.AppendLine("- When a question mentions a production order number (e.g., UretimEmriNo), prefer 'production' even if there is a sales link such as SiparisUretimEmri.");
            sb.AppendLine("- Never invent pack IDs; choose from the provided list only.");
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

        // =====================================================
        // 3) PACK-SCOPED SQL GENERATION PROMPT
        // =====================================================
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
            if (preferAnsi)
                sb.AppendLine("Prefer ANSI SQL when possible; use T-SQL specifics only if necessary.");
            if (forbidDml)
                sb.AppendLine("CRITICAL: DML/DDL is forbidden. Do NOT use INSERT/UPDATE/DELETE/MERGE/TRUNCATE/CREATE/ALTER/DROP.");
            if (requireSingleSelect)
                sb.AppendLine("Return ONLY one single SELECT statement without comments or markdown fences.");

            sb.AppendLine();
            sb.AppendLine("Scope control:");
            sb.AppendLine("- You may ONLY reference tables listed below.");
            sb.AppendLine("- If an adjacent pack is provided, you may use ONLY the explicitly allowed tables from that adjacent pack.");
            sb.AppendLine("- Use the provided FK edges to build safe and correct joins.");
            sb.AppendLine("- If a FK can be NULL, consider LEFT JOIN.");
            sb.AppendLine("- If the question implies a timeframe (e.g., last 30 days), use appropriate datetime columns from this pack.");
            sb.AppendLine();

            sb.AppendLine($"Pack: {pack.Name} ({pack.CategoryId})");
            if (!string.IsNullOrWhiteSpace(pack.Grain))
                sb.AppendLine($"Grain: {SanitizeInline(pack.Grain)}");

            if (pack.TablesCore?.Count > 0)
                sb.AppendLine("Tables (core): " + string.Join(", ", pack.TablesCore));
            if (pack.TablesSatellite?.Count > 0)
                sb.AppendLine("Tables (satellite): " + string.Join(", ", pack.TablesSatellite));

            if (pack.FkEdges?.Count > 0)
            {
                sb.AppendLine("Joins (FK edges):");
                foreach (var e in pack.FkEdges)
                    if (!string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To))
                        sb.AppendLine($" - {e.From} -> {e.To}");
            }

            if (pack.BridgeRefs?.Count > 0)
            {
                sb.AppendLine("Bridges (to other packs/tables):");
                foreach (var br in pack.BridgeRefs)
                    sb.AppendLine($" - via {string.Join(",", br.ViaTables ?? new List<string>())} -> {br.ToCategory}");
            }

            // Adjacent packs (allowed tables = only their core by default)
            var allowedAdjacentTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (adjacentPacks != null && adjacentPacks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Adjacent packs allowed (only if necessary):");
                foreach (var ap in adjacentPacks)
                {
                    var coreList = ap.TablesCore ?? new List<string>();
                    foreach (var t in coreList) allowedAdjacentTables.Add(t);
                    sb.AppendLine($" * {ap.Name}: {string.Join(", ", coreList)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Pack schema details:");
            var allowedMain = (pack.TablesCore ?? new List<string>())
                              .Concat(pack.TablesSatellite ?? new List<string>())
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var t in fullSchema.Tables.Where(t => allowedMain.Contains(t.Name)))
            {
                sb.AppendLine($"- {t.Name}:");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    sb.AppendLine($"  desc: {SanitizeInline(t.Description)}");
                if (t.Columns?.Count > 0)
                    sb.AppendLine("  columns: " + string.Join(", ", t.Columns.Select(c => c.Name)));
            }

            if (allowedAdjacentTables.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Adjacent schema details (allowed tables only):");
                foreach (var t in fullSchema.Tables.Where(t => allowedAdjacentTables.Contains(t.Name)))
                {
                    sb.AppendLine($"- {t.Name}:");
                    if (!string.IsNullOrWhiteSpace(t.Description))
                        sb.AppendLine($"  desc: {SanitizeInline(t.Description)}");
                    if (t.Columns?.Count > 0)
                        sb.AppendLine("  columns: " + string.Join(", ", t.Columns.Select(c => c.Name)));
                }
            }

            // Guardrails: type safety #IMPORTANT
            sb.AppendLine();
            sb.AppendLine("Type-safety rules:");
            sb.AppendLine("- NEVER compare string literals to numeric ID columns.");
            sb.AppendLine("- If the user mentions a NAME/TITLE, JOIN to the lookup table and filter by its TEXT column.");
            sb.AppendLine("- Only compare ID-to-ID, TEXT-to-TEXT.");
            sb.AppendLine("- Use explicit joins to dimension/lookup tables when filtering by human-readable fields.");

            // Domain hint , llm icin
            sb.AppendLine();
            sb.AppendLine("Domain hint:");
            sb.AppendLine("- If filtering by production order number (e.g., UretimEmriNo), consider that the key is stored on the production fact (e.g., UretimEmriP) and join/lookup there to fetch fields like UretilecekUrunKodu.");

            sb.AppendLine();
            sb.AppendLine($"UserQuestion: {userQuestion}");
            sb.AppendLine("Output requirements:");
            sb.AppendLine("- Generate a single efficient SELECT statement.");
            sb.AppendLine("- Use TOP N instead of LIMIT for SQL Server.");
            sb.AppendLine("- Escape reserved identifiers using [square_brackets] if necessary.");
            sb.AppendLine("- Do not include comments or markdown fences.");

            return sb.ToString();
        }

        // -------------------------------
        // Helpers
        // -------------------------------
        private static string SanitizeInline(string s)
        {
            var one = Regex.Replace(s, @"\s+", " ").Trim();
            one = one.Replace("\"", "'");
            return one;
        }
    }
}
