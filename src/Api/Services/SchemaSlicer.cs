using System.Linq;
using System.Text.RegularExpressions;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    /// <summary>
    /// Şemayı pack'lere böler:
    /// - FK grafından bileşenler çıkarır (ilişkileri korur)
    /// - Anlamsal isimlendirme yapar (Türkçe)
    /// - Çekirdek/uydu ayrımı uygular (heuristic)
    /// - Köprüleri (bridgeRefs) hedef pack'e çözer
    /// - Aynı isimli pack'leri birleştirir (Operasyon & Loglama vb.)
    /// </summary>
    /// #DONE
    public class SchemaSlicer : ISchemaSlicer
    {
        public SliceResultDto Slice(SchemaDto schema)
        {
            // 0) Koruma
            var allTables = schema.Tables?
                .Select(t => t.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            var tablesSet = new HashSet<string>(allTables, StringComparer.OrdinalIgnoreCase);

            // 1) Yönlü FK kenarları (boşları atla)
            var directedEdges = new List<(string From, string To)>();
            foreach (var t in schema.Tables ?? Enumerable.Empty<TableDto>())
            {
                if (t.ForeignKeys != null)
                {
                    foreach (var fk in t.ForeignKeys)
                    {
                        if (!string.IsNullOrWhiteSpace(fk.FromTable) &&
                            !string.IsNullOrWhiteSpace(fk.ToTable) &&
                            tablesSet.Contains(fk.FromTable) &&
                            tablesSet.Contains(fk.ToTable))
                        {
                            directedEdges.Add((fk.FromTable, fk.ToTable));
                        }
                    }
                }
                if (t.ReferencedBy != null)
                {
                    foreach (var rb in t.ReferencedBy)
                    {
                        var from = rb.FromTable;
                        var to = t.Name;
                        if (!string.IsNullOrWhiteSpace(from) &&
                            !string.IsNullOrWhiteSpace(to) &&
                            tablesSet.Contains(from) &&
                            tablesSet.Contains(to))
                        {
                            directedEdges.Add((from, to));
                        }
                    }
                }
            }

            // 2) Bağlı bileşenler (undirected graph)
            var undirected = BuildUndirectedGraph(tablesSet, directedEdges);
            var components = GetConnectedComponents(tablesSet, undirected); // List<HashSet<string>>

            // 3) Her bileşeni ön-pack'e dönüştür (isim + core/satellite + iç FK'lar + dış köprü adayları)
            var preliminaryPacks = new List<_PrePack>();
            foreach (var comp in components)
            {
                var compEdges = directedEdges
                    .Where(e => comp.Contains(e.From) && comp.Contains(e.To))
                    .Select(e => new FkEdgeDto(e.From, e.To))
                    .Distinct()
                    .ToList();

                // Çekirdek / uydu
                var core = new List<string>();
                var satellite = new List<string>();
                foreach (var t in comp.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsSatellite(t)) satellite.Add(t);
                    else core.Add(t);
                }

                // Geçici isim ve kategori id
                var name = GuessName(comp);
                var catId = ToSlug(name);

                // Dış köprü adayları (şimdilik tablo bazlı; sonra pack'e çözeceğiz)
                var external = directedEdges
                    .Where(e => comp.Contains(e.From) && !comp.Contains(e.To))
                    .GroupBy(e => e.To, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(x => x.From).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        StringComparer.OrdinalIgnoreCase
                    );

                preliminaryPacks.Add(new _PrePack
                {
                    Name = name,
                    CategoryId = catId,
                    Core = core,
                    Satellite = satellite,
                    FkEdges = compEdges,
                    ExternalTableLinks = external, // toTable -> viaTables
                    AllTables = new HashSet<string>(comp, StringComparer.OrdinalIgnoreCase),
                    Summary = $"Bu paket {name} ile ilgili tabloları içerir: {string.Join(", ", comp.OrderBy(x => x))}. " +
                              "FK ilişkileri pack içinde korunur; pack dışına köprüler BridgeRefs ile belirtilmiştir.",
                    Grain = "Çekirdek tablolar işlem/fakt (ör. Orders, Products), uydu tablolar referans/lookup niteliğindedir."
                });
            }

            // 4) Tablodan -> pack eşlemesi (merge öncesi)
            var preTableToPackName = preliminaryPacks
                .SelectMany(p => p.AllTables.Select(t => (Table: t, PackName: p.Name)))
                .GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().PackName, StringComparer.OrdinalIgnoreCase);

            // 5) Aynı isimli pack'leri birleştir (Operasyon & Loglama gibi)
            var merged = preliminaryPacks
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var name = g.Key;
                    var catId = ToSlug(name);

                    var core = g.SelectMany(x => x.Core).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var sat = g.SelectMany(x => x.Satellite).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var edges = g.SelectMany(x => x.FkEdges).Distinct().ToList();

                    // Tüm tablolar
                    var all = new HashSet<string>(core.Concat(sat), StringComparer.OrdinalIgnoreCase);

                    // External linkleri toplayalım (hala tablo hedefli)
                    var extLinks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in g)
                    {
                        foreach (var kvp in p.ExternalTableLinks)
                        {
                            if (!extLinks.TryGetValue(kvp.Key, out var via))
                            {
                                via = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                extLinks[kvp.Key] = via;
                            }
                            foreach (var v in kvp.Value) via.Add(v);
                        }
                    }

                    return new _MergedPrePack
                    {
                        Name = name,
                        CategoryId = catId,
                        Core = core,
                        Satellite = sat,
                        FkEdges = edges,
                        AllTables = all,
                        ExternalTableLinks = extLinks, // toTable -> viaTables (Set)
                        Summary = g.First().Summary,
                        Grain = g.First().Grain
                    };
                })
                .ToList();

            // 6) Merge sonrası tablo -> pack eşlemesi (gerçek isimler)
            var tableToPackName = merged
                .SelectMany(p => p.AllTables.Select(t => (Table: t, PackName: p.Name)))
                .GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().PackName, StringComparer.OrdinalIgnoreCase);

            // 7) BridgeRefs'i pack adına çözüp son PackDto üret
            var finalPacks = new List<PackDto>();
            foreach (var m in merged)
            {
                var bridges = new List<BridgeRefDto>();

                foreach (var kvp in m.ExternalTableLinks) // kvp.Key = targetTable
                {
                    var targetTable = kvp.Key;
                    if (!tableToPackName.TryGetValue(targetTable, out var targetPackName))
                        continue; // Erişilemeyen tablo (teorik olarak olmamalı)

                    // Kendi pack'ine köprü göstermeyelim
                    if (string.Equals(targetPackName, m.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Aynı hedef pack için via tables biriktir
                    var viaTables = kvp.Value.Where(v => m.AllTables.Contains(v))
                                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                             .ToList();

                    if (viaTables.Count == 0) continue;

                    var toCategoryId = ToSlug(targetPackName);
                    var existing = bridges.FirstOrDefault(b => string.Equals(b.ToCategory, toCategoryId, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        var mergedVia = existing.ViaTables.Concat(viaTables)
                                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        bridges.Remove(existing);
                        bridges.Add(new BridgeRefDto(toCategoryId, mergedVia));
                    }
                    else
                    {
                        bridges.Add(new BridgeRefDto(toCategoryId, viaTables));
                    }
                }

                finalPacks.Add(new PackDto(
                    CategoryId: m.CategoryId,
                    Name: m.Name,
                    TablesCore: m.Core,
                    TablesSatellite: m.Satellite,
                    FkEdges: m.FkEdges, // zaten FkEdgeDto listesi
                    BridgeRefs: bridges,
                    Summary: m.Summary,
                    Grain: m.Grain
                ));
            }

            // 8) Tutarlılık (FK kenarı / köprü temizliği) — init-only record'lar için 'with' kullan
            finalPacks = finalPacks
                .Select(p =>
                {
                    var cleanedEdges = (p.FkEdges ?? new List<FkEdgeDto>())
                        .Where(e => !string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To))
                        .Where(e => !e.From.Equals(e.To, StringComparison.OrdinalIgnoreCase))
                        .Distinct()
                        .ToList();

                    var packTables = (p.TablesCore ?? Array.Empty<string>())
                        .Concat(p.TablesSatellite ?? Array.Empty<string>())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var cleanedBridges = (p.BridgeRefs ?? new List<BridgeRefDto>())
                        .Select(br => new BridgeRefDto(
                            br.ToCategory,
                            (br.ViaTables ?? new List<string>())
                                .Where(v => packTables.Contains(v))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                .ToList()
                        ))
                        .Where(br => br.ViaTables.Count > 0)
                        .Distinct()
                        .ToList();

                    return p with { FkEdges = cleanedEdges, BridgeRefs = cleanedBridges };
                })
                .ToList();

            return new SliceResultDto(schema.SchemaName, finalPacks);
        }

        // ----------------- Helpers -----------------

        private static Dictionary<string, HashSet<string>> BuildUndirectedGraph(HashSet<string> nodes, List<(string From, string To)> directed)
        {
            var g = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes) g[n] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (from, to) in directed)
            {
                if (!g.ContainsKey(from)) g[from] = new(StringComparer.OrdinalIgnoreCase);
                if (!g.ContainsKey(to)) g[to] = new(StringComparer.OrdinalIgnoreCase);
                g[from].Add(to);
                g[to].Add(from);
            }
            return g;
        }

        private static List<HashSet<string>> GetConnectedComponents(HashSet<string> nodes, Dictionary<string, HashSet<string>> undirected)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var res = new List<HashSet<string>>();
            foreach (var start in nodes)
            {
                if (visited.Contains(start)) continue;
                var comp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var st = new Stack<string>();
                st.Push(start);
                visited.Add(start);
                while (st.Count > 0)
                {
                    var cur = st.Pop();
                    comp.Add(cur);
                    if (undirected.TryGetValue(cur, out var neigh))
                    {
                        foreach (var n in neigh)
                            if (visited.Add(n)) st.Push(n);
                    }
                }
                res.Add(comp);
            }
            return res;
        }

        private static string GuessName(HashSet<string> comp)
        {
            var joined = string.Join(" ", comp).ToLowerInvariant();

            // Satış & Sevkiyat
            if (Regex.IsMatch(joined, @"\border(s)?\b|\borderitems?\b|\bshipper(s)?\b|\bsipari[sş]\b|\bsevkiyat\b"))
                return "Satış & Sevkiyat";

            // Katalog & İçerik
            if (Regex.IsMatch(joined, @"\bproduct(s)?\b|\bcategories?\b|\breview(s)?\b|\bürün\b|\bkategori\b|\byorum\b"))
                return "Katalog & İçerik";

            // Müşteri
            if (Regex.IsMatch(joined, @"\bcustomer(s)?\b|\bmüşteri\b"))
                return "Müşteri";

            // Finans (Giderler)
            if (Regex.IsMatch(joined, @"\bexpense(s)?\b|\bexpensecategories?\b|\bgider\b|\bmasraf\b"))
                return "Finans (Giderler)";

            // Kullanıcı & Yetki
            if (Regex.IsMatch(joined, @"\buser(role)?\b|\brole(s)?\b|\byetki\b|\bkullanıcı\b"))
                return "Kullanıcı & Yetki";

            // Operasyon & Loglama
            if (Regex.IsMatch(joined, @"\berrorlog\b|\buseractivitylog\b|\bnotifications?\b|\baiquerylog\b|\blog\b|\berror\b"))
                return "Operasyon & Loglama";

            // Pazar Yeri Entegrasyonları
            if (Regex.IsMatch(joined, @"\bmarketplaceintegration(s)?\b|\bpazar\b|\bentegrasyon\b"))
                return "Pazar Yeri Entegrasyonları";

            // AI & Sohbet
            if (Regex.IsMatch(joined, @"\bchat(messages?|sessions?)\b|\bsohbet\b|\bai\b"))
                return "AI & Sohbet";

            return "Genel Modül";
        }

        private static bool IsSatellite(string table)
        {
            // Küçük referans/lookup/metadata tablolarını uydu say
            return Regex.IsMatch(table, @"\bCategories?\b|\bShippers?\b|\bNotifications?\b", RegexOptions.IgnoreCase);
        }

        private static string ToSlug(string s)
        {
            var lower = s.ToLowerInvariant();
            lower = lower.Replace('&', ' ').Replace('/', ' ').Replace('\\', ' ');
            lower = Regex.Replace(lower, @"[^a-z0-9\s]+", "");
            lower = Regex.Replace(lower, @"\s+", "_").Trim('_');
            return lower;
        }

        // ----- internal temp types -----
        private sealed class _PrePack
        {
            public required string Name { get; init; }
            public required string CategoryId { get; init; }
            public required List<string> Core { get; init; }
            public required List<string> Satellite { get; init; }
            public required List<FkEdgeDto> FkEdges { get; init; }
            public required Dictionary<string, List<string>> ExternalTableLinks { get; init; }  // toTable -> viaTables
            public required HashSet<string> AllTables { get; init; }
            public required string Summary { get; init; }
            public required string Grain { get; init; }
        }

        private sealed class _MergedPrePack
        {
            public required string Name { get; init; }
            public required string CategoryId { get; init; }
            public required List<string> Core { get; init; }
            public required List<string> Satellite { get; init; }
            public required List<FkEdgeDto> FkEdges { get; init; }
            public required HashSet<string> AllTables { get; init; }
            public required Dictionary<string, HashSet<string>> ExternalTableLinks { get; init; } // toTable -> viaTables
            public required string Summary { get; init; }
            public required string Grain { get; init; }
        }
    }
}
