#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using static LiteDB.Client.Coordinated.CoordinatorProtocol;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// A process that is not the coordinator. Each thread uses its own pipe session,
    /// matching the engine's thread-bound transactions. Reads outside a transaction
    /// open a read-only snapshot engine on the files directly, after registering the
    /// snapshot with the coordinator over a separate lease connection.
    /// </summary>
    internal sealed partial class CoordinatorClient : IDisposable
    {
        private readonly string _pipeName;
        private readonly EngineSettings _settings;
        private readonly SharedReaderRegistry _registry;
        // Null when the coordinator publishes no status page: snapshots are granted over IPC.
        private readonly CoordinatorStatusPage _page;
        private readonly ThreadLocal<Session> _sessions = new ThreadLocal<Session>(trackAllValues: true);
        private readonly object _leaseLock = new object();
        private Stream _lease;
        private int _disposed;

        internal long DirectReads;
        internal long IpcReads;
        internal long SnapshotOpens;

        private CoordinatorClient(string pipeName, EngineSettings settings)
        {
            _pipeName = pipeName;
            _settings = settings;
            _registry = new SharedReaderRegistry(settings.Filename, settings.SharedReaderFiles);
            _page = CoordinatorStatusPage.TryOpen(settings.Filename);
        }

        /// <summary>Connect and handshake; throws <see cref="TimeoutException"/> or <see cref="IOException"/> when no coordinator answers.</summary>
        internal static CoordinatorClient Connect(EngineSettings settings, TimeSpan timeout)
        {
            var client = new CoordinatorClient(PipeName(settings.Filename), settings);
            try
            {
                client.Call(new BsonDocument { ["op"] = "hello" }, timeout);
                return client;
            }
            catch (CoordinatorLostException ex)
            {
                client.Dispose();
                throw new IOException("No coordinator answered.", ex);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        internal BsonValue Call(BsonDocument request, TimeSpan? connectTimeout = null)
        {
            var session = this.GetSession(connectTimeout);
            try
            {
                Write(session.Stream, request);
                return Result(Read(session.Stream));
            }
            catch (Exception ex) when (IsPipeFailure(ex))
            {
                session.Dispose();
                throw new CoordinatorLostException(ex);
            }
        }

        /// <summary>Send documents one at a time; see <c>CoordinatorHost.Pull</c>.</summary>
        internal int Stream(BsonDocument header, IEnumerable<BsonDocument> documents)
        {
            var session = this.GetSession(null);
            var stream = session.Stream;
            try
            {
                Write(stream, header);
                using var e = documents.GetEnumerator();
                BsonDocument previous = null;
                while (true)
                {
                    var message = Read(stream);
                    if (!message.ContainsKey("next")) return Result(message).AsInt32;
                    if (previous != null && !message["id"].IsNull) previous["_id"] = message["id"];
                    bool more;
                    try { more = e.MoveNext(); }
                    catch (Exception ex)
                    {
                        // Let the engine roll the operation back, then report the caller's error.
                        Write(stream, new BsonDocument { ["abort"] = ex.Message });
                        Read(stream);
                        throw;
                    }
                    if (!more)
                    {
                        Write(stream, new BsonDocument { ["end"] = true });
                        return Result(Read(stream)).AsInt32;
                    }
                    previous = e.Current;
                    Write(stream, new BsonDocument { ["doc"] = previous });
                }
            }
            catch (Exception ex) when (IsPipeFailure(ex))
            {
                session.Dispose();
                throw new CoordinatorLostException(ex);
            }
        }

        internal IBsonDataReader QueryOverIpc(string collection, Query query)
        {
            Interlocked.Increment(ref IpcReads);
            var session = this.GetSession(null);
            var rows = new List<BsonValue>();
            BsonDocument result;
            try
            {
                Write(session.Stream, new BsonDocument { ["op"] = "query", ["c"] = collection, ["q"] = QueryToBson(query) });
                // Large results arrive as several frames; see CoordinatorProtocol.Chunk.
                while (true)
                {
                    result = Result(Read(session.Stream)).AsDocument;
                    rows.AddRange(result["rows"].AsArray);
                    if (!result["more"].AsBoolean) break;
                }
            }
            catch (Exception ex) when (IsPipeFailure(ex))
            {
                session.Dispose();
                throw new CoordinatorLostException(ex);
            }
            return new BufferedDataReader(rows, result["c"].IsNull ? collection : result["c"].AsString);
        }

        private Session GetSession(TimeSpan? connectTimeout)
        {
            var session = _sessions.Value;
            if (session != null && !session.Broken) return session;
            session?.Dispose();
            session = new Session(this.OpenPipe(connectTimeout ?? TimeSpan.FromSeconds(5)));
            _sessions.Value = session;
            return session;
        }

        private Stream GetLeaseStream() => _lease ??= this.OpenPipe(TimeSpan.FromSeconds(5));

        private void DropLeaseStream()
        {
            _lease?.Dispose();
            _lease = null;
        }

        private Stream OpenPipe(TimeSpan timeout)
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                pipe.Connect((int)timeout.TotalMilliseconds);
                return pipe;
            }
            catch (Exception ex) when (IsPipeFailure(ex))
            {
                pipe.Dispose();
                throw new CoordinatorLostException(ex);
            }
        }

        private static bool IsPipeFailure(Exception ex) =>
            ex is IOException && !(ex is CoordinatorLostException) || ex is ObjectDisposedException ||
            ex is TimeoutException || ex is InvalidOperationException && ex.Message.Contains("pipe", StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_leaseLock)
            {
                this.CloseSnapshots();
                this.DropLeaseStream();
                _registry.Dispose();
                // Under the lease lock: no snapshot acquisition can read it afterwards.
                _page?.Dispose();
            }
            foreach (var session in _sessions.Values) session?.Dispose();
            _sessions.Dispose();
        }

        private sealed class Session : IDisposable
        {
            internal Session(Stream stream) => this.Stream = stream;
            internal Stream Stream { get; }
            internal bool Broken { get; private set; }

            public void Dispose()
            {
                this.Broken = true;
                this.Stream.Dispose();
            }
        }
    }
}
#endif
