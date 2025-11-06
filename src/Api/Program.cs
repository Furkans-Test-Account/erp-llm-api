// File: src/Api/Program.cs
#nullable enable
using Serilog;
using Api.DTOs;
using Api.Services;
using Api.Services.Abstractions;
using Microsoft.OpenApi.Models;
using Microsoft.Extensions.Options;
using System;


var builder = WebApplication.CreateBuilder(args);


builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .WriteTo.Console()
       .Enrich.FromLogContext();
});


const string CorsPolicy = "DevCors";
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: CorsPolicy, policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:5000")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});


builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ERP-LLM API", Version = "v1" });
    c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
});


builder.Services.AddScoped<ISchemaService, SchemaServiceSqlServer>();
builder.Services.AddScoped<ISqlValidator, SqlValidator>();
builder.Services.AddScoped<ISqlExecutor, SqlExecutor>();
builder.Services.AddSingleton<IPromptBuilder, PromptBuilder>();
builder.Services.AddSingleton<IChatHistoryStore, ChatHistorySqlite>();


builder.Services.Configure<LlmAuditOptions>(builder.Configuration.GetSection("LlmAudit"));
builder.Services.AddSingleton<ILlmAudit, LlmAuditFile>();

// LLM service + runner
// IMPORTANT:
builder.Services.AddHttpClient<ILlmService, LlmService>(client =>
{
    var baseUrl = builder.Configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com/";
    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddScoped<SelfHealingSqlRunner>();

// Removed: department slicing DI and policies

// -----------------------------
// Build & Pipeline
// -----------------------------
var app = builder.Build();

if (app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "ERP-LLM API V1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseSerilogRequestLogging();
app.UseCors(CorsPolicy);

app.UseRouting();
app.UseAuthorization();

app.MapControllers();

app.Run();
