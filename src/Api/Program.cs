using Serilog;
using Api.DTOs;
using Api.Services;
using Api.Services.Abstractions;
using System.Text.Json;
using System.Collections;

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

// cors 
const string CorsPolicy = "DevCors";
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: CorsPolicy, policy =>
    {
        policy.WithOrigins(
            "http://localhost:5173", // your frontend
            "http://localhost:5000"  // for Swagger UI or direct calls from API docs
        )
        .AllowAnyHeader()
        .AllowAnyMethod();
    });
});

// -----------------------------
// DI
// -----------------------------
builder.Services.AddScoped<ISchemaService, SchemaServiceSqlServer>();
builder.Services.AddScoped<ISqlValidator, SqlValidator>();
builder.Services.AddScoped<ISqlExecutor, SqlExecutor>();
builder.Services.AddSingleton<IPromptBuilder, PromptBuilder>();

// Chat history store (SQLite)
builder.Services.AddSingleton<IChatHistoryStore, ChatHistorySqlite>();

// Sliced schema cache
builder.Services.AddSingleton<ISlicedSchemaCache, SlicedSchemaCache>();

// self-heal runner
builder.Services.AddScoped<SelfHealingSqlRunner>();

// -----------------------------
// OpenAI LLM (HttpClient)
// -----------------------------
builder.Services.AddHttpClient<ILlmService, LlmService>(client =>
{
    var baseUrl = builder.Configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Add controllers for new layered API
builder.Services.AddControllers();

// -----------------------------
// Swagger
// -----------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// -----------------------------
// Middleware Pipeline
// -----------------------------
app.UseSerilogRequestLogging();
app.UseCors(CorsPolicy);
app.UseSwagger();
app.UseSwaggerUI();

// Map controller routes
app.MapControllers();

// ==========================================================
// 2) LLM ile route (hangi pack?)
// ==========================================================
// Route endpoint migrated to RouteController
// (Removed)
// app.MapPost("/api/route/llm", ...)

// ==========================================================
// CHAT HISTORY ENDPOINTLERI
// ==========================================================

// Chat endpoints migrated to ChatController
// (Removed)
// app.MapPost("/api/chat/start", ...)
// app.MapGet("/api/chat/{id}/messages", ...)
// app.MapGet("/api/chat/list", ...)

// ==========================================================
// 3) /api/query  (SQL üret + self-heal + execute)
//    + history logging
//    + title = first user message
//    + LLM prompt’a geçmiş bağlamı ekleme
//    + Assistant content = sonuçtan özet (artık "Done." değil)
// ==========================================================
// Query endpoint migrated to QueryController
// (Removed)

app.Run();
