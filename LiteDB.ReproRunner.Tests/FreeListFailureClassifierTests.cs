using System.Runtime.ExceptionServices;
using Issue_2825_FreeListRace;
using LiteDB;

namespace LiteDB.ReproRunner.Tests;

public class FreeListFailureClassifierTests
{
    private const string PrimaryMessage = "empty page must be defined as empty type";
    private const string RollbackMessage = "discarded page must be writable";
    private const string PrimaryStack = "   at LiteDB.Engine.Snapshot.NewPage[T](Byte pageType)";
    private const string RollbackStack = "   at LiteDB.Engine.MemoryCache.DiscardPage(PageBuffer page)\n" +
        "   at LiteDB.Engine.TransactionService.Rollback()";
    private const string DeleteMessage = "page must be writable to support changes";
    private const string DeleteStack = "   at LiteDB.Engine.BasePage.Delete(Byte index)\n" +
        "   at LiteDB.Engine.IndexService.DeleteAll(PageAddress pkAddress)";
    private const string CommitStack = "   at LiteDB.Engine.DiskWriterQueue.EnqueuePage(PageBuffer page)\n" +
        "   at LiteDB.Engine.DiskService.WriteAsync(IEnumerable`1 pages)\n" +
        "   at LiteDB.Engine.TransactionService.PersistDirtyPages(Boolean commit)\n" +
        "   at LiteDB.Engine.TransactionService.Commit()\n" +
        "   at LiteDB.Engine.LiteEngine.CommitAndReleaseTransaction(TransactionService transaction)";

    [Fact]
    public void Primary_failure_with_secondary_worker_rollbacks_is_reported()
    {
        var error = new AggregateException(
            new AggregateException(Failure(PrimaryMessage, PrimaryStack)),
            Failure(PrimaryMessage, "   at LiteDB.Engine.EngineState.Validate()\n" +
                "   at LiteDB.Engine.LiteEngine.AutoTransaction[T](Func`2 fn)"),
            Failure(PrimaryMessage, "   at LiteDB.Engine.LiteEngine.Insert(String collection, IEnumerable`1 docs, BsonAutoId autoId)"),
            Failure(RollbackMessage, RollbackStack),
            Failure(RollbackMessage, RollbackStack),
            Failure(DeleteMessage, DeleteStack),
            Failure(DeleteMessage, "   at LiteDB.Engine.EngineState.Validate()"),
            Failure(RollbackMessage, "   at LiteDB.Engine.EngineState.Validate()"),
            Failure(DeleteMessage, "   at LiteDB.Engine.BasePage.InternalInsert(UInt16 bytesLength, Byte& index)\n" +
                "   at LiteDB.Engine.IndexService.AddNode(CollectionIndex index, BsonValue key)"),
            ClosedFileRollback());

        Assert.True(FreeListFailureClassifier.IsReportedFailure(error));
        Assert.True(FreeListFailureClassifier.IsReportedFailure(
            new AggregateException(Failure(PrimaryMessage, PrimaryStack))));
    }

