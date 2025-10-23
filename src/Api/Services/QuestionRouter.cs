using System.Text.RegularExpressions;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    /// <summary>
    /// Geliştirilmiş anahtar-kelime tabanlı yönlendirici.
    /// - Satış & Sevkiyat için güçlü ağırlıklar (satış/sipariş/tutar/ürün/kategori)
    /// - AI & Sohbet sinyallerini düşük ağırlıkla puanlar
    /// - Tablo adları eşleşirse bonus verir
    /// - Deterministik tie-break (yüksek skor, sonra tablo sayısı, sonra alfabetik)
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

                // ---------------------------
                // 1) İş alanı sinyalleri
                // ---------------------------

                // Satış & Sevkiyat (çok güçlü)
                score += Count(q, @"sat[ıi]ş|sipari[sş]|order|tutar|ciro|gelir|adet|miktar|quantity|price|amount|ürün|product|kategori|category|kargo|ship|sevkiyat") * 3;
                if (Contains(p.Name, "satış")) score += 6;
                if (Contains(p.Name, "sevkiyat")) score += 3;

                // Katalog & İçerik
                score += Count(q, @"product|ürün|category|kategori|review|yorum|rating|puan|stok|stock");
                if (Contains(p.Name, "katalog")) score += 3;

                // Müşteri
                score += Count(q, @"customer|müşteri|email|e-?posta|adres|address|phone|telefon");
                if (Contains(p.Name, "müşteri")) score += 3;

                // Finans (Giderler)
                score += Count(q, @"expense|gider|masraf|fatura|ödeme|maliyet|cost");
                if (Contains(p.Name, "finans")) score += 3;

                // Kullanıcı & Yetki
                score += Count(q, @"user|kullanıcı|role|yetki|login|auth|authorization|authentication");
                if (Contains(p.Name, "kullanıcı") || Contains(p.Name, "yetki")) score += 2;

                // Operasyon & Loglama
                score += Count(q, @"log|hata|error|istisna|exception|bildirim|notification|sayfa|page|ip|taray[ıi]c[ıi]|browser");
                if (Contains(p.Name, "operasyon") || Contains(p.Name, "loglama")) score += 2;

                // Pazar yeri
                score += Count(q, @"marketplace|pazar|entegrasyon|integration|api\s?key");
                if (Contains(p.Name, "pazar")) score += 2;

                // AI & Sohbet (düşük öncelik)
                score += Count(q, @"\bai\b|yapay zeka|prompt|chat|sohbet|session|message|oturum|mesaj");
                if (Contains(p.Name, "sohbet") || Contains(p.Name, "ai")) score += 1;

                // ---------------------------
                // 2) Tablo adlarına bonus
                // ---------------------------
                var tables = (p.TablesCore ?? Array.Empty<string>()).Concat(p.TablesSatellite ?? Array.Empty<string>());
                foreach (var t in tables)
                {
                    if (Regex.IsMatch(q, $@"\b{Regex.Escape(t.ToLowerInvariant())}\b"))
                        score += 3;
                }

                // Satış sorularında kritik tablolar geçiyorsa ekstra
                if (tables.Any(t => t.Equals("Orders", StringComparison.OrdinalIgnoreCase))) score += Count(q, @"sipari[sş]|order") * 2;
                if (tables.Any(t => t.Equals("OrderItems", StringComparison.OrdinalIgnoreCase))) score += Count(q, @"kalem|adet|miktar|quantity|sat[ıi]r") * 2;
                if (tables.Any(t => t.Equals("Products", StringComparison.OrdinalIgnoreCase))) score += Count(q, @"ürün|product") * 2;
                if (tables.Any(t => t.Equals("Categories", StringComparison.OrdinalIgnoreCase))) score += Count(q, @"kategori|category") * 2;

                results.Add((p, score));
            }

            // ---------------------------
            // 3) Sıralama ve seçim
            // ---------------------------
            var ordered = results
                .OrderByDescending(x => x.Score)
                // Tie-break 1: daha zengin pack (daha çok tablo)
                .ThenByDescending(x => (x.Pack.TablesCore?.Count ?? 0) + (x.Pack.TablesSatellite?.Count ?? 0))
                // Tie-break 2: isim alfabe
                .ThenBy(x => x.Pack.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var best = ordered.FirstOrDefault().Pack ?? slice.Packs.First();

            // En iyi 3 adayı döndür
            var candidates = ordered
                .Take(3)
                .Select(x => x.Pack.CategoryId)
                .ToArray();

            // Açıklama (debug amaçlı kısa)
            var reason = "Anahtar kelime + tablo-ismi bonuslu eşleştirme (v2). " +
                         "Satış/ürün/kategori/tutar sinyalleri yüksek ağırlıklı, AI/sohbet düşük ağırlıklı puanlandı.";

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
