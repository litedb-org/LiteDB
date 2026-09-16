using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2871_Tests
    {
        private const int ExpectedValue = 2871;
        private const int ReaderCount = 4;
        private const int MinimumReadsPerReader = 4096;

        public interface IConstructorProbe
        {
            Type LeftType { get; }
            Type RightType { get; }
            int Value { get; }
        }

        public sealed class ConstructorProbe<TLeft, TRight> : IConstructorProbe
        {
            public Type LeftType => typeof(TLeft);
            public Type RightType => typeof(TRight);
            public int Value => ExpectedValue;
        }

        public sealed class ReaderProbe
        {
            public int Value => ExpectedValue;
        }

        public sealed class TypeTag<T>
        {
        }

        [Fact(Timeout = 45000)]
        public async Task Cache_hits_remain_correct_while_unique_constructor_types_force_resizes()
        {
            var targetTypes = CreateTargetTypes();
            AssertWorkloadForcesResize(GetConstructorCache(), targetTypes.Length);
            var firstInstances = new object[targetTypes.Length];
            var errors = new ConcurrentQueue<Exception>();
            var completedReads = 0L;
            var writerDone = 0;

            var warm = Reflection.CreateInstance(typeof(ReaderProbe));
            AssertReaderProbe(warm, null);

            using (var start = new ManualResetEventSlim())
            using (var ready = new CountdownEvent(ReaderCount + 1))
            using (var cancellation = new CancellationTokenSource())
            {
                var workers = new List<Task>();

                for (var worker = 0; worker < ReaderCount; worker++)
                {
                    workers.Add(StartWorker(ready, start, cancellation.Token, errors, () =>
                    {
                        object previous = null;
                        var localReads = 0;

                        while (localReads < MinimumReadsPerReader || Volatile.Read(ref writerDone) == 0)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            var current = Reflection.CreateInstance(typeof(ReaderProbe));
                            AssertReaderProbe(current, previous);
                            previous = current;
                            localReads++;
                        }

                        Interlocked.Add(ref completedReads, localReads);
                    }));
                }

                workers.Add(StartWorker(ready, start, cancellation.Token, errors, () =>
                {
                    try
                    {
                        for (var index = 0; index < targetTypes.Length; index++)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            var instance = Reflection.CreateInstance(targetTypes[index]);
                            AssertConstructorProbe(instance, targetTypes[index]);
                            firstInstances[index] = instance;
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref writerDone, 1);
                    }
                }));

                var allWorkers = Task.WhenAll(workers);
                var allStarted = ready.Wait(TimeSpan.FromSeconds(10));
                start.Set();
                allStarted.Should().BeTrue("all dedicated workers must reach the bounded start gate");

                var completedInTime = await Task.WhenAny(
                    allWorkers,
                    Task.Delay(TimeSpan.FromSeconds(30))) == allWorkers;
                if (!completedInTime) cancellation.Cancel();

                completedInTime.Should().BeTrue("constructor-cache readers and writers must not hang");
                await allWorkers;
                errors.Should().BeEmpty("no cache hit may throw or return a corrupt constructor result");
            }

            completedReads.Should().BeGreaterThanOrEqualTo(ReaderCount * MinimumReadsPerReader);
            firstInstances.Should().OnlyContain(instance => instance != null);

            for (var index = 0; index < targetTypes.Length; index++)
            {
                var second = Reflection.CreateInstance(targetTypes[index]);
                AssertConstructorProbe(second, targetTypes[index]);
                second.Should().NotBeSameAs(firstInstances[index],
                    "a cached constructor delegate must still create a fresh object");
            }

            var finalReader = Reflection.CreateInstance(typeof(ReaderProbe));
            AssertReaderProbe(finalReader, warm);

            var list = Reflection.CreateInstance(typeof(IList<ReaderProbe>)) as IList<ReaderProbe>;
            list.Should().NotBeNull("interface-to-concrete recursion must remain usable");
            list.GetType().Should().Be(typeof(List<ReaderProbe>));
            list.Add((ReaderProbe)finalReader);
            list.Single().Value.Should().Be(ExpectedValue);
        }

        [Fact]
        public void Plain_constructor_dictionary_serializes_cache_hit_reads()
        {
            var cache = GetConstructorCache();
            var first = Reflection.CreateInstance(typeof(ReaderProbe));
            AssertReaderProbe(first, null);

            if (!(cache is Dictionary<Type, CreateObject>))
            {
                return;
            }

            var missType = typeof(ConstructorProbe<ReaderProbe, ReaderProbe>);
            object missResult = null;
            object hitResult = null;
            Exception missError = null;
            Exception hitError = null;

            using (var missReady = new ManualResetEventSlim())
            using (var hitReady = new ManualResetEventSlim())
            using (var invokeMiss = new ManualResetEventSlim())
            using (var invokeHit = new ManualResetEventSlim())
            {
                var miss = StartThread("Issue2871 cache-miss writer", missReady, invokeMiss,
                    () => missResult = Reflection.CreateInstance(missType),
                    ex => missError = ex);
                var hit = StartThread("Issue2871 cache-hit reader", hitReady, invokeHit,
                    () => hitResult = Reflection.CreateInstance(typeof(ReaderProbe)),
                    ex => hitError = ex);

                missReady.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the miss writer must start");
                hitReady.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the hit reader must start");

                var lockTaken = false;
                bool missCompletedWhileHeld;
                var hitCompletedWhileHeld = false;
                try
                {
                    Monitor.Enter(cache, ref lockTaken);
                    invokeMiss.Set();
                    missCompletedWhileHeld = miss.Join(TimeSpan.FromSeconds(1));

                    if (!missCompletedWhileHeld)
                    {
                        invokeHit.Set();
                        hitCompletedWhileHeld = hit.Join(TimeSpan.FromSeconds(1));
                    }
                }
                finally
                {
                    if (lockTaken) Monitor.Exit(cache);
                    invokeHit.Set();
                }

                miss.Join(TimeSpan.FromSeconds(5)).Should().BeTrue("the miss must finish after release");
                hit.Join(TimeSpan.FromSeconds(5)).Should().BeTrue("the hit must finish after release");
                missError.Should().BeNull();
                hitError.Should().BeNull();
                AssertConstructorProbe(missResult, missType);
                AssertReaderProbe(hitResult, first);

                missCompletedWhileHeld.Should().BeFalse(
                    "a retained mutable Dictionary must use its own monitor for production writes");
                hitCompletedWhileHeld.Should().BeFalse(
                    "a hit must use the dictionary monitor when production writes use that monitor");
            }
        }

        private static Thread StartThread(
            string name,
            ManualResetEventSlim ready,
            ManualResetEventSlim invoke,
            Action action,
            Action<Exception> fail)
        {
            var thread = new Thread(() =>
            {
                ready.Set();
                invoke.Wait();

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    fail(ex);
                }
            })
            {
                IsBackground = true,
                Name = name
            };
            thread.Start();
            return thread;
        }

        private static Task StartWorker(
            CountdownEvent ready,
            ManualResetEventSlim start,
            CancellationToken cancellation,
            ConcurrentQueue<Exception> errors,
            Action action)
        {
            return Task.Factory.StartNew(() =>
            {
                ready.Signal();

                try
                {
                    start.Wait(cancellation);
                    action();
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static Type[] CreateTargetTypes()
        {
            var layer = new[]
            {
                typeof(byte), typeof(short), typeof(int), typeof(long),
                typeof(float), typeof(double), typeof(decimal), typeof(bool),
                typeof(char), typeof(string), typeof(DateTime), typeof(DateTimeOffset),
                typeof(TimeSpan), typeof(Guid), typeof(Uri), typeof(Version)
            };
            var arguments = new List<Type>(64);

            for (var depth = 0; depth < 4; depth++)
            {
                arguments.AddRange(layer);
                layer = layer.Select(type => typeof(TypeTag<>).MakeGenericType(type)).ToArray();
            }

            return arguments
                .SelectMany(left => arguments.Select(right =>
                    typeof(ConstructorProbe<,>).MakeGenericType(left, right)))
                .ToArray();
        }

        private static void AssertReaderProbe(object instance, object previous)
        {
            if (instance == null || instance.GetType() != typeof(ReaderProbe))
            {
                throw new InvalidOperationException("The cached reader constructor returned the wrong runtime type.");
            }

            if (((ReaderProbe)instance).Value != ExpectedValue)
            {
                throw new InvalidOperationException("The cached reader constructor returned the wrong value.");
            }

            if (ReferenceEquals(instance, previous))
            {
                throw new InvalidOperationException("The cached reader constructor reused an object instance.");
            }
        }

        private static void AssertConstructorProbe(object instance, Type expectedType)
        {
            if (instance == null || instance.GetType() != expectedType)
            {
                throw new InvalidOperationException("A first-time constructor returned the wrong runtime type.");
            }

            var arguments = expectedType.GetGenericArguments();
            var probe = instance as IConstructorProbe;

            if (probe == null || probe.LeftType != arguments[0] || probe.RightType != arguments[1] ||
                probe.Value != ExpectedValue)
            {
                throw new InvalidOperationException(
                    "A constructor result did not preserve its type arguments and value.");
            }
        }

        private static object GetConstructorCache()
        {
            var field = typeof(Reflection).GetField("_cacheCtor", BindingFlags.Static | BindingFlags.NonPublic);
            field.Should().NotBeNull("the regression must inspect the constructor cache used by CreateInstance");
            var cache = field.GetValue(null);
            cache.Should().NotBeNull();
            return cache;
        }

        private static void AssertWorkloadForcesResize(object cache, int additions)
        {
            var dictionary = cache as Dictionary<Type, CreateObject>;

            if (dictionary == null) return;

            var bucketsField = cache.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(int[]));
            var buckets = (int[])bucketsField.GetValue(cache);
            var capacity = buckets == null ? 0 : buckets.Length;

            additions.Should().BeGreaterThan(capacity - dictionary.Count,
                "the unique types must cross a live Dictionary capacity boundary");
        }
    }
}
