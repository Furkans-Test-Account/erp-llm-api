using Api.DTOs;
using Api.Services.Abstractions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Api.Services;

public class SqlExecutor : ISqlExecutor
{
    private readonly IConfiguration _cfg;
    public SqlExecutor(IConfiguration cfg) => _cfg = cfg;

    public async Task<QueryResponse> ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cfg.GetConnectionString("Erp"));
        await conn.OpenAsync(ct);

        var rows = (await conn.QueryAsync(sql)).ToList();

        var columns = rows.Any()
            ? ((IDictionary<string, object>)rows[0]).Keys.ToList()
            : new List<string>();

        return new QueryResponse(columns, rows);
    }
}
