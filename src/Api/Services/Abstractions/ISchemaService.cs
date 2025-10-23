
using Api.DTOs;
namespace Api.Services.Abstractions;
public interface ISchemaService
{
    Task<SchemaDto> GetSchemaAsync(CancellationToken ct);
}
