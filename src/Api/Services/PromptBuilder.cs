using System.Text;
using System.Text.RegularExpressions;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    public class PromptBuilder : IPromptBuilder
    {
        // =====================================================
        // 1) schema slice icin prompt  (NebimH / Nebim V3 versiyonu)
        // =====================================================
        public string BuildSchemaSlicingPrompt(SchemaDto schema, int maxPacks = 10)
        {
            if (maxPacks < 4) maxPacks = 4;
            if (maxPacks > 20) maxPacks = 20;

            var sb = new StringBuilder();

            sb.AppendLine("Sen kidemli bir veri modelleme ve BI mimarısın.");
            sb.AppendLine("Gorevin: Asagidaki veritabanı semasini incele ve is mantigina gore KUCUK sayida kategori (pack) uretmek.");
            sb.AppendLine($"Hedef pack sayisi: 5–{maxPacks}. ASLA {maxPacks} uzerine cikma.");
            sb.AppendLine();

            sb.AppendLine("GENEL ZORUNLU KURALLAR:");
            sb.AppendLine("- Her tablo EN AZ BIR pack icinde yer almak zorunda. Pack disinda tablosuz kalamaz.");
            sb.AppendLine("- Ayni tablo birden fazla pack'te tekrar edilemez. Bir tablo ya tablesCore ya tablesSatellite olarak SADECE bir pack'te bulunur.");
            sb.AppendLine("- Bos pack OLUSTURMA. Eger bir pack hic gercek tablo icermiyorsa o pack'i hic yazma.");
            sb.AppendLine("- fkEdges yalnizca su formatta olmali: { \"from\": \"Tablo.Kolon\", \"to\": \"Tablo.Kolon\" }. Bos veya gecersiz obje koyma.");
            sb.AppendLine("- pack.name Turkce, aciklayici olsun. pack.categoryId kisaltma / snake_case olabilir (ornegin cari_hesap, satis_finans, organizasyon).");
            sb.AppendLine("- Header/Line iliskileri (ornegin trInvoiceHeader ↔ trInvoiceLine ↔ trInvoiceLineCurrency, trOrderHeader ↔ trOrderLine ↔ trOrderLineCurrency) AYNI pack icinde kalmali.");
            sb.AppendLine("- Guclu FK ile birbirine bagli tablo kumelerini parcalaMA.");
            sb.AppendLine();

            sb.AppendLine("DOMAIN IPUCLARI (NEBIM TARZI):");
            sb.AppendLine("1) 'cari_hesap' benzeri bir pack olustur:");
            sb.AppendLine("   - Cari hesap / musteri / tedarikci bilgisini tutan tablolar genelde cdCurrAcc*, prCurrAcc* ile baslar.");
            sb.AppendLine("   - Bu tablolar kredi limiti, odeme kosulu (PaymentTerm), vergi durumlari, e-fatura / e-irsaliye zorunluluklari, VIP flag, iletişim bilgisi, adres bilgisi gibi alanlar icerebilir.");
            sb.AppendLine("   - Ornek tablo aileleri: cdCurrAcc, cdCurrAccDesc, prCurrAccCommunication, prCurrAccPersonalInfo, prCurrAccPostalAddress, prCurrAccAttribute, cdCurrAccAttributeDesc (ve benzerleri).");
            sb.AppendLine();

            sb.AppendLine("2) 'satis_finans' benzeri bir pack olustur:");
            sb.AppendLine("   - Satis / siparis / fatura / finansal satir detaylarini tutan tablolar trInvoiceHeader, trInvoiceLine, trInvoiceLineCurrency, trOrderHeader, trOrderLine, trOrderLineCurrency gibi isimlere sahiptir.");
            sb.AppendLine("   - Bu tablolarda urun kodu (ItemCode), renk / varyant kodlari, miktar (Qty1), fiyat (Price), iskonto (DiscountRate), KDV (VatRate), doviz bilgileri (CurrencyCode, PriceExchangeRate), teslim tarihi (DeliveryDate), WarehouseCode vb. alanlar olur.");
            sb.AppendLine("   - FaturaHeader/SiparisHeader ile Line ve LineCurrency ayni pack'te kalmali.");
            sb.AppendLine("   - Bu pack icin bridgeRefs ZORUNLUDUR:");
            sb.AppendLine("       * 'cari_hesap' pack'ine bag: genelde Header tablosunda CurrAccTypeCode / CurrAccCode gibi kolonlar bulunur.");
            sb.AppendLine("       * 'organizasyon' pack'ine bag: genelde Header tablosunda StoreCode / OfficeCode / WarehouseCode / CompanyCode vb. kolonlar bulunur.");
            sb.AppendLine("     Bu nedenle satis_finans pack'indeki header tablolarini (or. trInvoiceHeader, trOrderHeader) bridgeRefs.viaTables icine yaz ve hem cari_hesap hem organizasyon icin bridgeRefs olustur.");
            sb.AppendLine();

            sb.AppendLine("3) 'organizasyon' benzeri bir pack olustur:");
            sb.AppendLine("   - Magaza / ofis / depo / sirket / POS / kasa parametrelerini tasiyan tablolar (StoreCode, OfficeCode, WarehouseCode, CompanyCode, POSTerminalID, IBAN, SWIFTCode, UseBankAccOnStore, IsSubjectToEInvoice, IsSubjectToEShipment vb.) BU PACK'E girmeli.");
            sb.AppendLine("   - Satis personeli, satis ekibi, satis tipi, prim orani gibi tablolar (cdSalesperson, cdSalespersonTeam, cdSalespersonTeamDesc, cdSalespersonType, cdSalespersonTypeDesc) BU PACK'E girmeli.");
            sb.AppendLine("   - Organizasyona bagli IK / calisan tablolarini da (or: hrEmployeeJobTitle, hrEmployeeWorkPlace, hrEmployeePayrollProfile gibi) bu pack'e tablesSatellite olarak ekle.");
            sb.AppendLine("   - Lokasyon / ofis / isyeri aciklamalari (cdOfficeDesc, cdWorkPlaceDesc, cdJobDepartmentDesc, cdJobTitle, cdJobTitleDesc vb.) da burada yer alabilir.");
            sb.AppendLine("   - Bu pack GENEL olarak isyeri ve organizasyon yapisini, magaza/Ofis/Depo baglantisini, banka hesap parametrelerini ve calisan bilgisini temsil eder.");
            sb.AppendLine();

            sb.AppendLine("4) 'sistem_tanim' benzeri bir pack olustur:");
            sb.AppendLine("   - Sistem / uygulama / dil tanimlari (bsApplication, bsApplicationDesc, cdDataLanguage, cdDataLanguageDesc vb.).");
            sb.AppendLine("   - Bu pack tipik olarak konfigurasyon ve dil destegi bilgisidir.");
            sb.AppendLine();

            sb.AppendLine("5) Diger pack'ler YALNIZCA gerekliyse olustur:");
            sb.AppendLine("   - Ornegin ayri bir 'finans_parametre' pack'i olusturmak istiyorsan, oraya IBAN / SWIFT / banka hesabı / vergi yukumlulugu gibi TAMAMI finansal parametre tablolarini koymalisin.");
            sb.AppendLine("   - Eger boyle ayri bir tablo seti bulamiyorsan veya sadece baska pack'lerde zaten kullanilmis ayni tablolar varsa, bu pack'i HIC yazma.");
            sb.AppendLine("   - Kesinlikle SADECE tekrar tablo koymak icin yeni pack olusturma.");
            sb.AppendLine();

            sb.AppendLine("BRIDGEREFS KURALLARI:");
            sb.AppendLine("- satis_finans pack'inin bridgeRefs alaninda EN AZ iki kayit olmali:");
            sb.AppendLine("    { \"toCategory\": \"cari_hesap\", \"viaTables\": [\"trInvoiceHeader\", \"trOrderHeader\"] }");
            sb.AppendLine("    { \"toCategory\": \"organizasyon\", \"viaTables\": [\"trInvoiceHeader\", \"trOrderHeader\"] }");
            sb.AppendLine("- cari_hesap pack'i icin bridgeRefs, satis_finans ile bag kurabilir (musterinin satis hareketlerine ulasmak icin).");
            sb.AppendLine("- organizasyon pack'i icin bridgeRefs zorunlu DEGIL ama olabilir (or. organizasyon → satis_finans uzerinden magaza satis performansi).");
            sb.AppendLine();

            sb.AppendLine("HER PACK OBJESI SU ALANLARA SAHIP OLMALI:");
            sb.AppendLine("- categoryId: kisa id (ornegin 'cari_hesap', 'satis_finans', 'organizasyon', 'sistem_tanim').");
            sb.AppendLine("- name: Turkce okunabilir isim (ornegin 'Cari Hesaplar', 'Satis & Fatura Satirlari').");
            sb.AppendLine("- tablesCore: o domainin ana islemsel tabloları (header, line, vs). BOS OLMAMALI.");
            sb.AppendLine("- tablesSatellite: lookup / aciklama / parametre / bagli detay tabloları.");
            sb.AppendLine("- fkEdges: sadece bu pack icindeki join kenarlari (\"from\": \"Tablo.Kolon\", \"to\": \"Tablo.Kolon\").");
            sb.AppendLine("- bridgeRefs: diger pack'lere kritik kopruler. Ornek:");
            sb.AppendLine("    { \"toCategory\": \"cari_hesap\", \"viaTables\": [\"trInvoiceHeader\"] }");
            sb.AppendLine("- summary: Turkce ozet (<=200 kelime).");
            sb.AppendLine("- grain: Kayit tanesi. Ornek: 'Cari hesap', 'Fatura satiri', 'Organizasyon kaydi', 'Sistem tanimi'.");
            sb.AppendLine();

            sb.AppendLine("STRICT JSON FORMATINDA DONDUR. SADECE JSON CIKART. Yapı su sekilde olmali:");
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

            // schema dökümü: tablolar, kolonlar, FK ilişkileri
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
            sb.AppendLine("SADECE JSON DONDUR. Aciklama yazma.");
            return sb.ToString();
        }


        // =====================================================
        // 2) route promptu , hangi soru hangi packe ait
        // (simdilik ayni birakiyoruz; tek DB test ediyorsun)
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

            // NOTE: burasi hala eski heuristic'leri kullaniyor.
            // Ileride Nebim icin 'cari', 'satis', 'organizasyon' kelimeleriyle update edebiliriz.
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
        // 3) PACK-SCOPED SQL GENERATION PROMPT  (NebimH domain hint ile)
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

            // Guardrails: type safety
            sb.AppendLine();
            sb.AppendLine("Type-safety rules:");
            sb.AppendLine("- NEVER compare string literals to numeric ID columns.");
            sb.AppendLine("- If the user mentions a NAME/TITLE, JOIN to the lookup table and filter by its TEXT/VARCHAR column.");
            sb.AppendLine("- Only compare ID-to-ID, TEXT-to-TEXT.");
            sb.AppendLine("- Use explicit joins to dimension/lookup tables when filtering by human-readable fields (e.g. StoreCode, SalespersonTeamName, CurrAccName).");

            // Nebim domain hint
            sb.AppendLine();
            sb.AppendLine("Domain hint (Nebim-like ERP):");
            sb.AppendLine("- 'Cari hesap' (musteri/tedarikci) bilgileri CurrAcc* / cdCurrAcc* / prCurrAcc* tablolardadir. Bu tablolarda kredi limiti, odeme kosulu, e-fatura / e-irsaliye durumu, iletisim bilgileri bulunabilir.");
            sb.AppendLine("- Satis / fatura / siparis satirlari ItemCode, Qty1, Price, VatRate, DiscountRate, CurrencyCode, WarehouseCode gibi kolonlar icerir. Bunlar genelde line ve line-currency tablolarida tutulur.");
            sb.AppendLine("- Magaza / ofis / depo / satisci yapisi StoreCode, OfficeCode, WarehouseCode, SalespersonTeamCode gibi alanlarla temsil edilir; eger soru 'hangi magaza' veya 'hangi satis ekibi' diyorsa bu alanlari kullan.");
            sb.AppendLine("- Doviz (CurrencyCode, ExchangeRate) gibi alanlar ayri satir tablosunda olabilir; ihtiyac varsa JOIN et.");
            sb.AppendLine("- Eger kullanici tablo veya alan adi vermediyse ama 'musteri', 'cari', 'magaza', 'sube', 'satis miktari', 'KDV', 'iskonto' gibi ticari kavramlar soruyorsa yukaridaki tablolar tipik olarak dogru yerdir.");
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

