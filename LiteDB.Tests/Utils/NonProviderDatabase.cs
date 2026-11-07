using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using LiteDB.Engine;

namespace LiteDB.Tests
{
    /// <summary>
    /// An <see cref="ILiteDatabase"/> that is not an <see cref="ILiteTransactionProvider"/>, the
    /// shape of a hand-written test double or third-party wrapper. Every member records its call
    /// and returns a default value, so a caller's side effects on it are observable.
    /// </summary>
    internal sealed class NonProviderDatabase : ILiteDatabase
    {
        internal readonly List<string> Calls = new List<string>();

        private T Record<T>(T result = default, [CallerMemberName] string member = null)
        {
            Calls.Add(member);
            return result;
        }

        public BsonMapper Mapper => Record<BsonMapper>();
        public ILiteStorage<string> FileStorage => Record<ILiteStorage<string>>();
        public ILiteCollection<T> GetCollection<T>(string name, BsonAutoId autoId = BsonAutoId.ObjectId) => Record<ILiteCollection<T>>();
        public ILiteCollection<T> GetCollection<T>() => Record<ILiteCollection<T>>();
        public ILiteCollection<T> GetCollection<T>(BsonAutoId autoId) => Record<ILiteCollection<T>>();
        public ILiteCollection<BsonDocument> GetCollection(string name, BsonAutoId autoId = BsonAutoId.ObjectId) => Record<ILiteCollection<BsonDocument>>();
        public bool BeginTrans() => Record(false);
        public bool Commit() => Record(false);
        public bool Rollback() => Record(false);
        public ILiteStorage<TFileId> GetStorage<TFileId>(string filesCollection = "_files", string chunksCollection = "_chunks") => Record<ILiteStorage<TFileId>>();
        public IEnumerable<string> GetCollectionNames() => Record<IEnumerable<string>>(new string[0]);
        public bool CollectionExists(string name) => Record(false);
        public bool DropCollection(string name) => Record(false);
        public bool RenameCollection(string oldName, string newName) => Record(false);
        public IBsonDataReader Execute(TextReader commandReader, BsonDocument parameters = null) => Record<IBsonDataReader>();
        public IBsonDataReader Execute(string command, BsonDocument parameters = null) => Record<IBsonDataReader>();
        public IBsonDataReader Execute(string command, params BsonValue[] args) => Record<IBsonDataReader>();
        public void Checkpoint() => Record(0);
        public long Rebuild(RebuildOptions options = null) => Record(0L);
        public BsonValue Pragma(string name) => Record<BsonValue>();
        public BsonValue Pragma(string name, BsonValue value) => Record<BsonValue>();
        public int UserVersion { get => Record(0); set => Record(0); }
        public TimeSpan Timeout { get => Record(TimeSpan.Zero); set => Record(0); }
        public bool UtcDate { get => Record(false); set => Record(0); }
        public long LimitSize { get => Record(0L); set => Record(0); }
        public int CheckpointSize { get => Record(0); set => Record(0); }
        public Collation Collation => Record<Collation>();
        public void Dispose() => Record(0);
    }
}
