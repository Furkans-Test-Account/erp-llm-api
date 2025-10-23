// IChatHistoryStore.cs
using Api.DTOs;

public interface IChatHistoryStore
{
    Task<string> EnsureThreadAsync(string? threadId, CancellationToken ct);
    Task AppendAsync(ChatMessageDto msg, CancellationToken ct);
    Task<IReadOnlyList<ChatMessageDto>> GetMessagesAsync(string threadId, CancellationToken ct);
    Task<IReadOnlyList<ChatThreadDto>> ListThreadsAsync(int take, CancellationToken ct);

    // NEW: set title once, from first user message
    Task SetTitleIfEmptyAsync(string threadId, string title, CancellationToken ct);
    Task SetWorkingSetAsync(string threadId, string entity, string idColumn, IReadOnlyList<string> ids, CancellationToken ct);
    Task<(string Entity, string IdColumn, IReadOnlyList<string> Ids)?> GetWorkingSetAsync(string threadId, CancellationToken ct);

}
