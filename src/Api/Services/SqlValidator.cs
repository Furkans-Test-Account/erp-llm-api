using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using Api.Services.Abstractions;

namespace Api.Services
{
    /// <summary>
    /// Basit denetleyici:
    /// - Sadece SELECT
    /// - Tek statement
    /// - Sadece izinli tablolar
    /// - Id = 'text' yasak
    /// - Basit sentaks kontrolleri
    /// </summary>
    public sealed class SqlValidator : ISqlValidator
    {
       
        private static readonly Regex RxDisallowed =
            new(@"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|CREATE|ALTER|DROP|EXEC|EXECUTE)\b",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        
        private static readonly Regex RxSelectStart =
            new(@"^\s*SELECT\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

       
        private static readonly Regex RxMultipleStatements =
            new(@";\s*\S", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        
        private static readonly Regex RxFromJoinTables =
            new(@"\b(FROM|JOIN)\s+([A-Za-z_][A-Za-z0-9_\.\[\]]+)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

       
        private static readonly Regex RxIdEqualsString =
            new(@"\b([A-Za-z_][A-Za-z0-9_]*Id)\s*=\s*'[^']+'",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public bool TryValidate(string sql, IReadOnlyList<string> allowedTables, out string? error)
        {
            var r = Validate(sql, allowedTables);
            error = r.IsValid ? null : $"{r.Kind}: {r.Message}";
            return r.IsValid;
        }

        public SqlValidationResult Validate(string sql, IReadOnlyList<string> allowedTables)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return Fail(SqlErrorKind.SyntaxSuspicious, "Empty SQL.");

            if (!RxSelectStart.IsMatch(sql))
                return Fail(SqlErrorKind.DisallowedDml, "Only a single SELECT statement is allowed.");

            if (RxDisallowed.IsMatch(sql))
                return Fail(SqlErrorKind.DisallowedDml, "DML/DDL keywords are not allowed.");

            if (RxMultipleStatements.IsMatch(sql))
                return Fail(SqlErrorKind.MultipleStatements, "Only one single SELECT statement is allowed.");

            if (RxIdEqualsString.IsMatch(sql))
                return Fail(SqlErrorKind.IdVsStringCompare,
                    "String literal compared to an Id column. Join to the lookup table and filter by its TEXT column.");

            // check allowed tables only if a whitelist is provided
            if (allowedTables != null && allowedTables.Count > 0)
            {
                var usedTables = ExtractTables(sql);
                foreach (var t in usedTables)
                {
                    var bare = NormalizeTableName(t);
                    if (!IsAllowed(bare, allowedTables))
                        return Fail(SqlErrorKind.DisallowedTable,
                            $"Table '{bare}' is not allowed. Allowed: {string.Join(", ", allowedTables)}");
                }
            }

            // FROM check
            if (!Regex.IsMatch(sql, @"\bFROM\b", RegexOptions.IgnoreCase))
                return Fail(SqlErrorKind.SyntaxSuspicious, "Query seems to be missing a FROM clause.");

            return new SqlValidationResult(true, SqlErrorKind.None, null);
        }

        // --- helpers ---

        private static IEnumerable<string> ExtractTables(string sql)
        {
            foreach (Match m in RxFromJoinTables.Matches(sql))
                if (m.Success && m.Groups.Count >= 3)
                    yield return m.Groups[2].Value;
        }

        private static string NormalizeTableName(string raw)
        {
            var s = raw.Trim().Trim('[', ']');
            var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length > 0 ? parts[^1] : s;

            var spaceIx = name.IndexOf(' ');
            if (spaceIx > 0) name = name[..spaceIx];

            return name.Trim('[', ']');
        }

        private static bool IsAllowed(string tableName, IReadOnlyList<string> allowedTables) =>
            allowedTables.Any(t => string.Equals(t, tableName, StringComparison.OrdinalIgnoreCase));

        private static SqlValidationResult Fail(SqlErrorKind kind, string message) =>
            new(false, kind, message);
    }
}
