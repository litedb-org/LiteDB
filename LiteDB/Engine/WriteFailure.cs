using System;

namespace LiteDB.Engine
{
    /// <summary>
    /// A write or sync that failed. The device is taken as bad: the engine stops, performs no further
    /// write or sync on its handles, and every later operation throws the original failure. A cold
    /// reopen recovers the previously acknowledged state.
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

        /// <summary>The failure itself: a later refusal carries it as its inner exception.</summary>
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

        /// <summary>
        /// The failure names the file whose write or sync failed (<see cref="InFile"/>): it is a write-side
        /// failure, recorded before the stop it causes. A failed read names none.
        /// </summary>
        internal static bool NamesFile(Exception error) => error.Data.Contains(FileDataKey);

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
