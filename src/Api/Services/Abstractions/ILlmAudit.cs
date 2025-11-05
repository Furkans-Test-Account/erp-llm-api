namespace Api.Services.Abstractions
{
    public interface ILlmAudit
    {
        bool Enabled { get; }
        string RootFolder { get; }

        Task SaveAsync(
            string purpose,        // "route" | "pack-sql" | "refine" v.b.
            string promptText,     // LLM'e gönderilen prompt (metin)
            string requestJson,    // OpenAI'ye gönderilen JSON body
            CancellationToken ct = default);
    }
}
