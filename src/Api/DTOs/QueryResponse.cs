
namespace Api.DTOs;
public record QueryResponse(IReadOnlyList<string> Columns, IReadOnlyList<object> Rows);
