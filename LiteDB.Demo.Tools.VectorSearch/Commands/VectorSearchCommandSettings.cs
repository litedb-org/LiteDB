using System;
using Spectre.Console.Cli;
using LiteDB.Demo.Tools.VectorSearch.Configuration;
using ValidationResult = Spectre.Console.ValidationResult;

namespace LiteDB.Demo.Tools.VectorSearch.Commands
{
    internal abstract class VectorSearchCommandSettings : CommandSettings
    {
        private const string DefaultModel = "gemini-embedding-001";
        private const string DefaultLocation = "us-central1";
        private const string ApiKeyEnvironmentVariable = "GOOGLE_VERTEX_API_KEY";
        private const string ApiKeyFallbackEnvironmentVariable = "GOOGLE_API_KEY";

        [CommandOption("-d|--database <PATH>")]
        public string DatabasePath { get; set; } = "vector-search.db";

        [CommandOption("--project-id <PROJECT>")]
        public string? ProjectId { get; set; }

        [CommandOption("--location <LOCATION>")]
        public string? Location { get; set; }

        [CommandOption("--model <MODEL>")]
        public string? Model { get; set; }

        [CommandOption("--api-key <KEY>")]
        public string? ApiKey { get; set; }

        [CommandOption("--max-input-length <CHARS>")]
        public int MaxInputLength { get; set; } = 7000;

        public GeminiEmbeddingOptions CreateEmbeddingOptions()
        {
            var model = ResolveModel();
            var apiKey = ResolveApiKey();

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return GeminiEmbeddingOptions.ForApiKey(apiKey!, model, MaxInputLength);
            }

            var projectId = ResolveProjectIdOrNull();
            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new InvalidOperationException("Provide --api-key/GOOGLE_VERTEX_API_KEY or --project-id/GOOGLE_PROJECT_ID to configure Gemini embeddings.");
            }

            var location = ResolveLocation();
            return GeminiEmbeddingOptions.ForServiceAccount(projectId!, location, model, MaxInputLength);
        }

        public override ValidationResult Validate()
        {
            if (MaxInputLength <= 0)
            {
                return ValidationResult.Error("--max-input-length must be greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(DatabasePath))
            {
                return ValidationResult.Error("A database path must be provided.");
            }

            var hasApiKey = !string.IsNullOrWhiteSpace(ResolveApiKey());
            var hasProject = !string.IsNullOrWhiteSpace(ResolveProjectIdOrNull());

            if (!hasApiKey && !hasProject)
            {
                return ValidationResult.Error("Authentication required. Supply --api-key (or GOOGLE_VERTEX_API_KEY/GOOGLE_API_KEY) or --project-id (or GOOGLE_PROJECT_ID).");
            }

            return ValidationResult.Success();
        }

        private string? ResolveProjectIdOrNull()
        {
            if (!string.IsNullOrWhiteSpace(ProjectId))
            {
                return ProjectId;
            }

            var fromEnv = Environment.GetEnvironmentVariable("GOOGLE_PROJECT_ID");
            return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
        }

        private string ResolveLocation()
        {
            if (!string.IsNullOrWhiteSpace(Location))
            {
                return Location;
            }

            var fromEnv = Environment.GetEnvironmentVariable("GOOGLE_VERTEX_LOCATION");
            return string.IsNullOrWhiteSpace(fromEnv) ? DefaultLocation : fromEnv;
        }

        private string ResolveModel()
        {
            if (!string.IsNullOrWhiteSpace(Model))
            {
                return Model;
            }

            var fromEnv = Environment.GetEnvironmentVariable("GOOGLE_VERTEX_EMBEDDING_MODEL");
            return string.IsNullOrWhiteSpace(fromEnv) ? DefaultModel : fromEnv;
        }

        private string? ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                return ApiKey;
            }

            var fromEnv = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(fromEnv))
            {
                fromEnv = Environment.GetEnvironmentVariable(ApiKeyFallbackEnvironmentVariable);
            }

            return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
        }
    }
}
