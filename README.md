# ERP-LLM API

A RESTful API that converts natural language questions into SQL queries using Large Language Models (LLM). The API is designed for ERP systems and uses template-based prompts to generate safe, validated SQL queries.

## Features

- **Natural Language to SQL**: Convert Turkish/English questions into SQL Server (T-SQL) queries
- **Template-Based Prompts**: Uses customizable prompt templates from `App_Data/Prompts/`
- **SQL Validation**: Automatically validates generated SQL to prevent DML/DDL operations
- **Self-Healing SQL**: Automatically retries and fixes SQL generation errors
- **Chat History**: Maintains conversation context using SQLite storage
- **Audit Logging**: Logs all LLM interactions for debugging and analysis
- **Schema Inspection**: Optional endpoints to inspect database schema

## Architecture

The API follows a clean architecture pattern with:

- **Controllers**: Handle HTTP requests and responses
- **Services**: Business logic and LLM integration
- **DTOs**: Data transfer objects for API contracts
- **Template System**: File-based prompt templates for different domains

### Key Components

- **PromptBuilder**: Loads and combines prompt templates with user questions
- **LlmService**: Interfaces with OpenAI API for SQL generation
- **SqlValidator**: Validates SQL to ensure only SELECT statements are allowed
- **SelfHealingSqlRunner**: Executes SQL with automatic retry and error correction
- **ChatHistoryStore**: Manages conversation history in SQLite

## API Endpoints

### Query Endpoints

#### `POST /api/query`
Converts a natural language question into SQL and executes it.

**Request:**
```json
{
  "question": "Her depo bazında toplam stok miktarını ve toplam tutarı getir",
  "conversationId": "optional-conversation-id"
}
```

**Response:**
```json
{
  "sql": "SELECT WarehouseCode, SUM(Qty) as TotalQty, SUM(Amount) as TotalAmount FROM ...",
  "result": { ... },
  "conversationId": "thread-id"
}
```

### Schema Endpoints

#### `GET /api/schema`
Retrieves the database schema information.

#### `POST /api/schema/save-json`
Saves the current database schema to `App_Data/Schema.json`.

### Chat Endpoints

#### `POST /api/chat/start`
Creates a new conversation thread.

**Response:**
```json
{
  "conversationId": "new-thread-id"
}
```

#### `GET /api/chat/{id}/messages`
Retrieves all messages for a conversation thread.

#### `GET /api/chat/list?take=20`
Lists recent conversation threads (default: 20).



