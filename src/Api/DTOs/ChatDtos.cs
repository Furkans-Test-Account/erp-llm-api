namespace Api.DTOs;

public record ChatThreadDto(
    string Id,
    DateTime CreatedAtUtc,
    string? Title
);

public record ChatMessageDto(
    string Id,
    string ThreadId,
    string Role,                
    string? Content,
    string? Sql,
    string? Error,
    DateTime CreatedAtUtc
);
