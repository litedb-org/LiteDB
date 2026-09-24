using System;

namespace LiteDB.Demo.Tools.VectorSearch.Configuration
{
    internal sealed class GeminiEmbeddingOptions
    {
        private const string ApiModelPrefix = "models/";

        private GeminiEmbeddingOptions(string? projectId, string? location, string model, int maxInputLength, string? apiKey)
        {
            Model = TrimModelPrefix(model);

            if (maxInputLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxInputLength));
            }

            ProjectId = projectId;
            Location = location;
            MaxInputLength = maxInputLength;
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        }

        public static GeminiEmbeddingOptions ForServiceAccount(string projectId, string location, string model, int maxInputLength)
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new ArgumentNullException(nameof(projectId));
            }

            if (string.IsNullOrWhiteSpace(location))
            {
                throw new ArgumentNullException(nameof(location));
            }

            return new GeminiEmbeddingOptions(projectId, location, model, maxInputLength, apiKey: null);
        }

        public static GeminiEmbeddingOptions ForApiKey(string apiKey, string model, int maxInputLength)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentNullException(nameof(apiKey));
            }

            return new GeminiEmbeddingOptions(projectId: null, location: null, model, maxInputLength, apiKey);
        }

        public string? ProjectId { get; }

        public string? Location { get; }

        public string Model { get; }

        public int MaxInputLength { get; }

        public string? ApiKey { get; }

        public bool UseApiKey => !string.IsNullOrWhiteSpace(ApiKey);

        public string GetVertexEndpoint()
        {
            if (string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(Location))
            {
                throw new InvalidOperationException("Vertex endpoint requires both project id and location.");
            }

            return $"https://{Location}-aiplatform.googleapis.com/v1/projects/{ProjectId}/locations/{Location}/publishers/google/models/{Model}:predict";
        }

        public string GetApiEndpoint()
        {
            return $"https://generativelanguage.googleapis.com/v1beta/{GetApiModelIdentifier()}:embedContent"; //models/{GetApiModelIdentifier()}:embedContent";
        }

        public string GetApiModelIdentifier()
        {
            return Model.StartsWith(ApiModelPrefix, StringComparison.Ordinal)
                ? Model
                : $"{ApiModelPrefix}{Model}";
        }

        private static string TrimModelPrefix(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentNullException(nameof(model));
            }

            return model.StartsWith(ApiModelPrefix, StringComparison.OrdinalIgnoreCase)
                ? model.Substring(ApiModelPrefix.Length)
                : model;
        }
    }
}
