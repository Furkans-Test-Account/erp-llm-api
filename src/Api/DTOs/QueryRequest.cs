// src/Api/DTOs/QueryRequest.cs
namespace Api.DTOs;

public record QueryRequest(
    string Question,
    string? ConversationId = null
);
