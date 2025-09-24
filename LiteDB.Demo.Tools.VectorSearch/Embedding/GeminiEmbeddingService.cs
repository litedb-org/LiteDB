using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using LiteDB.Demo.Tools.VectorSearch.Configuration;

namespace LiteDB.Demo.Tools.VectorSearch.Embedding
{
    internal sealed class GeminiEmbeddingService : IEmbeddingService, IDisposable
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HttpClient _httpClient;
        private readonly GeminiEmbeddingOptions _options;
        private readonly ITokenAccess? _tokenAccessor;
        private bool _disposed;

        private GeminiEmbeddingService(HttpClient httpClient, GeminiEmbeddingOptions options, ITokenAccess? tokenAccessor)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _tokenAccessor = tokenAccessor;
        }

        public static async Task<GeminiEmbeddingService> CreateAsync(GeminiEmbeddingOptions options, CancellationToken cancellationToken)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ITokenAccess? tokenAccessor = null;

            if (!options.UseApiKey)
            {
                var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
                credential = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
                tokenAccessor = credential;
            }

            var httpClient = new HttpClient();
            return new GeminiEmbeddingService(httpClient, options, tokenAccessor);
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Text must be provided for embedding.", nameof(text));
            }

            EnsureNotDisposed();

            var normalized = text.Length <= _options.MaxInputLength
                ? text
                : text[.._options.MaxInputLength];

            var endpoint = _options.UseApiKey ? _options.GetApiEndpoint() : _options.GetVertexEndpoint();
            object payload = _options.UseApiKey
                ? new
                {
                    model = _options.GetApiModelIdentifier(),
                    content = new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = normalized
                            }
                        }
                    }
                }
                : new
                {
                    instances = new[]
                    {
                        new
                        {
                            content = new
                            {
                                parts = new[]
                                {
                                    new
                                    {
                                        text = normalized
                                    }
                                }
                            }
                        }
                    }
                };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var json = System.Text.Json.JsonSerializer.Serialize(payload, SerializerOptions);
            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;

            if (_options.UseApiKey)
            {
                request.Headers.TryAddWithoutValidation("x-goog-api-key", _options.ApiKey);
            }
            else
            {
                if (_tokenAccessor == null)
                {
                    throw new InvalidOperationException("Google credentials are required when no API key is provided.");
                }

                var token = await _tokenAccessor.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var details = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Embedding request failed ({response.StatusCode}). {details}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (TryReadValues(document.RootElement, out var values))
            {
                return values;
            }

            throw new InvalidOperationException("Embedding response did not contain any vector values.");
        }

        private static bool TryReadValues(JsonElement root, out float[] values)
        {
            if (root.TryGetProperty("predictions", out var predictions) && predictions.GetArrayLength() > 0)
            {
                var embeddings = predictions[0].GetProperty("embeddings").GetProperty("values");
                values = ReadFloatArray(embeddings);
                return true;
            }

            if (root.TryGetProperty("embedding", out var embedding) && embedding.TryGetProperty("values", out var apiValues))
            {
                values = ReadFloatArray(apiValues);
                return true;
            }

            values = Array.Empty<float>();
            return false;
        }

        private static float[] ReadFloatArray(JsonElement element)
        {
            var array = new float[element.GetArrayLength()];
            for (var i = 0; i < array.Length; i++)
            {
                array[i] = (float)element[i].GetDouble();
            }

            return array;
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GeminiEmbeddingService));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
