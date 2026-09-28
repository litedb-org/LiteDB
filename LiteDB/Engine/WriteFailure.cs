using System;

namespace LiteDB.Engine
{
    /// <summary>
    /// A write or sync that failed (docs/decisions/durability-policy.md, decision 6). The device is
    /// taken as bad: the engine stops writing, reopens read-only on its next call, and refuses every
    /// later write until the database is reopened; <c>$database.writeFailure</c> reports this record.
    /// </summary>
    internal sealed class WriteFailure
    {
        /// <summary>Exception.Data key: the file ("data" or "log") whose write or sync failed.</summary>
        internal const string FileDataKey = "LiteDB.FailedFile";

        internal WriteFailure(string operation, Exception error, bool walKept)
        {
            this.File = error.Data[FileDataKey] as string;
            this.Cause = error;
            this.Operation = operation;
            this.Error = error.Message;
            this.Time = DateTime.UtcNow;
            this.WalKept = walKept;
        }

        /// <summary>"data", "log", or null when the failure did not say.</summary>
        internal string File { get; }

        internal string Operation { get; }

        /// <summary>The failure itself: every later write's error carries it as its inner exception.</summary>
        internal Exception Cause { get; }

        internal string Error { get; }

        internal DateTime Time { get; }

        /// <summary>The log file still held the WAL when the write failed: nothing it held was removed.</summary>
        internal bool WalKept { get; }

        /// <summary>Name the file whose write or sync failed, unless a deeper failure already did.</summary>
        internal static T InFile<T>(T error, FileOrigin origin) where T : Exception
        {
            if (!error.Data.Contains(FileDataKey)) error.Data[FileDataKey] = origin == FileOrigin.Log ? "log" : "data";
            return error;
        }

        internal BsonDocument ToDocument() => new BsonDocument
        {
            ["file"] = this.File == null ? BsonValue.Null : new BsonValue(this.File),
            ["operation"] = this.Operation,
            ["error"] = this.Error,
            ["time"] = this.Time,
            ["walKept"] = this.WalKept
        };

        public override string ToString() =>
            $"{this.Operation} failed at {this.Time:yyyy-MM-dd HH:mm:ss} UTC" +
            (this.File == null ? "" : $" on the {this.File} file") + $": {this.Error} " +
            (this.WalKept ? "The log file was kept." : "The log file was empty.");
    }
}
