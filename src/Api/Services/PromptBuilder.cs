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
            catch { }


            return "You are a SQL generator. Return a single SELECT.\n";
        }

        public string BuildPromptForPack(
            string userQuestion,
            PackDto pack,
            SchemaDto fullSchema,
            IReadOnlyList<PackDto>? _ignoredAdjacentPacks = null,
            string sqlDialect = "SQL Server (T-SQL)",
            bool requireSingleSelect = true,
            bool forbidDml = true,
            bool preferAnsi = true)
        {

            var template = LoadTemplateOrFallback();

            var sb = new StringBuilder();
            sb.AppendLine(template);
            sb.AppendLine();
            sb.AppendLine($"UserQuestion: {userQuestion}");
            return sb.ToString();
        }


    }
}
