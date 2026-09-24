#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using static LiteDB.Client.Coordinated.CoordinatorProtocol;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// The coordinator: this process owns the only writable engine and serves other
    /// processes over a named pipe. Each pipe connection is a session executed on its
    /// own thread, so the engine's thread-bound transactions map to sessions and a
    /// closed session rolls its transaction back.
    /// </summary>
    internal sealed partial class CoordinatorHost : IDisposable
    {
        private static readonly TimeSpan GrantQuiescence = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan GrantOpenTimeout = TimeSpan.FromSeconds(10);

        private readonly LiteEngine _engine;
        private readonly CoordinatorStatusPage _page;
        private readonly CoordinatorSignals _signals;
        private readonly CoordinatorGate _gate = new CoordinatorGate();
        private readonly CoordinatorMutex _mutex;
        private readonly string _pipeName;
        private readonly string _filename;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly List<(Thread Thread, Stream Stream)> _sessions = new List<(Thread, Stream)>();
        private readonly Thread _accept;
        private int _disposed;
        private volatile Exception _acceptFailure;

        /// <summary>Why the accept loop stopped, when it could not create its pipe.</summary>
        internal Exception AcceptFailure => _acceptFailure;

#if DEBUG || TESTING
        /// <summary>Test hook: runs before the accept loop creates a pipe instance (database filename, stop token).</summary>
        internal static Action<string, CancellationToken> BeforeCreatePipe;
#endif

        internal CoordinatorHost(EngineSettings settings, CoordinatorMutex mutex)
        {
            _mutex = mutex;
            _pipeName = PipeName(settings.Filename);
            _filename = settings.Filename;
            var registry = new SharedReaderRegistry(settings.Filename, settings.SharedReaderFiles);
            var engineSettings = settings.Clone();
            // Client snapshots are leased by OS-held files, so they survive this
            // process: a successor coordinator still sees every live snapshot.
            engineSettings.SharedReaderVersions = registry.LiveVersions;
            // Without a status page clients fall back to snapshot grants over IPC.
            try { _page = CoordinatorStatusPage.Create(settings.Filename); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // Publish "starting" before the engine opens: opening can change the files.
            _signals = _page == null ? null : new CoordinatorSignals(_page);
            engineSettings.CoordinationSignals = _signals;
            try { _engine = new LiteEngine(engineSettings); }
            catch
            {
                _signals?.Dispose();
                _page?.Dispose();
                throw;
            }
            _signals?.Started(_engine.ReadVersion);
            RemoveStaleUnixEndpoint(_pipeName);
            _accept = new Thread(this.AcceptLoop) { IsBackground = true, Name = "LiteDB coordinator accept" };
            _accept.Start();
        }

        internal LiteEngine Engine => _engine;

        internal T Run<T>(Func<LiteEngine, T> action) => _gate.Run(() => action(_engine));

        private void AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
#if DEBUG || TESTING
                    BeforeCreatePipe?.Invoke(_filename, _stop.Token);
#endif
                    server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (IOException)
                {
                    // Busy pipe instances (Windows) or a failed bind (Unix) are transient, but
                    // during Stop they must end the loop: nothing may escape this thread.
                    if (_stop.IsCancellationRequested) return;
                    Thread.Sleep(20);
                    continue;
                }
                catch (Exception ex)
                {
                    // An exception on this background thread would terminate the process.
                    // Clients cannot connect; they time out and fail with a coordinator error.
                    _acceptFailure = ex;
                    return;
                }
                try { server.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult(); }
                catch (Exception)
                {
                    server.Dispose();
                    if (_stop.IsCancellationRequested) return;
                    continue;
                }
                var thread = new Thread(() => this.Serve(server)) { IsBackground = true, Name = "LiteDB coordinator session" };
                lock (_sessions)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        server.Dispose();
                        return;
                    }
                    _sessions.Add((thread, server));
                }
                thread.Start();
            }
        }

        private void Serve(Stream stream)
        {
            try
            {
                while (true)
                {
                    var request = Read(stream);
                    var reply = this.Dispatch(stream, request);
                    if (reply != null) Write(stream, reply);
                }
            }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (TimeoutException) { }
            finally
            {
                // A disconnected session aborts its explicit transaction.
                try { _gate.Run(() => _engine.Rollback()); }
                catch (Exception) { }
                stream.Dispose();
            }
        }

        private BsonDocument Dispatch(Stream stream, BsonDocument request)
        {
            var op = request["op"].AsString;
            if (op == "snapshot") return this.GrantSnapshot(stream, request);
            try { return Ok(this.Execute(stream, op, request)); }
            catch (CoordinatorLostException) { throw; }
            // Engine failures, including its I/O errors, are the operation's result.
            catch (Exception ex) { return Error(ex); }
        }

        /// <summary>
        /// Close the gate so no engine call runs, give the client the committed read
        /// version, and keep the gate closed until it has opened its snapshot engine.
        /// The client leases the snapshot with an OS-held file before opening it.
        /// </summary>
        private BsonDocument GrantSnapshot(Stream stream, BsonDocument request)
        {
            // Nothing committed since the client's cached snapshot: it is still current.
            // Its lease prevents WAL truncation, so the version cannot be reused meanwhile.
            var have = request["have"];
            if (have.IsInt32 && have.AsInt32 >= 0 && have.AsInt32 == _engine.ReadVersion)
                return Ok(new BsonDocument { ["same"] = true });
            if (!_gate.TryClose(GrantQuiescence)) return Ok(new BsonDocument { ["busy"] = true });
            try
            {
                Write(stream, Ok(new BsonDocument { ["v"] = _engine.ReadVersion }));
                Read(stream, GrantOpenTimeout);
                return null;
            }
            finally
            {
                _gate.Open();
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void RemoveStaleUnixEndpoint(string pipeName)
        {
            // A killed coordinator leaves its Unix domain socket file behind. This
            // process owns the coordinator mutex, so no live server can own it.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            try { File.Delete(UnixEndpoint(pipeName)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose() => this.Stop(crash: false);

        /// <summary>Test hook: stop as a killed process would, without checkpoint or cleanup.</summary>
        internal void Crash() => this.Stop(crash: true);

        private void Stop(bool crash)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _accept.Join();
            List<(Thread Thread, Stream Stream)> sessions;
            lock (_sessions) sessions = new List<(Thread, Stream)>(_sessions);
            foreach (var session in sessions) session.Stream.Dispose();
            foreach (var session in sessions) session.Thread.Join(TimeSpan.FromSeconds(10));
            try
            {
                if (crash) _engine.Close(checkpoint: false);
                else _engine.Dispose();
            }
            finally
            {
                // A crash leaves the page as a killed process would. A graceful stop
                // publishes "no coordinator" first, so clients still mapping the file
                // (Windows keeps it until they unmap) never trust it again.
                if (!crash) _signals?.Dispose();
                _page?.Dispose();
                if (!crash && _page != null) TryDelete(CoordinatorStatusPage.PathFor(_filename));
                _mutex.Dispose();
                _stop.Dispose();
            }
        }
    }
}
#endif
