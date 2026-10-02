using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// The sequential property: one thread runs the case's prefix against a fresh database while the
    /// model runs in lock step. After every command its result must be the model's, the full
    /// contents read back on the same thread must match the model's view (own uncommitted writes
    /// included), and the contents read by another thread must match the committed state (in Shared
    /// mode only while no explicit transaction holds the mutex, since that reader would wait).
    /// </summary>
    public static class SequentialChecker
    {
        /// <summary>Returns null when the case passes, otherwise the first divergence.</summary>
        public static CaseFailure Run(PropertyCase propertyCase, PropertyOptions options)
        {
            CaseFailure failure = null;
            using (var database = new PropertyDatabase(options))
            {
                var worker = new Thread(() =>
                {
                    try { failure = Steps(propertyCase, database, options.ProbeDeadline); }
                    catch (Exception ex) { failure = new CaseFailure("harness", ex.ToString(), "(not reached)"); }
                }) { IsBackground = true, Name = "pbt-sequential" };
                worker.Start();
                if (!worker.Join(options.HangDeadline))
                    return new CaseFailure("hang", "HANG: the sequential case did not finish", "(not reached)", timingBound: true);
            }
            return failure;
        }

        private static CaseFailure Steps(PropertyCase propertyCase, PropertyDatabase database, TimeSpan readDeadline)
        {
            var context = new ThreadContext(0, database.Database, new ConcurrentDictionary<int, object>());
            var model = propertyCase.InitialModel();
            var log = new StringBuilder();
            var step = 0;
            foreach (var command in propertyCase.Commands(0))
            {
                var failure = Step(step, command, context, ref model, log, null);
                for (var c = 0; c < propertyCase.Collections && failure == null; c++)
                {
                    var readBack = new PropertyCommand(OrdinaryAccess.KindName, DataOperations.FindAll, c);
                    failure = Step(step, readBack, context, ref model, null, command);
                    if (failure != null || (model.Mode == ConnectionType.Shared && model.SharedHolder != ModelState.NoOwner)) continue;

                    var committed = ReadOnOtherThread(database, c, readDeadline);
                    if (committed.Kind != OutcomeKind.Ok || committed.Value != model.CommittedContents(c))
                    {
                        failure = new CaseFailure($"sequential:{command.Kind}.{command.Op}:other-thread-read",
                            $"{log}    after step {step} ({command}) another thread read c{c} -> {committed}",
                            $"    committed state c{c}={model.CommittedContents(c)}; model state {model}",
                            timingBound: committed.Kind == OutcomeKind.Uncertain);
                    }
                }
                if (failure != null) return failure;
                step++;
            }
            return null;
        }

        private static CaseFailure Step(int step, PropertyCommand command, ThreadContext context, ref ModelState model, StringBuilder log, PropertyCommand after)
        {
            var kind = AccessKinds.Get(command.Kind);
            var observed = kind.Execute(command, context);
            var outcomes = new List<ModelOutcome>();
            kind.Apply(model, command, 0, outcomes);
            var match = outcomes.FirstOrDefault(o => o.Observation.Equals(observed));
            while (match != null && !match.Completes)
            {
                outcomes.Clear();
                kind.Apply(match.Next, command, 0, outcomes);
                match = outcomes.FirstOrDefault(o => o.Observation.Equals(observed));
            }
            if (match == null)
            {
                var allowed = outcomes.Count == 0 ? "nothing (it would wait)" : string.Join(" | ", outcomes.Select(o => o.Observation.ToString()));
                var origin = after == null ? $"step {step}" : $"read-back after step {step} ({after})";
                var identity = after == null ? $"sequential:{command.Kind}.{command.Op}" : $"sequential:{after.Kind}.{after.Op}:read-back";
                return new CaseFailure(identity,
                    $"{log}    {origin}: {command} -> {observed}",
                    $"    {command} permits {allowed}; model state before it: {model}");
            }
            log?.AppendLine($"    step {step}: {command} -> {observed}");
            model = match.Next;
            return null;
        }

        private static Observation ReadOnOtherThread(PropertyDatabase database, int collection, TimeSpan deadline)
        {
            Observation contents = null;
            var reader = new Thread(() =>
            {
                try { contents = Observation.Ok(database.ReadContents(collection)); }
                catch (Exception ex) { contents = Observation.FromException(ex); }
            }) { IsBackground = true, Name = "pbt-reader" };
            reader.Start();
            // The read may wait for ownership forever (Shared mutex waits are unbounded).
            return reader.Join(deadline) ? contents : Observation.Uncertain;
        }
    }
}
