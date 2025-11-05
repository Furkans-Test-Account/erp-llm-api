using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Api.Services.Abstractions;

namespace Api.Services
{
    public sealed class LlmAuditOptions
    {
        public bool Enabled { get; set; } = true;
        public string Folder { get; set; } = "App_Data/LlmDebug";
    }

    public sealed class LlmAuditFile : ILlmAudit
    {
        private readonly IHostEnvironment _env;
        private readonly LlmAuditOptions _opt;

        public LlmAuditFile(IHostEnvironment env, IOptions<LlmAuditOptions> opt)
        {
            _env = env;
            _opt = opt.Value ?? new LlmAuditOptions();
        }

        public bool Enabled => _opt.Enabled;
        public string RootFolder => Path.Combine(_env.ContentRootPath, _opt.Folder);

        public async Task SaveAsync(string purpose, string promptText, string requestJson, CancellationToken ct = default)
        {
            if (!Enabled) return;

            Directory.CreateDirectory(RootFolder);

            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var baseName = $"{purpose}_{ts}";

            var promptPath = Path.Combine(RootFolder, $"{baseName}.txt");
            var bodyPath = Path.Combine(RootFolder, $"{baseName}.json");

            // prompt (metin)
            await File.WriteAllTextAsync(promptPath, promptText ?? string.Empty, new UTF8Encoding(false), ct);

            // request body (json pretty)
            using var jdoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(requestJson) ? "{}" : requestJson);
            var pretty = JsonSerializer.Serialize(jdoc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(bodyPath, pretty, new UTF8Encoding(false), ct);
        }
    }
}
