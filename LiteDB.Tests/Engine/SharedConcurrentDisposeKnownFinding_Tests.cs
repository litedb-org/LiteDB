using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests.Safety;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Known finding (dev 7b71bc4d, found by the chaos-maintenance fuzz target): a second Dispose of a
    /// Shared connection, concurrent with a first Dispose that is still closing, returns at once
    /// (SharedEngine.Dispose returns when <c>_disposed</c> is already set) while the first still
    /// holds the database's Shared mutex and files. docs/shared-mode-safety.md says "<c>Dispose</c>
    /// returns only after it: a disposed connection holds no mutex, so the next connection's final
    /// close finds it free". The first Dispose is paused deterministically through the close
    /// checkpoint's <c>before-commit-lock</c> stage (evidence class 1). This test pins the CURRENT
    /// behaviour: when it starts failing because the second Dispose waits, the finding is fixed;
    /// update this test and the chaos-maintenance known finding together.
    /// </summary>
    public class SharedConcurrentDisposeKnownFinding_Tests : IDisposable
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-second-dispose-" + Guid.NewGuid().ToString("N"));

        public SharedConcurrentDisposeKnownFinding_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public void Known_finding_a_second_shared_dispose_returns_while_the_first_still_holds_the_mutex()
        {
            var path = Path.Combine(_directory, "second-dispose.db");
            var full = Path.GetFullPath(path);
            using var paused = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            Thread first = null;
            var engine = new SharedEngine(new EngineSettings
            {
                Filename = path,
                CheckpointStage = stage =>
                {
                    // Only the first Dispose's final close checkpoint pauses, before it takes the commit lock.
                    if (stage != "before-commit-lock" || !ReferenceEquals(Thread.CurrentThread, first)) return;
                    paused.Set();
                    release.Wait(Bound);
                }
            });
            // A WAL below the close threshold: the connection's final close checkpoints it.
            for (var id = 1; id <= 3; id++)
                engine.Insert("rows", new[] { new BsonDocument { ["_id"] = id, ["payload"] = new string('p', 3000) } }, BsonAutoId.Int32);

            Exception firstError = null;
            first = new Thread(() =>
            {
                try { engine.Dispose(); }
                catch (Exception error) { firstError = error; }
            }) { IsBackground = true, Name = "first dispose" };
            Exception secondError = null;
            var second = new Thread(() =>
            {
                try { engine.Dispose(); }
                catch (Exception error) { secondError = error; }
            }) { IsBackground = true, Name = "second dispose" };
            try
            {
                first.Start();
                paused.Wait(Bound).Should().BeTrue("the first Dispose reaches its final close checkpoint");

                second.Start();
                var returned = second.Join(TimeSpan.FromSeconds(5));
                var mutexFree = QuiescentProbe.TryAcquire(SharedMutexNameFactory.Create(full, SharedMutexNameStrategy.Default), out _);
                var gaps = new List<string>();
                var handles = QuiescentProbe.OpenHandles(full, gaps);

                returned.Should().BeTrue("KNOWN FINDING: the second Dispose returns while the first is still closing; " +
                    "if it now waits for the first, the finding is fixed: update this test and known-findings.json");
                secondError.Should().BeNull();
                first.IsAlive.Should().BeTrue("the first Dispose is still paused in its close checkpoint");
                mutexFree.Should().BeFalse("KNOWN FINDING: the disposed connection still holds the Shared mutex after Dispose returned");
                if (handles != null) handles.Should().NotBeEmpty("KNOWN FINDING: the database files are still open after Dispose returned");
            }
            finally
            {
                release.Set();
                first.Join(Bound).Should().BeTrue("the first Dispose completes once released");
                if (second.ThreadState != ThreadState.Unstarted) second.Join(Bound).Should().BeTrue();
            }

            firstError.Should().BeNull();
            // Once the first close completed the connection holds nothing.
            QuiescentProbe.Evaluate(path).Violations.Should().BeEmpty();
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
