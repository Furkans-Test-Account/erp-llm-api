// File: src/Api/Services/StrictSchemaSlicer.cs
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    /// <summary>
    /// LLM kullanmadan, policy/prefix tabanlı deterministik departman slicer.
    /// Bu sürüm:
    /// - TablesCore (prefix) doldurur
    /// - FkEdges (pack içi) üretir (mümkünse Table.Column seviyesinde)
    /// - BridgeRefs (pack -> başka departman) üretir (ViaTables = lokal dokunan tablolar)
    /// - AllowedRefViews (Code/Name otomatik çözümlemeli) üretir
    /// </summary>
    public sealed class StrictSchemaSlicer : IStrictDepartmentSlicer
    {
        public Task<DepartmentSliceResultDto> SliceAsync(
            SchemaDto schema,
            DepartmentSliceOptions options,
            CancellationToken ct = default)
        {
            options ??= new DepartmentSliceOptions(Array.Empty<DepartmentPolicy>());
            var policies = options.Policies ?? Array.Empty<DepartmentPolicy>();
            var refPrefixes = options.EffectiveRefTargetsPrefixes; // genelde {"cd","df","bs"}

            // --- 0) Hızlı indeksler ---
            var tables = schema.Tables ?? new List<TableDto>();
            var tableByName = tables
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

            // Tüm tablolar için “hangi departmana ait?” haritası (prefix policy ile)
            var tableDeptMap = BuildTableDepartmentMap(tables, policies);

            var deptPacks = new List<DepartmentPackDto>();

            foreach (var pol in policies)
            {
                ct.ThrowIfCancellationRequested();

                var include = pol.IncludePrefixes ?? Array.Empty<string>();
                var exclude = pol.ExcludePrefixes ?? Array.Empty<string>();

                // 1) Çekirdek: prefix filtresi
                var core = tables
                    .Where(t =>
                        include.Any(pref => t.Name.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) &&
                       !exclude.Any(pref => t.Name.StartsWith(pref, StringComparison.OrdinalIgnoreCase)))
                    .Select(t => t.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList();

                var coreSet = core.ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 2) Pack içi FK’lar (FkEdges)
                var fkEdges = new List<FkEdgeDto>();
                foreach (var srcName in core)
                {
                    if (!tableByName.TryGetValue(srcName, out var src)) continue;

                    foreach (var fk in GetForeignKeysEnumerable(src) ?? Array.Empty<object>())
                    {
                        var toTable = GetFkTargetTableName(fk);
                        if (string.IsNullOrWhiteSpace(toTable)) continue;
                        toTable = NormalizeTableName(toTable!);

                        // sadece pack içi edge
                        if (coreSet.Contains(toTable))
                        {
                            var edge = BuildEdgeWithColumns(srcName, fk, toTable);
                            if (edge != null) fkEdges.Add(edge);
                        }
                    }
                }

                // 3) CandidateRefs: core -> cd*/df*/bs* hedefleri (sözlük/parametre) (gating + AllowedRefViews için)
                var candidateRefBag = new Dictionary<string, (int score, HashSet<string> viaTables)>(StringComparer.OrdinalIgnoreCase);
                foreach (var srcName in core)
                {
                    if (!tableByName.TryGetValue(srcName, out var src)) continue;

                    foreach (var fk in GetForeignKeysEnumerable(src) ?? Array.Empty<object>())
                    {
                        var toTable = GetFkTargetTableName(fk);
                        if (string.IsNullOrWhiteSpace(toTable)) continue;
                        toTable = NormalizeTableName(toTable!);

                        if (!refPrefixes.Any(pref => toTable.StartsWith(pref, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        if (!candidateRefBag.TryGetValue(toTable, out var item))
                            item = (0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                        if (item.viaTables.Add(srcName))
                            item.score++;

                        candidateRefBag[toTable] = item;
                    }
                }

                var candidateRefs = candidateRefBag
                    .OrderByDescending(kv => kv.Value.score)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(0, options.MaxCandidateRefsPerPack))
                    .Select(kv => new CandidateRefDto(
                        TargetTable: kv.Key,
                        Reason: $"Referenced by {string.Join(", ", kv.Value.viaTables.Take(3))}{(kv.Value.viaTables.Count > 3 ? "..." : "")}",
                        Score: kv.Value.score))
                    .ToList();

                // 4) AllowedRefViews: gerçek Code/Name kolonlarını schema’dan çöz
                var allowedRefViews = new List<RefViewSpecDto>();
                foreach (var cr in candidateRefs.Take(10)) // token koruması
                {
                    var (codeCol, nameCol) = ResolveCodeNameColumns(cr.TargetTable, tableByName);
                    allowedRefViews.Add(new RefViewSpecDto(
                        Name: $"{pol.DeptId}_ref_{Shorten(cr.TargetTable)}",
                        SourceTable: cr.TargetTable,
                        Columns: new[] { codeCol, nameCol }
                    ));
                }

                // 5) BridgeRefs: core’daki tablolar → başka departmana ait tablolara FK atıyorsa
                var bridgeMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); // toDept -> viaTables
                foreach (var srcName in core)
                {
                    if (!tableByName.TryGetValue(srcName, out var src)) continue;

                    foreach (var fk in GetForeignKeysEnumerable(src) ?? Array.Empty<object>())
                    {
                        var toTable = GetFkTargetTableName(fk);
                        if (string.IsNullOrWhiteSpace(toTable)) continue;
                        toTable = NormalizeTableName(toTable!);

                        // Hedef tablo farklı bir departmana mı ait?
                        if (tableDeptMap.TryGetValue(toTable, out var toDept) &&
                            !string.Equals(toDept, pol.DeptId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!bridgeMap.TryGetValue(toDept!, out var via))
                            {
                                via = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                bridgeMap[toDept!] = via;
                            }
                            via.Add(srcName); // köprü “via” = kaynak core tablo
                        }
                    }
                }

                var bridges = bridgeMap
                    .Select(kv => new BridgeRefDto(
                        ToCategory: kv.Key,
                        ViaTables: kv.Value
                            .Where(v => coreSet.Contains(v))
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .ToList()))
                    .Where(br => br.ViaTables.Count > 0)
                    .Distinct()
                    .ToList();

                // 6) FkEdges temizlik (benzerleri at, self-edge yok)
                fkEdges = fkEdges
                    .Where(e => !string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To))
                    .Where(e => !e.From.Equals(e.To, StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList();

                // 7) Pack DTO
                var pack = new DepartmentPackDto
                {
                    CategoryId = pol.DeptId,
                    Name = pol.Name,
                    TablesCore = core,
                    TablesSatellite = new List<string>(), // istenirse ek heuristic yazılabilir
                    FkEdges = fkEdges.Cast<object>().ToList(),   // DepartmentPackDto bazen object list tutuyordu
                    BridgeRefs = bridges.Cast<object>().ToList(),
                    CandidateRefs = candidateRefs,
                    AllowedRefViews = allowedRefViews
                };

                deptPacks.Add(pack);
            }

            var result = new DepartmentSliceResultDto(schema.SchemaName, deptPacks);
            return Task.FromResult(result);
        }

        // ------------------------- Helpers -------------------------

        private static Dictionary<string, string> BuildTableDepartmentMap(
            IReadOnlyList<TableDto> tables,
            IReadOnlyList<DepartmentPolicy> policies)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in tables)
            {
                var name = t.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                foreach (var p in policies)
                {
                    var include = p.IncludePrefixes ?? Array.Empty<string>();
                    var exclude = p.ExcludePrefixes ?? Array.Empty<string>();

                    if (include.Any(pref => name.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) &&
                       !exclude.Any(pref => name.StartsWith(pref, StringComparison.OrdinalIgnoreCase)))
                    {
                        map[name] = p.DeptId;
                        break;
                    }
                }
            }

            return map;
        }

        /// Güvenli FK enumerasyonu (farklı şema modellerinde isimler değişebiliyor)
        private static IEnumerable? GetForeignKeysEnumerable(object tableDto)
        {
            var t = tableDto.GetType();
            var fkProp =
                t.GetProperty("ForeignKeys") ??
                t.GetProperty("ForeignKeyList") ??
                t.GetProperty("Fks") ??
                t.GetProperty("FKs");
            if (fkProp == null) return null;

            var val = fkProp.GetValue(tableDto);
            return val as IEnumerable;
        }

        /// FK hedef tablo adını çöz (çeşitli muhtemel isimler/şekiller)
        private static string? GetFkTargetTableName(object fk)
        {
            var t = fk.GetType();

            string[] directNames = new[]
            {
                "ReferencesTable", "ReferencedTable", "ReferenceTable",
                "PrincipalTable", "TargetTable", "ToTable",
                "ForeignTable", "PkTable", "PrimaryTable", "RefTable"
            };

            foreach (var name in directNames)
            {
                var p = t.GetProperty(name);
                if (p == null) continue;

                var v = p.GetValue(fk);
                if (v is string s && !string.IsNullOrWhiteSpace(s))
                    return s;

                if (v != null)
                {
                    var nameProp = v.GetType().GetProperty("Name");
                    if (nameProp != null)
                    {
                        var s2 = nameProp.GetValue(v) as string;
                        if (!string.IsNullOrWhiteSpace(s2))
                            return s2;
                    }
                }
            }

            var alt = t.GetProperty("Referenced") ?? t.GetProperty("Principal");
            if (alt != null)
            {
                var obj = alt.GetValue(fk);
                if (obj != null)
                {
                    var tableName =
                        (obj.GetType().GetProperty("Table")?.GetValue(obj) as string) ??
                        (obj.GetType().GetProperty("TableName")?.GetValue(obj) as string) ??
                        (obj.GetType().GetProperty("Name")?.GetValue(obj) as string);
                    if (!string.IsNullOrWhiteSpace(tableName))
                        return tableName;
                }
            }

            var colsProp = t.GetProperty("Columns") ?? t.GetProperty("ForeignKeyColumns");
            if (colsProp != null)
            {
                var cols = colsProp.GetValue(fk) as IEnumerable;
                if (cols != null)
                {
                    foreach (var c in cols)
                    {
                        var ct = c.GetType();
                        var cand =
                            (ct.GetProperty("ReferencedTable")?.GetValue(c) as string) ??
                            (ct.GetProperty("TargetTable")?.GetValue(c) as string) ??
                            (ct.GetProperty("ToTable")?.GetValue(c) as string) ??
                            (ct.GetProperty("Table")?.GetValue(c) as string) ??
                            (ct.GetProperty("TableName")?.GetValue(c) as string) ??
                            (ct.GetProperty("Name")?.GetValue(c) as string);

                        if (!string.IsNullOrWhiteSpace(cand))
                            return cand;
                    }
                }
            }

            return null;
        }

        /// Mümkünse Table.Column seviyesinde kenar üret; değilse tablo seviyesine düş.
        private static FkEdgeDto? BuildEdgeWithColumns(string fromTable, object fk, string toTable)
        {
            var (fromCol, toCol) = GetFirstColumnPair(fk);
            if (!string.IsNullOrWhiteSpace(fromCol) && !string.IsNullOrWhiteSpace(toCol))
            {
                return new FkEdgeDto($"{fromTable}.{fromCol}", $"{toTable}.{toCol}");
            }

            // Kolon yakalanamazsa tablo seviyesinde yine de bir edge verelim
            return new FkEdgeDto(fromTable, toTable);
        }

        /// FK’nin ilk kolon çifti (From/To) bulunabiliyorsa döndür.
        private static (string? fromCol, string? toCol) GetFirstColumnPair(object fk)
        {
            var t = fk.GetType();

            // Yaygın: Columns[i] => { FromColumn/Column, ToColumn/ReferencedColumn/PKColumn ... }
            var colsProp = t.GetProperty("Columns") ?? t.GetProperty("ForeignKeyColumns");
            if (colsProp != null)
            {
                if (colsProp.GetValue(fk) is IEnumerable cols)
                {
                    foreach (var c in cols)
                    {
                        var ct = c.GetType();
                        var fromCol =
                            (ct.GetProperty("FromColumn")?.GetValue(c) as string) ??
                            (ct.GetProperty("Column")?.GetValue(c) as string) ??
                            (ct.GetProperty("FkColumn")?.GetValue(c) as string) ??
                            (ct.GetProperty("ChildColumn")?.GetValue(c) as string) ??
                            (ct.GetProperty("Name")?.GetValue(c) as string);

                        var toCol =
                            (ct.GetProperty("ToColumn")?.GetValue(c) as string) ??
                            (ct.GetProperty("ReferencedColumn")?.GetValue(c) as string) ??
                            (ct.GetProperty("PkColumn")?.GetValue(c) as string) ??
                            (ct.GetProperty("ParentColumn")?.GetValue(c) as string) ??
                            (ct.GetProperty("RefColumn")?.GetValue(c) as string);

                        if (!string.IsNullOrWhiteSpace(fromCol) && !string.IsNullOrWhiteSpace(toCol))
                            return (fromCol, toCol);
                    }
                }
            }

            // Alternatif: Child/Parent özellikleri
            var childCol =
                (t.GetProperty("ChildColumn")?.GetValue(fk) as string) ??
                (t.GetProperty("FkColumn")?.GetValue(fk) as string) ??
                (t.GetProperty("Column")?.GetValue(fk) as string);

            var parentCol =
                (t.GetProperty("ParentColumn")?.GetValue(fk) as string) ??
                (t.GetProperty("PkColumn")?.GetValue(fk) as string) ??
                (t.GetProperty("ReferencedColumn")?.GetValue(fk) as string);

            return (childCol, parentCol);
        }

        /// "dbo.Table" -> "Table"
        private static string NormalizeTableName(string name)
        {
            var dot = name.IndexOf('.');
            return dot >= 0 ? name[(dot + 1)..] : name;
        }

        /// "cdCountry" -> "Country"
        private static string Shorten(string table)
        {
            foreach (var k in new[] { "cd", "df", "bs" })
                if (table.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                    return table.Substring(k.Length);
            return table;
        }

        /// AllowedRefViews için Code/Name kolonlarını şemadan çözmeye çalış.
        private static (string codeCol, string nameCol) ResolveCodeNameColumns(
            string tableName,
            IReadOnlyDictionary<string, TableDto> tableByName)
        {
            const string DefaultCode = "Code";
            const string DefaultName = "Name";

            if (!tableByName.TryGetValue(tableName, out var t) || t.Columns == null || t.Columns.Count == 0)
                return (DefaultCode, DefaultName);

            var cols = t.Columns
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (cols.Count == 0) return (DefaultCode, DefaultName);

            // Öncelikli adaylar (TR & EN) — güçlendirilmiş regex'ler
            string? code = cols.FirstOrDefault(n =>
                Regex.IsMatch(n, @"\b(Code|Kod|Kodu|[A-Za-z0-9]+Code)\b", RegexOptions.IgnoreCase));
            string? name = cols.FirstOrDefault(n =>
                Regex.IsMatch(n, @"\b(Name|Ad[ıi]?|A[çc]ıklama|Description|Title|ShortName|LongName)\b", RegexOptions.IgnoreCase));

            // Fallback: "Code"/"Name" birebir varsa onları kullan
            code ??= cols.FirstOrDefault(n => n.Equals("Code", StringComparison.OrdinalIgnoreCase));
            name ??= cols.FirstOrDefault(n => n.Equals("Name", StringComparison.OrdinalIgnoreCase));

            // Hâlâ yoksa: ilk iki uygun kolon
            code ??= cols.FirstOrDefault();
            name ??= cols.Skip(1).FirstOrDefault() ?? cols.First();

            return (code!, name!);
        }
    }
}
