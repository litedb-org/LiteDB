using System;
using System.Collections.Generic;
using System.IO;

using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Utils
{
    [Collection(nameof(LoggingCollection))]
    public class Logging_Tests
    {
        [Fact]
        public void Subscriber_ReceivesDatabaseMessages()
        {
            var entries = new List<LogEventArgs>();
            Action<LogEventArgs> subscriber = entries.Add;

            Logging.LogCallback += subscriber;

            try
            {
                using var database = new LiteDatabase(":memory:");
                database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            }
            finally
            {
                Logging.LogCallback -= subscriber;
            }

            entries.Should().Contain(entry =>
                entry.Category == "COMMAND" && entry.Message == "insert `docs`");
        }

        [Fact]
        public void FailingSubscriber_DoesNotStopRemainingSubscribers()
        {
            var calls = new List<string>();
            Action<LogEventArgs> failing = _ =>
            {
                calls.Add("failing");
                throw new InvalidOperationException("logger failed");
            };
            Action<LogEventArgs> remaining = args => calls.Add(args.Message);

            Logging.LogCallback += failing;
            Logging.LogCallback += remaining;

            try
            {
                Logging.LOG("received", "TEST");
            }
            finally
            {
                Logging.LogCallback -= failing;
                Logging.LogCallback -= remaining;
            }

            calls.Should().Equal("failing", "received");
        }

        [Fact]
        public void SubscriberGeneratedMessage_IsSuppressed()
        {
            var calls = new List<string>();
            Action<LogEventArgs> recursive = args =>
            {
                calls.Add("recursive:" + args.Message);
                Logging.LOG("nested", "TEST");
            };
            Action<LogEventArgs> remaining = args => calls.Add("remaining:" + args.Message);

            Logging.LogCallback += recursive;
            Logging.LogCallback += remaining;

            try
            {
                Logging.LOG("outer", "TEST");
            }
            finally
            {
                Logging.LogCallback -= recursive;
                Logging.LogCallback -= remaining;
            }

            calls.Should().Equal("recursive:outer", "remaining:outer");
        }

#if !NETFRAMEWORK
        [Fact]
        public void NoSubscriber_Log_DoesNotAllocate()
        {
            Logging.IsEnabled.Should().BeFalse();
            Logging.LOG("warmup", "TEST");

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 1000; i++)
            {
                Logging.LOG("message", "TEST");
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            allocated.Should().Be(0);
        }
#endif

        [Fact]
        public void FatalOpen_FailingSubscriber_PreservesErrorAndReleasesFile()
        {
            using var file = new TempFile();
            var invalidHeader = new byte[Constants.PAGE_SIZE];
            invalidHeader[0] = 1;
            File.WriteAllBytes(file.Filename, invalidHeader);

            Exception observed = null;
            Action<LogEventArgs> failing = _ => throw new InvalidOperationException("logger failed");
            Action<LogEventArgs> remaining = args => observed = args.Exception ?? observed;

            Logging.LogCallback += failing;
            Logging.LogCallback += remaining;

            LiteException exception;

            try
            {
                Action open = () => new LiteEngine(file.Filename);
                exception = open.Should().Throw<LiteException>()
                    .WithMessage("This data file is encrypted and needs a password to open")
                    .Which;
            }
            finally
            {
                Logging.LogCallback -= failing;
                Logging.LogCallback -= remaining;
            }

            observed.Should().BeSameAs(exception);

            using var exclusive = new FileStream(
                file.Filename,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
    }

    [CollectionDefinition(nameof(LoggingCollection), DisableParallelization = true)]
    public sealed class LoggingCollection
    {
    }
}
