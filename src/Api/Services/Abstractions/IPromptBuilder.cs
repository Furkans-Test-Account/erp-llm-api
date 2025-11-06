using Api.DTOs;

namespace Api.Services.Abstractions
{
    /// <summary>
    /// LLM prompt oluşturucu:
    /// 1) Şemayı kategorilere (pack'lere) ayırmak için slicing prompt'u üretir.
    /// 2) Seçilen pack'e göre SQL üretim prompt'u oluşturur.
    /// 3) CategoryId ile hızlı pack-seçip (adjacent opsiyonlu) prompt üretir.
    /// </summary>
    public interface IPromptBuilder
    {



        /// <summary>
        /// Seçilen pack (ve opsiyonel komşu pack) bağlamında, kullanıcı sorusuna karşılık
        /// sadece izinli tabloları kullanarak SQL üreten prompt'u oluşturur.
        /// </summary>
        string BuildPromptForPack(
            string userQuestion,
            PackDto pack,
            SchemaDto fullSchema,
            IReadOnlyList<PackDto>? adjacentPacks = null,
            string sqlDialect = "SQL Server (T-SQL)",
            bool requireSingleSelect = true,
            bool forbidDml = true,
            bool preferAnsi = true);

        /// <summary>
        /// Department-aware pack prompt (uses DepartmentPackDto and department adjacents).
        /// </summary>
        string BuildPromptForPack(
            string userQuestion,
            DepartmentPackDto deptPack,
            SchemaDto fullSchema,
            IReadOnlyList<DepartmentPackDto>? adjacentPacks = null,
            string sqlDialect = "SQL Server (T-SQL)",
            bool requireSingleSelect = true,
            bool forbidDml = true,
            bool preferAnsi = true);

        /// <summary>
        /// CategoryId ile doğrudan pack seçerek (opsiyonel adjacent kategorilerle) prompt üretir.
        /// UI'da departman seçimine uygun kısayol.
        /// </summary>
        string BuildPromptForCategory(
            string userQuestion,
            DepartmentSliceResultDto deptSlice,
            string categoryId,
            SchemaDto fullSchema,
            IReadOnlyList<string>? adjacentCategoryIds = null,
            string sqlDialect = "SQL Server (T-SQL)");

        /// <summary>
        /// Soru -> pack yönlendirme prompt'u (topK aday döner).
        /// </summary>
        string BuildRoutingPrompt(SliceResultDto sliced, string userQuestion, int topK = 3);
    }
}
