using System.Collections.Generic;

namespace Api.Services.Abstractions
{
    public interface ISqlValidator
    {
        // Eski TryValidate var ise kalsın, ama yeni Validate ile zengin bilgi döndürelim.
        bool TryValidate(string sql, IReadOnlyList<string> allowedTables, out string? error);

        // Yeni: detaylı sonuç döndüren API
        SqlValidationResult Validate(string sql, IReadOnlyList<string> allowedTables);
    }
}
