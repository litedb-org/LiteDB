#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOneFileOwnershipProcess_Tests
    {
        [OneFileTheory]
        [InlineData(null, "commit")]
        [InlineData(null, "rollback")]
        [InlineData(null, "kill")]
        [InlineData("secret", "commit")]
        [InlineData("secret", "rollback")]
        [InlineData("secret", "kill")]
        public async Task Two_process_writers_contend_then_successor_recovers_without_aborted_rows(string password, string completion)
        {
            using var file = new MappedTestFile();
            Seed(file, password);
            using (var owner = new MvccProcess("one-file-ownership", file, password))
            using (var peer = new MvccProcess("one-file-ownership", file, password))
            {
                await owner.Expect("attached");
                await peer.Expect("attached");
                owner.ProcessId.Should().NotBe(peer.ProcessId);
                owner.Send("own");
                await owner.Expect("owned");
                peer.Send("write");
                await peer.Expect("attempting");
                // An acquired turnstile proves the peer reached native contention,
                // rather than merely being delayed during process startup.
                using var turn = SharedMutexFactory.Create(SharedMutexNameFactory.Create(file, SharedMutexNameStrategy.Default) + ".Turn");
                var probe = new SharedMutexTurnstile(turn);
                SpinWait.SpinUntil(probe.HasWaiter, TimeSpan.FromSeconds(5)).Should().BeTrue();
                if (completion == "kill") await owner.Kill(sharedFilesRemainOpen: true);
                else
                {
                    owner.Send(completion);
                    await owner.Expect("released");
                }
                (await peer.ReadLine(TimeSpan.FromSeconds(5))).Should().Be("written");
                await Close(peer);
                if (completion != "kill") await Close(owner);
            }
            var expected = new[] { 1 }.Concat(completion == "commit" ? new[] { 1000 } : Array.Empty<int>())
                .Concat(Enumerable.Range(3000, 20)).ToArray();
            VerifyTwice(file, password, expected);
        }

        [OneFileTheory]
        [InlineData(null, "retry-commit")]
        [InlineData(null, "retry-rollback")]
        [InlineData(null, "retry-failed")]
        [InlineData("secret", "retry-commit")]
        [InlineData("secret", "retry-rollback")]
        [InlineData("secret", "retry-failed")]
        public async Task Issue3094_3125_repeated_and_failed_retries_release_to_another_process_while_owner_stays_alive(string password, string route)
        {
            using var file = new MappedTestFile();
            Seed(file, password);
            using (var owner = new MvccProcess("one-file-ownership", file, password))
            using (var peer = new MvccProcess("one-file-ownership", file, password))
            {
                await owner.Expect("attached");
                await peer.Expect("attached");
                owner.Send(route);
                if (route == "retry-failed")
                    for (var retry = 0; retry < 3; retry++) await owner.Expect("retry-failed");
                await owner.Expect("released");
                peer.Send("write");
                await peer.Expect("attempting");
                (await peer.ReadLine(TimeSpan.FromSeconds(5))).Should().Be("written");
                await Close(peer);
                // The originating process remains alive, retaining its mapped
                // participant, until the remote writer has finished all 20 writes.
                await Close(owner);
            }
            var expected = new[] { 1 }.Concat(route == "retry-rollback" ? Array.Empty<int>() : new[] { 2000 })
                .Concat(Enumerable.Range(3000, 20)).ToArray();
            VerifyTwice(file, password, expected);
        }

        private static async Task Close(MvccProcess child)
        {
            child.Send("close");
            await child.Expect("done");
            await child.Finish();
        }
        private static BsonDocument Row(int id) => new BsonDocument
        { ["_id"] = id, ["value"] = id, ["payload"] = new string('x', 20000) };
        private static void Seed(string filename, string password)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password });
            db.GetCollection("docs").Insert(Row(1));
            db.GetCollection("docs").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(Row(42));
            db.GetCollection("sentinel").EnsureIndex("value");
        }
        private static void VerifyTwice(string filename, string password, IEnumerable<int> ids)
        {
            var expected = ids.Select(Row).ToArray();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password });
                db.GetCollection("docs").FindAll().Should().BeEquivalentTo(expected);
                foreach (var row in expected)
                    db.GetCollection("docs").Find(Query.EQ("value", row["value"])).Should().BeEquivalentTo(new[] { row });
                db.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42) });
                db.GetCollection("sentinel").Find(Query.EQ("value", 42)).Should().BeEquivalentTo(new[] { Row(42) });
            }
        }
    }
}
#endif
