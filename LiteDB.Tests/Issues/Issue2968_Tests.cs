using System;
using System.IO;
using System.Threading;

using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2968_Tests
    {
        [Fact]
        public void Open_retries_a_transient_sharing_failure_after_mutex_owner_death()
        {
            using var file = new TempFile();
            var settings = new EngineSettings { Filename = file.Filename };
            using var engine = new SharedEngine(settings);
            var mutexName = SharedMutexNameFactory.Create(settings.Filename, settings.SharedMutexNameStrategy);
            using var mutex = SharedMutexFactory.Create(mutexName);

            Exception ownerFailure = null;
            var owner = new Thread(() =>
            {
                try { mutex.WaitOne(); }
                catch (Exception error) { ownerFailure = error; }
            });
            owner.Start();
            owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            ownerFailure.Should().BeNull();

            var attempts = 0;
            engine.SimulateOpenEngine = () =>
            {
                if (attempts++ == 0) throw new IOException("sharing violation");
                return new LiteEngine(settings);
            };

            engine.Pragma(Pragmas.USER_VERSION).AsInt32.Should().Be(0);
            attempts.Should().Be(2);
        }

        [Fact]
        public void Ordinary_open_failure_is_not_retried()
        {
            using var file = new TempFile();
            var settings = new EngineSettings { Filename = file.Filename };
            using var engine = new SharedEngine(settings);
            var attempts = 0;
            engine.SimulateOpenEngine = () =>
            {
                attempts++;
                throw new IOException("ordinary failure");
            };

            Action open = () => engine.Pragma(Pragmas.USER_VERSION);
            open.Should().Throw<IOException>().WithMessage("ordinary failure");
            attempts.Should().Be(1);
        }
    }
}