    [Fact]
    public void Secondary_errors_without_the_reported_primary_are_not_proof()
    {
        Assert.False(FreeListFailureClassifier.IsReportedFailure(
            new AggregateException(Failure(RollbackMessage, RollbackStack))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException()));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure(RollbackMessage, RollbackStack), Failure(DeleteMessage, DeleteStack))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(ClosedFileRollback())));
    }

    [Fact]
    public void Unrelated_failures_cannot_be_hidden_by_a_primary_failure()
    {
        foreach (var unrelated in new Exception[]
        {
            new Xunit.Sdk.XunitException("Persisted payload is incorrect"),
            new IOException("Storage unavailable"),
            new ObjectDisposedException(null, "Cannot access a closed file."),
            Failure("another engine failure", RollbackStack),
            Failure(DeleteMessage, "   at Application.Delete()"),
            Failure(RollbackMessage, "   at LiteDB.Engine.Snapshot.NewPage[T](Byte pageType)"),
            Failure(RollbackMessage, "   at LiteDB.Engine.MemoryCache.DiscardPage(PageBuffer page)")
        })
        {
            Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
                Failure(PrimaryMessage, PrimaryStack), unrelated)));
        }
    }

    [Fact]
    public void Matching_text_without_the_engine_type_and_origin_is_not_proof()
    {
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            ExceptionDispatchInfo.SetRemoteStackTrace(new InvalidOperationException(PrimaryMessage), PrimaryStack))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure(PrimaryMessage, "   at Application.Insert()"))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            new LiteException(0, PrimaryMessage))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            ExceptionDispatchInfo.SetRemoteStackTrace(new LiteException(0, PrimaryMessage), PrimaryStack))));
    }

    [Fact]
    public void Closed_WAL_during_commit_requires_the_primary_failure_and_observed_write_path()
    {
        var commitFailure = ExceptionDispatchInfo.SetRemoteStackTrace(
            new ObjectDisposedException(null, "Cannot access a closed file."), CommitStack);

        Assert.True(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure(PrimaryMessage, PrimaryStack), commitFailure)));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(commitFailure)));

        foreach (var unrelated in new Exception[]
        {
            ExceptionDispatchInfo.SetRemoteStackTrace(new IOException("Cannot access a closed file."), CommitStack),
            ExceptionDispatchInfo.SetRemoteStackTrace(new ObjectDisposedException("application"), CommitStack),
            ExceptionDispatchInfo.SetRemoteStackTrace(new ObjectDisposedException(null, "Cannot access a closed file."),
                "   at LiteDB.Engine.TransactionService.PersistDirtyPages(Boolean commit)\n" +
                "   at LiteDB.Engine.TransactionService.Commit()"),
            ExceptionDispatchInfo.SetRemoteStackTrace(new ObjectDisposedException(null, "Cannot access a closed file."),
                "   at LiteDB.Engine.DiskWriterQueue.EnqueuePage(PageBuffer page)\n" +
                "   at LiteDB.Engine.TransactionService.Commit()")
        })
        {
            Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
                Failure(PrimaryMessage, PrimaryStack), unrelated)));
        }
    }

    private static Exception Failure(string message, string stack)
    {
        return ExceptionDispatchInfo.SetRemoteStackTrace(new LiteException(LiteException.INVALID_DATAFILE_STATE, message), stack);
    }

    [Fact]
    public void Disposed_transaction_during_delete_requires_the_primary_and_snapshot_path()
    {
        const string message = "transaction must be active to create new snapshot";
        const string stack = "   at LiteDB.Engine.TransactionService.CreateSnapshot(LockMode mode, String collection, Boolean addIfNotExists)\n" +
            "   at LiteDB.Engine.LiteEngine.AutoTransaction[T](Func`2 fn)\n" +
            "   at LiteDB.Engine.LiteEngine.Delete(String collection, IEnumerable`1 ids)";
        var secondary = Failure(message, stack);
        Assert.True(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure(PrimaryMessage, PrimaryStack), secondary)));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(secondary)));
        foreach (var unrelated in new Exception[]
        {
            ExceptionDispatchInfo.SetRemoteStackTrace(new LiteException(0, message), stack),
            ExceptionDispatchInfo.SetRemoteStackTrace(new InvalidOperationException(message), stack),
            Failure(message, "   at Application.Delete()"),
            Failure(message, "   at LiteDB.Engine.TransactionService.CreateSnapshot()"),
            Failure("transaction must be active to rollback", stack)
        })
        {
            Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
                Failure(PrimaryMessage, PrimaryStack), unrelated)));
        }
    }

    private static Exception ClosedFileRollback()
    {
        return ExceptionDispatchInfo.SetRemoteStackTrace(
            new ObjectDisposedException(null, "Cannot access a closed file."),
            "   at LiteDB.Engine.DiskWriterQueue.EnqueuePage(PageBuffer page)\n" +
            "   at LiteDB.Engine.TransactionService.Rollback()");
    }
}
