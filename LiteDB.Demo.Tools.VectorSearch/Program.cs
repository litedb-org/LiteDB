using System.Diagnostics;
using System.Threading.Tasks;
using LiteDB.Demo.Tools.VectorSearch.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.Demo.Tools.VectorSearch
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var app = new CommandApp();

            app.Configure(config =>
            {
                config.SetApplicationName("litedb-vector-search");
                config.SetExceptionHandler(ex =>
                {
                    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                    return -1;
                });

                config.AddCommand<IngestCommand>("ingest")
                    .WithDescription("Embed text documents from a folder into LiteDB for vector search.");

                config.AddCommand<SearchCommand>("search")
                    .WithDescription("Search previously embedded documents using vector similarity.");

                if (Debugger.IsAttached)
                {
                    config.PropagateExceptions();
                }
            });

            return await app.RunAsync(args);
        }
    }
}

