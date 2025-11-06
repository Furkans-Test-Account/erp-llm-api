using Api.DTOs;

namespace Api.Services.Abstractions
{
    /// <summary>
    /// LLM prompt oluşturucu: seçilen prompt şablonuna göre SQL üretim prompt'u oluşturur.
    /// </summary>
    public interface IPromptBuilder
    {
        /// <summary>
        /// Kullanıcı sorusu için şablon tabanlı SQL üretim prompt'u oluşturur.
        /// Pack ve schema parametreleri geçiş süreci için korunmuştur; şu an şablon göz önüne alınır.
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
    }
}
