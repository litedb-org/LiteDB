using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Demo.Tools.VectorSearch.Embedding;
using LiteDB.Demo.Tools.VectorSearch.Models;
using LiteDB.Demo.Tools.VectorSearch.Services;
using LiteDB.Demo.Tools.VectorSearch.Utilities;
using Spectre.Console;
using Spectre.Console.Cli;
using ValidationResult = Spectre.Console.ValidationResult;

namespace LiteDB.Demo.Tools.VectorSearch.Commands
{
    internal sealed class SearchCommand : AsyncCommand<SearchCommandSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, SearchCommandSettings settings)
        {
            using var documentStore = new DocumentStore(settings.DatabasePath);

            var embeddingOptions = settings.CreateEmbeddingOptions();
            using var embeddingService = await GeminiEmbeddingService.CreateAsync(embeddingOptions, CancellationToken.None);

            var queryText = settings.Query;
            if (string.IsNullOrWhiteSpace(queryText))
            {
                queryText = AnsiConsole.Ask<string>("Enter a search prompt:");
            }

            if (string.IsNullOrWhiteSpace(queryText))
            {
                AnsiConsole.MarkupLine("[red]A non-empty query is required.[/]");
                return 1;
            }

            var normalized = TextUtilities.NormalizeForEmbedding(queryText, embeddingOptions.MaxInputLength);
            var queryEmbedding = await embeddingService.EmbedAsync(normalized, CancellationToken.None);

            var chunkResults = documentStore.TopNearestChunks(queryEmbedding, settings.Top)
                .Select(chunk => new SearchHit(chunk, VectorMath.CosineSimilarity(chunk.Embedding, queryEmbedding)))
                .ToList();

            if (settings.MaxDistance.HasValue)
            {
                chunkResults = chunkResults
                    .Where(hit => VectorMath.CosineDistance(hit.Chunk.Embedding, queryEmbedding) <= settings.MaxDistance.Value)
                    .ToList();
            }

            if (chunkResults.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No matching documents were found.[/]");
                return 0;
            }

            chunkResults.Sort((left, right) => right.Similarity.CompareTo(left.Similarity));

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("#");
            table.AddColumn("Score");
            table.AddColumn("Document");
            if (!settings.HidePath)
            {
                table.AddColumn("Path");
            }
            table.AddColumn("Snippet");

            var rank = 1;
            var documentCache = new Dictionary<string, IndexedDocument?>(StringComparer.OrdinalIgnoreCase);

            foreach (var hit in chunkResults)
            {
                var snippet = hit.Chunk.Snippet;
                if (snippet.Length > settings.PreviewLength)
                {
                    snippet = snippet[..settings.PreviewLength] + "\u2026";
                }

                if (!documentCache.TryGetValue(hit.Chunk.Path, out var parentDocument))
                {
                    parentDocument = documentStore.FindByPath(hit.Chunk.Path);
                    documentCache[hit.Chunk.Path] = parentDocument;
                }

                var chunkNumber = hit.Chunk.ChunkIndex + 1;
                var documentLabel = parentDocument != null
                    ? $"{parentDocument.Title} (Chunk {chunkNumber})"
                    : $"Chunk {chunkNumber}";

                var rowData = new List<string>
                {
                    Markup.Escape(rank.ToString()),
                    Markup.Escape(hit.Similarity.ToString("F3")),
                    Markup.Escape(documentLabel)
                };

                if (!settings.HidePath)
                {
                    var pathValue = parentDocument?.Path ?? hit.Chunk.Path;
                    rowData.Add(Markup.Escape(pathValue));
                }

                rowData.Add(Markup.Escape(snippet));

                table.AddRow(rowData.ToArray());

                rank++;
            }

            AnsiConsole.Write(table);
            return 0;
        }

        private sealed record SearchHit(IndexedDocumentChunk Chunk, double Similarity);
    }

    internal sealed class SearchCommandSettings : VectorSearchCommandSettings
    {
        [CommandOption("-q|--query <TEXT>")]
        public string? Query { get; set; }

        [CommandOption("--top <N>")]
        public int Top { get; set; } = 5;

        [CommandOption("--max-distance <VALUE>")]
        public double? MaxDistance { get; set; }

        [CommandOption("--preview-length <CHARS>")]
        public int PreviewLength { get; set; } = 160;

        [CommandOption("--hide-path")]
        public bool HidePath { get; set; }

        public override ValidationResult Validate()
        {
            var baseResult = base.Validate();
            if (!baseResult.Successful)
            {
                return baseResult;
            }

            if (Top <= 0)
            {
                return ValidationResult.Error("--top must be greater than zero.");
            }

            if (MaxDistance.HasValue && MaxDistance <= 0)
            {
                return ValidationResult.Error("--max-distance must be greater than zero when specified.");
            }

            if (PreviewLength <= 0)
            {
                return ValidationResult.Error("--preview-length must be greater than zero.");
            }

            return ValidationResult.Success();
        }
    }
}




