namespace Api.DTOs
{
    public sealed class QueryResultDto
    {
        public List<string> Columns { get; init; } = new();
        public List<object[]> Rows { get; init; } = new();
    }
}
