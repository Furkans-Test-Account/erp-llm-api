namespace Api.Services.Abstractions
{
    public interface ILlmService
    {
        /// <summary>
        /// Chat Completions ile tek bir SELECT üreten yardımcı. (SQL normalize eder)
        /// </summary>
        Task<string> GetSqlAsync(string prompt, CancellationToken ct = default);

        /// <summary>
        /// Modelden ham JSON (ya da metin) döndürmek istediğimizde kullanırız (pack slicing / routing).
        /// </summary>
        Task<string> GetRawJsonAsync(string prompt, CancellationToken ct = default);

        /// <summary>
        /// Üretilen SQL bir hata verdiğinde (validasyon veya çalıştırma), hatayı ve önceki SQL’i vererek
        /// aynı bağlamda tek bir SELECT ile düzeltme ister.
        /// </summary>
        Task<string> RefineSqlAsync(
            string userQuestion,
            string previousSql,
            string errorMessage,
            IEnumerable<string> allowedTables,
            string guardrailsPrompt,
            CancellationToken ct = default);
    }
}
