## Vector Search Demo CLI

`LiteDB.Demo.Tools.VectorSearch` showcases the new vector index APIs with an end-to-end ingestion and query experience. It embeds text documents using Google Gemini embeddings and persists metadata plus the resulting vectors in LiteDB.

### Requirements

- Supply Gemini credentials using **one** of the following approaches:
  - API key with `--api-key`, `GOOGLE_VERTEX_API_KEY`, or `GOOGLE_API_KEY` (calls the public Generative Language endpoint).
  - Service account credentials via `GOOGLE_APPLICATION_CREDENTIALS` (or other default `GoogleCredential` providers) together with project metadata.
- When targeting Vertex AI with a service account, the following settings apply (optionally via command options):
  - `GOOGLE_PROJECT_ID`
  - `GOOGLE_VERTEX_LOCATION` (defaults to `us-central1`)
- Model selection is controlled with `--model` or `GOOGLE_VERTEX_EMBEDDING_MODEL` and defaults to `gemini-embedding-001`.

### Usage

Restore and build the demo project:

```bash
dotnet build LiteDB.Demo.Tools.VectorSearch.csproj -c Release
```

Index a folder of `.txt`/`.md` files (API key example):

```bash
dotnet run --project LiteDB.Demo.Tools.VectorSearch.csproj -- ingest --source ./docs --database vector.db --api-key "$env:GOOGLE_VERTEX_API_KEY"
```

Run a semantic search over the ingested content (Vertex AI example):

```bash
dotnet run --project LiteDB.Demo.Tools.VectorSearch.csproj -- search --database vector.db --query "Explain document storage guarantees"
```

Use `--help` on either command to list all supported options (preview length, pruning behaviour, auth mode, custom model identifiers, etc.).

## License

[MIT](http://opensource.org/licenses/MIT)