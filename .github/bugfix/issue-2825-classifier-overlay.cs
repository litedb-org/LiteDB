using LiteDB;

namespace Issue_2825_FreeListRace;

internal static class FreeListFailureClassifier
{
    public static bool IsReportedFailure(Exception error)
    {
        if (error is not AggregateException aggregate)
        {
            return false;
        }

        var failures = aggregate.Flatten().InnerExceptions;
        // Other workers can fail while rolling back after the primary page error.
        // Those errors alone do not prove #2825, and unrelated failures must surface.
        return failures.Any(IsPrimaryFailure) &&
            failures.All(failure => IsPrimaryFailure(failure) || IsRollbackFailure(failure) ||
                IsConcurrentWriteFailure(failure) || IsClosedFileTransactionFailure(failure) ||
                IsClosedTransactionFailure(failure));
    }

    private static bool IsPrimaryFailure(Exception error)
    {
        return error is LiteException lite && lite.ErrorCode == LiteException.INVALID_DATAFILE_STATE &&
            error.Message == "empty page must be defined as empty type" &&
            (error.StackTrace?.Contains("LiteDB.Engine.Snapshot.NewPage") == true ||
                error.StackTrace?.Contains("LiteDB.Engine.EngineState.Validate") == true ||
                // Historical concurrent rethrows can overwrite the shared exception's
                // original frames, leaving only the public insertion boundary.
                error.StackTrace?.Contains("LiteDB.Engine.LiteEngine.Insert") == true);
    }

    private static bool IsRollbackFailure(Exception error)
    {
        return error is LiteException lite && lite.ErrorCode == LiteException.INVALID_DATAFILE_STATE &&
            error.Message == "discarded page must be writable" &&
            ((error.StackTrace?.Contains("LiteDB.Engine.MemoryCache.DiscardPage") == true &&
                error.StackTrace.Contains("LiteDB.Engine.TransactionService.Rollback")) ||
                error.StackTrace?.Contains("LiteDB.Engine.EngineState.Validate") == true);
    }

    private static bool IsConcurrentWriteFailure(Exception error)
    {
        return error is LiteException lite && lite.ErrorCode == LiteException.INVALID_DATAFILE_STATE &&
            error.Message == "page must be writable to support changes" &&
            ((error.StackTrace?.Contains("LiteDB.Engine.BasePage.Delete") == true &&
                error.StackTrace.Contains("LiteDB.Engine.IndexService.DeleteAll")) ||
             (error.StackTrace?.Contains("LiteDB.Engine.BasePage.InternalInsert") == true &&
                (error.StackTrace.Contains("LiteDB.Engine.IndexService.AddNode") ||
                 ContainsAdjacentFrames(error.StackTrace,
                    "at LiteDB.Engine.BasePage.InternalInsert(",
                    "at LiteDB.Engine.BasePage.Insert(",
                    "at LiteDB.Engine.DataPage.InsertBlock(",
                    "at LiteDB.Engine.DataService.<>c__DisplayClass4_0.<<Insert>g__source|0>d.MoveNext(",
                    "at LiteDB.Engine.BufferWriter.MoveForward(",
                    "at LiteDB.Engine.BufferWriter.Write(",
                    "at LiteDB.Engine.BufferWriter.WriteString(",
                    "at LiteDB.Engine.BufferWriter.WriteElement(",
                    "at LiteDB.Engine.BufferWriter.WriteDocument(",
                    "at LiteDB.Engine.DataService.Insert(",
                    "at LiteDB.Engine.LiteEngine.InsertDocument("))) ||
                error.StackTrace?.Contains("LiteDB.Engine.EngineState.Validate") == true);
    }

    private static bool ContainsAdjacentFrames(string stackTrace, params string[] frames)
    {
        var lines = stackTrace.Replace("\r\n", "\n").Split('\n');
        for (var start = 0; start <= lines.Length - frames.Length; start++)
        {
            if (frames.Select((frame, index) => lines[start + index].TrimStart().StartsWith(
                frame, StringComparison.Ordinal)).All(matches => matches)) return true;
        }
        return false;
    }

    private static bool IsClosedFileTransactionFailure(Exception error)
    {
        return error is ObjectDisposedException && error.Message == "Cannot access a closed file." &&
            error.StackTrace?.Contains("LiteDB.Engine.DiskWriterQueue.EnqueuePage") == true &&
            (error.StackTrace.Contains("LiteDB.Engine.TransactionService.Rollback") ||
                (error.StackTrace.Contains("LiteDB.Engine.TransactionService.PersistDirtyPages") &&
                    error.StackTrace.Contains("LiteDB.Engine.TransactionService.Commit")));
    }

    private static bool IsClosedTransactionFailure(Exception error)
    {
        // Historical engine shutdown disposes all monitored transactions, including
        // a different worker that has not yet acquired its collection snapshot.
        return error is LiteException lite && lite.ErrorCode == LiteException.INVALID_DATAFILE_STATE &&
            error.Message == "transaction must be active to create new snapshot" &&
            error.StackTrace?.Contains("LiteDB.Engine.TransactionService.CreateSnapshot") == true &&
            error.StackTrace.Contains("LiteDB.Engine.LiteEngine.Delete") &&
            error.StackTrace.Contains("LiteDB.Engine.LiteEngine.AutoTransaction");
    }
}
