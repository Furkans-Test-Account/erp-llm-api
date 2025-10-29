using System.Text.RegularExpressions;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    /// <summary>
    /// Nebim-aware yönlendirici.
    /// - Soru metnini (req.Question) inceleyip her pack için domain'e özel skor üretir.
    /// - Domain sinyalleri Nebim terminolojisine göre ayarlanmıştır
    ///   (cari hesap, fatura, mağaza, satışçı, dil tanımı, oyun kampanyası vs).
    /// - Tablo adı geçtiyse ekstra bonus verir.
    /// - Deterministik tie-break: skor, tablo zenginliği, alfabetik.
    /// </summary>
    public class QuestionRouter : IQuestionRouter
    {
        public RouteResponseDto Route(RouteRequestDto req, SliceResultDto slice)
        {
            var q = (req.Question ?? string.Empty).ToLowerInvariant();

            var results = new List<(PackDto Pack, int Score)>();

            foreach (var p in slice.Packs)
            {
                var score = 0;
                var packName = (p.Name ?? string.Empty).ToLowerInvariant();
                var packId = (p.CategoryId ?? string.Empty).ToLowerInvariant();

                // -------------------------------------------------
                // 1) PACK-BAZI DOMAIN SKORLAMA
                // -------------------------------------------------
                // Burada amaç şu:
                //   Eğer pack cari_hesap ise sadece cari_hesap ile ilgili anahtar kelimelerden puan al.
                //   Eğer pack satis_finans ise satış / fatura / sipariş kelimelerinden puan al.
                //   vs.
                // Böylece yanlış pack'e puan yazmıyoruz.

                // ---- cari_hesap ----
                if (packId.Contains("cari") || packName.Contains("cari") || packName.Contains("hesap"))
                {
                    // cari, müşteri, tedarikçi, iletişim, kredi limiti, ödeme şartı, e-fatura/e-irsaliye
                    score += Count(q, @"\bcari\b|\bmüşteri\b|\bmusteri\b|\btedarikçi\b|\btedarikci\b") * 4;
                    score += Count(q, @"cari\s*kodu|cari\s*hesap\s*kodu|curracccode|curracctypecode") * 4;
                    score += Count(q, @"kredi\s*limiti|credit\s*limit|risk\s*limiti|payment\s*term|ödeme\s*şart|odeme\s*sart") * 3;
                    score += Count(q, @"iletişim|iletisim|telefon|phone|e-?posta|email|adres|address") * 2;
                    score += Count(q, @"e[- ]?fatura|e[- ]?irsaliye|efatura|eirsaliye") * 2;
                    score += Count(q, @"verg[iı]|vergi\s*no|tax\s*id|vkn|tckn") * 2;
                    score += Count(q, @"vip|önemli\s*müşteri|öncelikli\s*müşteri|priority\s*customer") * 2;
                }

                // ---- satis_finans ----
                if (packId.Contains("satis") || packId.Contains("finans") ||
                    packName.Contains("satış") || packName.Contains("fatura") || packName.Contains("sipariş"))
                {
                    // satış, sipariş, fatura, tutar, KDV, iskonto, miktar, adet, fiyat
                    score += Count(q, @"sat[ıi]ş|satis|sipari[sş]|sipariş|order|fatura|invoice|irsaliye") * 4;
                    score += Count(q, @"tutar|ciro|gelir|hasılat|hasilat|amount|price|fiyat|bedel|maliyet|cost") * 3;
                    score += Count(q, @"adet|miktar|qty|quantity|qty1|qty2") * 3;
                    score += Count(q, @"iskonto|discount|indirim|kdv|vat|vergi\s*oran[ıi]|vatrate") * 3;
                    score += Count(q, @"teslimat|teslim\s*tarihi|delivery\s*date|sevkiyat|shipment|kargo") * 2;
                    score += Count(q, @"depo|warehousecode|warehouse|stok\s*çıkış|stok\s*cikis") * 2;
                    score += Count(q, @"döviz|doviz|currency|kur|exchange\s*rate|priceexchangerate|currencycode") * 2;
                    score += Count(q, @"vade|taksit|iscreditsale|kredi\s*sati[sş]") * 2;
                }

                // ---- organizasyon ----
                if (packId.Contains("organizasyon") || packName.Contains("organizasyon") ||
                    packName.Contains("çalışan") || packName.Contains("calisan") ||
                    packName.Contains("mağaza") || packName.Contains("magaza") ||
                    packName.Contains("ofis") || packName.Contains("şube") || packName.Contains("sube"))
                {
                    // mağaza / ofis / şube / depo / satışçı / ekip / prim / personel / çalışma yeri
                    score += Count(q, @"mağaza|magaza|storecode|şube|sube|ofis|officecode|işyeri|isyeri|workplace|workplacecode") * 4;
                    score += Count(q, @"depo|warehouse|warehousecode|stok\s*lokasyon|lokasyon\s*kodu") * 3;
                    score += Count(q, @"satışçı|satisci|salesperson|ekip|team|satış\s*ekibi|prim|incentive|komisyon") * 3;
                    score += Count(q, @"personel|çalışan|calisan|maa[sş]|maas|payroll|bordro|payrollprofile") * 3;
                    score += Count(q, @"iban|swift|banka\s*hesap|bank\s*account|isSubjectToEInvoice|isSubjectToEShipment") * 2;
                    score += Count(q, @"pos|terminal|posterminalid|kasa|companycode") * 2;
                }

                // ---- sistem_tanim ----
                if (packId.Contains("sistem") || packId.Contains("tanim") ||
                    packName.Contains("sistem") || packName.Contains("tanım") ||
                    packName.Contains("uygulama") || packName.Contains("dil"))
                {
                    // dil, language, application, config
                    score += Count(q, @"dil|language|langcode|languagecode|locale|çeviri|ceviri") * 4;
                    score += Count(q, @"uygulama|applicationcode|application|modül|modul|feature\s*flag|konfig|config|ayar") * 3;
                    score += Count(q, @"versiyon|sürüm|surum|aktif\s*mi|enabled|disabled") * 2;
                }

                // ---- oyun_yonetimi ----
                if (packId.Contains("oyun") || packName.Contains("oyun") ||
                    packName.Contains("game") || packName.Contains("puan"))
                {
                    // kampanya, oyun, puan, görev, skor
                    score += Count(q, @"oyun|game|kampanya|campaign|görev|gorev|hedef|target") * 4;
                    score += Count(q, @"puan|point|score|skor|ödül|odul|bonus") * 3;
                    score += Count(q, @"gameid|gameline|gametype|gametypecode|gameperiyod|periyod|period") * 2;
                }

                // -------------------------------------------------
                // 2) TABLO ADI BONUSU
                // -------------------------------------------------
                var tables = (p.TablesCore ?? Array.Empty<string>()).Concat(p.TablesSatellite ?? Array.Empty<string>());
                foreach (var t in tables)
                {
                    var tLower = t.ToLowerInvariant();
                    // Soru direkt tablo adını andırıyorsa puan
                    if (Regex.IsMatch(q, $@"\b{Regex.Escape(tLower)}\b"))
                        score += 5;

                    // Nebim spesifik kolon ipuçlarını tabloya kredi ver:
                    // örn. kullanıcı "CurrAccCode" diyorsa bu tablo cari_hesap ile ilişkilidir.
                    if (tLower.StartsWith("cdcurracc") || tLower.StartsWith("prcurracc"))
                    {
                        score += Count(q, @"curracccode|curracctypecode|cari\s*kodu|cari\s*hesap") * 2;
                    }
                    if (tLower.StartsWith("trinvoice") || tLower.StartsWith("trorder"))
                    {
                        score += Count(q, @"fatura|invoice|sipariş|siparis|orderheader|orderline|invoiceheader|invoiceline") * 2;
                    }
                    if (tLower.Contains("salesperson") || tLower.Contains("workplace") || tLower.Contains("office"))
                    {
                        score += Count(q, @"satışçı|satisci|salesperson|workplacecode|officecode|mağaza|magaza|ofis|şube|sube") * 2;
                    }
                    if (tLower.StartsWith("bsapplication") || tLower.StartsWith("cddataLanguage".ToLowerInvariant()))
                    {
                        score += Count(q, @"applicationcode|uygulama|dil|language|langcode") * 2;
                    }
                    if (tLower.StartsWith("gm_") || tLower.StartsWith("gm"))
                    {
                        score += Count(q, @"oyun|game|puan|score|skor|kampanya|hedef") * 2;
                    }
                }

                results.Add((p, score));
            }

            // -------------------------------------------------
            // 3) SIRALAMA VE SEÇİM
            // -------------------------------------------------
            var ordered = results
                .OrderByDescending(x => x.Score)
                // tie-break #1: zengin pack (toplam tablo sayısı yüksek olan)
                .ThenByDescending(x =>
                    (x.Pack.TablesCore?.Count ?? 0) +
                    (x.Pack.TablesSatellite?.Count ?? 0))
                // tie-break #2: alfabetik
                .ThenBy(x => x.Pack.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var best = ordered.FirstOrDefault().Pack ?? slice.Packs.First();

            var candidates = ordered
                .Take(3)
                .Select(x => x.Pack.CategoryId)
                .ToArray();

            var reason = "Nebim domain kelimeleri (cari hesap, satış & fatura, mağaza/organizasyon, sistem tanımı, oyun/puan) + tablo adı eşleşmesi kullanılarak skorlandı.";

            return new RouteResponseDto(
                SelectedCategoryId: best.CategoryId,
                CandidateCategoryIds: candidates,
                Reason: reason
            );
        }

        private static int Count(string text, string pattern)
            => Regex.Matches(text, pattern, RegexOptions.IgnoreCase).Count;

        private static bool Contains(string? haystack, string needle)
            => (haystack ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
