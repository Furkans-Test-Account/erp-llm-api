
using Api.DTOs;
namespace Api.Services.Abstractions;
public interface ISqlExecutor
{
    Task<QueryResponse> ExecuteAsync(string sql, CancellationToken ct);
}
