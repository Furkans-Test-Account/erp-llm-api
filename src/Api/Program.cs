// File: src/Api/Program.cs
#nullable enable
using Serilog;
using Api.DTOs;
using Api.Services;
using Api.Services.Abstractions;
using Microsoft.OpenApi.Models;
using Microsoft.Extensions.Options;
using System;

// ---------------------------------
// WebApplication Bootstrap
// ---------------------------------
var builder = WebApplication.CreateBuilder(args);

// -----------------------------
// Serilog
// -----------------------------
builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .WriteTo.Console()
       .Enrich.FromLogContext();
});

// -----------------------------
// CORS (Vite dev server için 5173)
// -----------------------------
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

// -----------------------------
// Controllers + Swagger
// -----------------------------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ERP-LLM API", Version = "v1" });
    c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
});

// -----------------------------
// DI – EXISTING SERVICES
// -----------------------------
builder.Services.AddScoped<ISchemaService, SchemaServiceSqlServer>();
builder.Services.AddScoped<ISqlValidator, SqlValidator>();
builder.Services.AddScoped<ISqlExecutor, SqlExecutor>();
builder.Services.AddSingleton<IPromptBuilder, PromptBuilder>();
builder.Services.AddSingleton<IChatHistoryStore, ChatHistorySqlite>();

// Department-aware + legacy slice cache tek yerde
builder.Services.AddSingleton<ISlicedSchemaCache, SlicedSchemaCache>();

// LLM denetim (audit) ve opsiyonları
builder.Services.Configure<LlmAuditOptions>(builder.Configuration.GetSection("LlmAudit"));
builder.Services.AddSingleton<ILlmAudit, LlmAuditFile>();

// LLM service + runner
// IMPORTANT: BaseAddress ayarı yapıldı => “invalid request URI” hatası çözülür
builder.Services.AddHttpClient<ILlmService, LlmService>(client =>
{
    var baseUrl = builder.Configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com/";
    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddScoped<SelfHealingSqlRunner>();

// -----------------------------
// DI – NEW: Strict department slicer
// -----------------------------
builder.Services.AddSingleton<IStrictDepartmentSlicer, StrictSchemaSlicer>();

// DepartmentSliceOptions (policy set)
// İhtiyaca göre prefix'leri/politikaları burada güncelleyebilirsin.
builder.Services.AddSingleton<IOptions<DepartmentSliceOptions>>(sp =>
    Options.Create(new DepartmentSliceOptions(
        Policies: new[]
        {
            new DepartmentPolicy("insan_kaynaklari","İK", new[]{ "hr" }),
            new DepartmentPolicy("depo_stok","Depo", new[]{ "st" }),
            new DepartmentPolicy("lojistik","Lojistik", new[]{ "e_" }),
            new DepartmentPolicy("satis","Satış", new[]{ "tr" }, new[]{ "trAdjustCost" }),
            new DepartmentPolicy("finans","Finans", new[]{ "bs","gl","trAdjustCost" }),
            new DepartmentPolicy("sozluk_kod","Sözlük/Kod", new[]{ "cd" }),
            new DepartmentPolicy("sistem_parametre","Sistem/Parametre", new[]{ "df","sr","tp","au","rp" })
        },
        MaxCandidateRefsPerPack: 20,
        RefTargetsPrefixes: new[] { "cd", "df", "bs" }
    ))
);

// -----------------------------
// Build
// -----------------------------
var app = builder.Build();

// -----------------------------
// Middleware
// -----------------------------
// Not: Geliştirmede de Swagger açmak istersen koşulu kaldır:
// if (app.Environment.IsDevelopment()) { ... }
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "ERP-LLM API V1");
    c.RoutePrefix = string.Empty;
});

app.UseSerilogRequestLogging();
app.UseCors(CorsPolicy);
app.UseRouting();

app.UseAuthorization();

app.MapControllers();

app.Run();
