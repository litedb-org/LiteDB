using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Demo.Tools.VectorSearch.Configuration;
using LiteDB.Demo.Tools.VectorSearch.Embedding;
using LiteDB.Demo.Tools.VectorSearch.Models;
using LiteDB.Demo.Tools.VectorSearch.Services;
using LiteDB.Demo.Tools.VectorSearch.Utilities;
using Spectre.Console;
using Spectre.Console.Cli;
using ValidationResult = Spectre.Console.ValidationResult;

namespace LiteDB.Demo.Tools.VectorSearch.Commands
{
    internal sealed class IngestCommand : AsyncCommand<IngestCommandSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, IngestCommandSettings settings)
        {
            if (!Directory.Exists(settings.SourceDirectory))
            {
                throw new InvalidOperationException($"Source directory '{settings.SourceDirectory}' does not exist.");
            }

            var embeddingOptions = settings.CreateEmbeddingOptions();

            using var documentStore = new DocumentStore(settings.DatabasePath);
            using var embeddingService = await GeminiEmbeddingService.CreateAsync(embeddingOptions, CancellationToken.None);

            var searchOption = settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(settings.SourceDirectory, "*", searchOption)
                .Where(TextUtilities.IsSupportedDocument)
                .OrderBy(x => x)
                .Select(Path.GetFullPath)
                .ToArray();

            if (files.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No supported text documents were found. Nothing to ingest.[/]");
                return 0;
            }

            var skipUnchanged = !settings.Force;
            var processed = 0;
            var skipped = 0;
            var errors = new List<(string Path, string Error)>();

            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new ElapsedTimeColumn(),
                    new RemainingTimeColumn()
                })
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Embedding documents", maxValue: files.Length);

                    foreach (var path in files)
                    {
                        try
                        {
                            var info = new FileInfo(path);
                            var rawContent = TextUtilities.ReadDocument(path);
                            var contentHash = TextUtilities.ComputeContentHash(rawContent);

                            var existing = documentStore.FindByPath(path);
                            if (existing != null && skipUnchanged && string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal))
                            {
                                skipped++;
                                continue;
                            }

                            var chunkRecords = new List<IndexedDocumentChunk>();
                            var chunkIndex = 0;
                            var ensuredIndex = false;

                            foreach (var chunk in TextUtilities.SplitIntoChunks(rawContent, settings.ChunkLength, settings.ChunkOverlap))
                            {
                                var normalizedChunk = TextUtilities.NormalizeForEmbedding(chunk, embeddingOptions.MaxInputLength);
                                if (string.IsNullOrWhiteSpace(normalizedChunk))
                                {
                                    chunkIndex++;
                                    continue;
                                }

                                var embedding = await embeddingService.EmbedAsync(normalizedChunk, CancellationToken.None);

                                if (!ensuredIndex)
                                {
                                    documentStore.EnsureChunkVectorIndex(embedding.Length);
                                    ensuredIndex = true;
                                }

                                chunkRecords.Add(new IndexedDocumentChunk
                                {
                                    Path = path,
                                    ChunkIndex = chunkIndex,
                                    Snippet = chunk.Trim(),
                                    Embedding = embedding
                                });

                                chunkIndex++;
                            }

                            var record = existing ?? new IndexedDocument();
                            record.Path = path;
                            record.Title = Path.GetFileName(path);
                            record.Preview = TextUtilities.BuildPreview(rawContent, settings.PreviewLength);
                            record.Embedding = Array.Empty<float>();
                            record.LastModifiedUtc = info.LastWriteTimeUtc;
                            record.SizeBytes = info.Length;
                            record.ContentHash = contentHash;
                            record.IngestedUtc = DateTime.UtcNow;

                            if (chunkRecords.Count == 0)
                            {
                                documentStore.Upsert(record);
                                documentStore.ReplaceDocumentChunks(path, Array.Empty<IndexedDocumentChunk>());
                                skipped++;
                                continue;
                            }

                            documentStore.Upsert(record);
                            documentStore.ReplaceDocumentChunks(path, chunkRecords);
                            processed++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add((path, ex.Message));
                        }
                        finally
                        {
                            task.Increment(1);
                        }
                    }
                });

            if (settings.PruneMissing)
            {
                var indexedPaths = documentStore.GetTrackedPaths();
                var currentPaths = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                var missing = indexedPaths.Where(path => !currentPaths.Contains(path)).ToArray();

                if (missing.Length > 0)
                {
                    documentStore.RemoveMissingDocuments(missing);
                    AnsiConsole.MarkupLine($"[yellow]Removed {missing.Length} documents that no longer exist on disk.[/]");
                }
            }

            var summary = new Table().Border(TableBorder.Rounded);
            summary.AddColumn("Metric");
            summary.AddColumn("Value");
            summary.AddRow("Processed", processed.ToString());
            summary.AddRow("Skipped", skipped.ToString());
            summary.AddRow("Errors", errors.Count.ToString());

            AnsiConsole.Write(summary);

            if (errors.Count > 0)
            {
                var errorTable = new Table().Border(TableBorder.Rounded);
                errorTable.AddColumn("File");
                errorTable.AddColumn("Error");

                foreach (var (path, message) in errors)
                {
                    errorTable.AddRow(path, message);
                }

                AnsiConsole.Write(errorTable);
                return 1;
            }

            return 0;
        }
    }

    internal sealed class IngestCommandSettings : VectorSearchCommandSettings
    {
        [CommandOption("-s|--source <DIRECTORY>")]
        public string SourceDirectory { get; set; } = string.Empty;

        [CommandOption("--preview-length <CHARS>")]
        public int PreviewLength { get; set; } = 240;

        [CommandOption("--no-recursive")]
        public bool NoRecursive { get; set; }

        [CommandOption("--force")]
        public bool Force { get; set; }

        [CommandOption("--prune-missing")]
        public bool PruneMissing { get; set; }

        [CommandOption("--chunk-length <CHARS>")]
        public int ChunkLength { get; set; } = 600;

        [CommandOption("--chunk-overlap <CHARS>")]
        public int ChunkOverlap { get; set; } = 100;

        public bool Recursive => !NoRecursive;

        public override ValidationResult Validate()
        {
            var baseResult = base.Validate();
            if (!baseResult.Successful)
            {
                return baseResult;
            }

            if (string.IsNullOrWhiteSpace(SourceDirectory))
            {
                return ValidationResult.Error("A source directory is required.");
            }

            if (PreviewLength <= 0)
            {
                return ValidationResult.Error("--preview-length must be greater than zero.");
            }

            if (ChunkLength <= 0)
            {
                return ValidationResult.Error("--chunk-length must be greater than zero.");
            }

            if (ChunkOverlap < 0)
            {
                return ValidationResult.Error("--chunk-overlap must be zero or greater.");
            }

            if (ChunkOverlap >= ChunkLength)
            {
                return ValidationResult.Error("--chunk-overlap must be smaller than --chunk-length.");
            }

            return ValidationResult.Success();
        }
    }
}




