using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;

namespace LiteDB.Internals
{
    internal sealed class BackgroundCheckpointPowerScenario : IDisposable
    {
        internal readonly TempFile File = new TempFile();
        internal readonly LiteEngine Engine;
        internal readonly LiteDatabase Database;
        internal readonly AutomaticCheckpointCoordinator Worker;
        internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim Completed = new ManualResetEventSlim();
        private IDisposable _finishedPause;
        private readonly ManualResetEventSlim _readerRelease = new ManualResetEventSlim();
        private Thread _reader;
        internal readonly string Password;

        internal BackgroundCheckpointPowerScenario(string password, bool durable,
            Func<byte[], byte[], Func<FileOrigin, bool, IStreamFactory>> device, bool partial = false)
        {
            Password = password;
            using (var seed = new LiteDatabase(new ConnectionString { Filename = File.Filename, Password = password }))
                BackgroundCheckpointWorkload.Seed(seed);
            var log = FileHelper.GetLogFile(File.Filename);
            var factory = device(System.IO.File.ReadAllBytes(File.Filename),
                System.IO.File.Exists(log) ? System.IO.File.ReadAllBytes(log) : Array.Empty<byte>());
            Engine = new LiteEngine(new EngineSettings
            {
                Filename = File.Filename, Password = password, DurableCommits = durable, TestStreamFactory = factory
            });
            Database = new LiteDatabase(Engine, disposeOnClose: false);
            Worker = Engine.InstallDirectCheckpointLifetime();
            Worker.BeforeCheckpointWorkerPass = () => { Entered.Set(); Wait(Release); };
            Worker.AfterCheckpointWorkerPass = Completed.Set;
            using (Worker.Pause())
            {
                Database.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(1));
                if (partial)
                {
                    using var pinned = new ManualResetEventSlim();
                    _reader = new Thread(() =>
                    {
                        using var reader = Engine.Query("rows", new Query());
                        reader.Read().Should().BeTrue();
                        pinned.Set();
                        _readerRelease.Wait();
                    });
                    _reader.Start();
                    Wait(pinned);
                    Database.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(2));
                    Database.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(3));
                }
                Database.CheckpointSize = 2;
                // The acknowledgement is recorded by the caller only after Update returns.
                BackgroundCheckpointWorkload.Verify(Database, partial ? 3 : 1);
            }
            Wait(Entered);
        }

        internal static Func<FileOrigin, bool, IStreamFactory> Factory(Stream data, Stream log, string password) =>
            (origin, aes) => new StreamFactory(origin == FileOrigin.Data ? data : log,
                aes ? password : null, isLog: origin == FileOrigin.Log);

        internal void FinishPass()
        {
            Release.Set();
            Wait(Completed);
            _finishedPause = Worker.Pause();
            Worker.State.Should().Be(AutomaticCheckpointState.Paused);
        }

        internal static void Wait(ManualResetEventSlim signal) =>
            signal.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue("the requested worker boundary must execute");

        public void Dispose()
        {
            Release.Set();
            _readerRelease.Set();
            if (_reader != null) _reader.Join(20000).Should().BeTrue();
            try { Engine.Dispose(); }
            finally
            {
                try { Database.Dispose(); }
                finally { File.Dispose(); _finishedPause?.Dispose(); _readerRelease.Dispose(); Entered.Dispose(); Release.Dispose(); Completed.Dispose(); }
            }
        }
    }
}
