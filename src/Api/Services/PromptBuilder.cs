// File: src/Api/Services/PromptBuilder.cs
#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Linq;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    public class PromptBuilder : IPromptBuilder
    {
        private readonly IWebHostEnvironment _env;

        public PromptBuilder(IWebHostEnvironment env)
        {
            _env = env;
        }

        // TEMP: Hard-coded prompt template path (will be replaced by CategoryId mapping later)
        private string GetTemplatePath()
        {
            return Path.Combine(
                _env.ContentRootPath,
                "App_Data",
                "Prompts",
                "prompt_depo_stok.txt"
            );
        }

        private string LoadTemplateOrFallback()
        {
            try
            {
                var full = GetTemplatePath();
                if (File.Exists(full))
                    return File.ReadAllText(full, Encoding.UTF8);
            }
            catch { /* ignore and fallback */ }

            // Minimal fallback to keep runtime functional if file not found
            return "You are a SQL generator. Return a single SELECT.\n";
        }
        // Routing prompt removed

        // 2) PACK-SCOPED SQL PROMPT — department-only (no adjacent packs)
        public string BuildPromptForPack(
            string userQuestion,
            PackDto pack,
            SchemaDto fullSchema,
            IReadOnlyList<PackDto>? _ignoredAdjacentPacks = null, // kept for signature compat; ignored
            string sqlDialect = "SQL Server (T-SQL)",
            bool requireSingleSelect = true,
            bool forbidDml = true,
            bool preferAnsi = true)
        {
            // New behavior: ignore schema slicing/pack metadata and load a hard-coded prompt template
            // The template itself is expected to contain all instructions (and embedded schema if needed)
            var template = LoadTemplateOrFallback();

            var sb = new StringBuilder();
            sb.AppendLine(template);
            sb.AppendLine();
            sb.AppendLine($"UserQuestion: {userQuestion}");
            return sb.ToString();
        }

        // Helpers removed (no longer needed in template-based prompts)
    }
}
