using System.Data;
using Api.DTOs;
using Api.Services.Abstractions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Api.Services;

/// <summary>
/// SQL Server: tablolar, kolonlar, PK ve FK ilişkileri + açıklamalar (extended properties)
/// </summary>
public class SchemaServiceSqlServer : ISchemaService
{
    private readonly IConfiguration _cfg;
    public SchemaServiceSqlServer(IConfiguration cfg) => _cfg = cfg;

    public async Task<SchemaDto> GetSchemaAsync(CancellationToken ct)
    {
        var cs = _cfg.GetConnectionString("Erp")
                 ?? throw new InvalidOperationException("ConnectionStrings:Erp missing.");
        var schema = _cfg.GetSection("Schema")["SearchPath"] ?? "dbo";

        using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);

        // 1) tablolar ve aciklamalar #DONE
        var tables = (await conn.QueryAsync<(string table_name, string? table_comment)>(@"
            SELECT t.name AS table_name,
                   CAST(ep.value AS nvarchar(4000)) AS table_comment
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            OUTER APPLY (
                SELECT TOP(1) ep.value
                FROM sys.extended_properties ep
                WHERE ep.major_id = t.object_id
                  AND ep.minor_id = 0
                  AND ep.class_desc = 'OBJECT_OR_COLUMN'
                  AND ep.name IN ('MS_Description','Description')
            ) ep
            WHERE s.name = @schema
            ORDER BY t.name;
        ", new { schema })).ToList();

        // 2) Columns + data type + nullability + descriptions
        var columns = (await conn.QueryAsync<(string table_name, string column_name, string data_type, bool is_nullable, string? column_comment)>(@"
            SELECT 
                t.name AS table_name,
                c.name AS column_name,
                CASE 
                  WHEN ty.name IN ('varchar','char','varbinary','binary','nvarchar','nchar')
                       THEN ty.name + '(' + CASE WHEN c.max_length = -1 THEN 'max'
                           ELSE CASE WHEN ty.name LIKE 'n%' THEN CAST(c.max_length/2 AS varchar(10))
                                     ELSE CAST(c.max_length AS varchar(10)) END END + ')'
                  WHEN ty.name IN ('decimal','numeric')
                       THEN ty.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                  ELSE ty.name
                END AS data_type,
                CASE WHEN c.is_nullable = 1 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS is_nullable,
                CAST(ep.value AS nvarchar(4000)) AS column_comment
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            OUTER APPLY (
                SELECT TOP(1) ep.value
                FROM sys.extended_properties ep
                WHERE ep.major_id = t.object_id
                  AND ep.minor_id = c.column_id
                  AND ep.class_desc = 'OBJECT_OR_COLUMN'
                  AND ep.name IN ('MS_Description','Description')
            ) ep
            WHERE s.name = @schema
            ORDER BY t.name, c.column_id;
        ", new { schema })).ToList();

        // 3)PK'LER
        var pks = (await conn.QueryAsync<(string table_name, string constraint_name, string column_name, int ordinal)>(@"
            SELECT
                t.name AS table_name,
                kc.name AS constraint_name,
                c.name AS column_name,
                ic.key_ordinal AS ordinal
            FROM sys.key_constraints kc
            JOIN sys.tables t ON t.object_id = kc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.[type] = 'PK' AND s.name = @schema
            ORDER BY t.name, ic.key_ordinal;
        ", new { schema })).ToList();

        // 4) FK'LER
        var fks = (await conn.QueryAsync<(string constraint_name, string from_table, string from_column, string to_table, string to_column, int ordinal)>(@"
            SELECT
                fk.name AS constraint_name,
                tp.name AS from_table,
                cp.name AS from_column,
                tr.name AS to_table,
                cr.name AS to_column,
                fkc.constraint_column_id AS ordinal
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
            JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
            JOIN sys.schemas sp ON sp.schema_id = tp.schema_id
            JOIN sys.schemas sr ON sr.schema_id = tr.schema_id
            JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
            WHERE sp.name = @schema AND sr.name = @schema
            ORDER BY fk.name, fkc.constraint_column_id;
        ", new { schema })).ToList();

        // Map
        var tableDtos = new List<TableDto>();

        foreach (var t in tables)
        {
            var cols = columns.Where(x => x.table_name.Equals(t.table_name, StringComparison.OrdinalIgnoreCase))
                              .Select(x => new ColumnDto(x.column_name, x.data_type, x.is_nullable, x.column_comment))
                              .ToList();

            var pkCols = pks.Where(x => x.table_name.Equals(t.table_name, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(x => x.ordinal)
                            .Select(x => x.column_name)
                            .ToList();

            // Outbound FK’ler (bu tablodan başka tabloya)
            var outbound = fks.Where(x => x.from_table.Equals(t.table_name, StringComparison.OrdinalIgnoreCase))
                              .GroupBy(x => (x.constraint_name, x.to_table))
                              .Select(g => new ForeignKeyDto(
                                  ConstraintName: g.Key.constraint_name,
                                  FromTable: t.table_name,
                                  FromColumns: g.OrderBy(x => x.ordinal).Select(x => x.from_column).ToList(),
                                  ToTable: g.Key.to_table,
                                  ToColumns: g.OrderBy(x => x.ordinal).Select(x => x.to_column).ToList()
                              ))
                              .ToList();

            // Inbound FK’ler (başka tablolardan bu tabloya)
            var inbound = fks.Where(x => x.to_table.Equals(t.table_name, StringComparison.OrdinalIgnoreCase))
                             .GroupBy(x => (x.constraint_name, x.from_table))
                             .Select(g => new ReferencedByDto(
                                 ConstraintName: g.Key.constraint_name,
                                 FromTable: g.Key.from_table,
                                 FromColumns: g.OrderBy(x => x.ordinal).Select(x => x.from_column).ToList(),
                                 ToColumns: g.OrderBy(x => x.ordinal).Select(x => x.to_column).ToList()
                             ))
                             .ToList();

            tableDtos.Add(new TableDto(
                Name: t.table_name,
                Description: t.table_comment,
                PrimaryKey: pkCols,
                Columns: cols,
                ForeignKeys: outbound,
                ReferencedBy: inbound
            ));
        }

        return new SchemaDto(schema, tableDtos);
    }
}
