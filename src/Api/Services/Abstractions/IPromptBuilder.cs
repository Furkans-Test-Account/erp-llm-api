using Api.DTOs;

namespace Api.Services.Abstractions
{
    /// <summary>
    /// LLM prompt oluşturucu:
    /// 1) Şemayı kategorilere (pack'lere) ayırmak için slicing prompt'u üretir.
    /// 2) Seçilen pack'e göre SQL üretim prompt'u oluşturur.
    /// </summary>
    public interface IPromptBuilder
    {
        /// <summary>
        /// LLM'in şemayı kategorilere ayırması (schema slicing) için kullanılacak prompt'u üretir.
        /// </summary>
        string BuildSchemaSlicingPrompt(SchemaDto schema, int maxPacks = 12);

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

        
        string BuildRoutingPrompt(SliceResultDto sliced, string userQuestion, int topK = 3); // adım 2’de kullanacağız
       
    }

}
