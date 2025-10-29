using System.Text;
using Api.DTOs;                    
using Api.Services.Abstractions;   

namespace Api.Services
{
    /// <summary>
    /// İlk SQL üretimini yapar; validasyon veya çalıştırma hatası olursa
    /// LLM'e hata mesajı ve izinli tablolarla geri dönüp tek bir SELECT
    /// üretmesini isteyen self-heal/refine döngüsünü işletir.
    /// </summary>
    public class SelfHealingSqlRunner
    {
        private readonly ILlmService _llm;
        private readonly ISqlValidator _validator;
        private readonly ISqlExecutor _exec;

        public SelfHealingSqlRunner(ILlmService llm, ISqlValidator validator, ISqlExecutor exec)
        {
            _llm = llm;
            _validator = validator;
            _exec = exec;
        }


        public async Task<(string FinalSql, QueryResponse Result)> RunAsync(
            string promptForPack,
            string userQuestion,
            IEnumerable<string> allowedTables,
            string? guardrailsPrompt = null,
            int maxRetries = 2,
            CancellationToken ct = default)
        {
            if (maxRetries < 0) maxRetries = 0;


            var sql = await _llm.GetSqlAsync(promptForPack, ct);


            var attempt = 0;
            while (true)
            {
                attempt++;

                //  Validasyon
                if (!_validator.TryValidate(sql, allowedTables.ToList(), out var validationError))
                {
                    if (attempt > maxRetries + 1)
                        throw new InvalidOperationException(BuildExhaustedMessage("Validation failed", sql, validationError));

                    sql = await _llm.RefineSqlAsync(
                        userQuestion: userQuestion,
                        previousSql: sql,
                        errorMessage: $"VALIDATION ERROR: {validationError}",
                        allowedTables: allowedTables,
                        guardrailsPrompt: guardrailsPrompt ?? string.Empty,
                        ct: ct);
                    continue;
                }

                // EXEC
                try
                {
                    var result = await _exec.ExecuteAsync(sql, ct);
                    return (sql, result);
                }
                catch (Exception ex)
                {

                    if (attempt > maxRetries + 1)
                        throw new InvalidOperationException(BuildExhaustedMessage("Execution failed", sql, ex.Message), ex);

                    sql = await _llm.RefineSqlAsync(
                        userQuestion: userQuestion,
                        previousSql: sql,
                        errorMessage: $"EXECUTION ERROR: {BuildBriefExecutionError(ex)}",
                        allowedTables: allowedTables,
                        guardrailsPrompt: guardrailsPrompt ?? string.Empty,
                        ct: ct);
                }
            }
        }

        private static string BuildExhaustedMessage(string stage, string sql, string error)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Self-heal exhausted. Stage: {stage}");
            sb.AppendLine("Last SQL:");
            sb.AppendLine(sql);
            sb.AppendLine("Last error:");
            sb.AppendLine(error);
            return sb.ToString();
        }

        private static string BuildBriefExecutionError(Exception ex)
        {
            // mesaji kisa hale getir
            var msgs = new List<string>();
            var cur = ex;
            while (cur != null)
            {
                if (!string.IsNullOrWhiteSpace(cur.Message))
                    msgs.Add(cur.Message.Trim());
                cur = cur.InnerException;
            }
            var joined = string.Join(" | ", msgs.Distinct());
            return joined.Length > 400 ? joined.Substring(0, 400) + "..." : joined;
        }
    }
}
