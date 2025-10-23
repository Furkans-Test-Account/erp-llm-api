
# erp-llm-api (Minimal API, PostgreSQL schema-driven)

This is a minimal .NET 8 Web API that:
- Reads **database schema (with comments)** from PostgreSQL at runtime
- Builds an LLM prompt from that schema
- (Stub) Generates a sample SQL
- Validates and executes the SQL
- Returns both the SQL and the result

## Quick start
1) Update connection string in `src/Api/appsettings.json`.
2) Run:
```bash
dotnet restore src/Api
dotnet run --project src/Api
```
3) Test endpoints:
- `GET /api/health`
- `GET /api/schema`
- `POST /api/query` with body `{ "question": "Top products?" }`

> Ensure your DB has comments (`COMMENT ON TABLE/COLUMN`) for richer prompts.
