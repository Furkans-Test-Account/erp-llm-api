
namespace Api.DTOs;

public record ColumnDto(
    string Name,
    string DataType,
    bool IsNullable,
    string? Description
);

public record ForeignKeyDto(
    string ConstraintName,
    string FromTable,
    IReadOnlyList<string> FromColumns,
    string ToTable,
    IReadOnlyList<string> ToColumns
);

public record TableDto(
    string Name,
    string? Description,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<ForeignKeyDto> ForeignKeys,
     IReadOnlyList<ReferencedByDto>? ReferencedBy = null
);

public record SchemaDto(
    string SchemaName,
    IReadOnlyList<TableDto> Tables
);

public record ReferencedByDto(
    string ConstraintName,
    string FromTable,                 
    IReadOnlyList<string> FromColumns,
    IReadOnlyList<string> ToColumns   
);


