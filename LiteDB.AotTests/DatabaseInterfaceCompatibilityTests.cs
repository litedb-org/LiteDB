using System;
using System.Collections.Generic;
using System.IO;

using LiteDB.Engine;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests;

[TestClass]
public sealed class DatabaseInterfaceCompatibilityTests
{
    [TestMethod]
    public void LegacyDatabaseDecorator_CompilesAndSupportsFileStorageWithoutGeneratedCollectionMember()
    {
        using var database = new LiteDatabase(new MemoryStream());
        using var decorator = new LegacyDatabaseDecorator(database);
        var storage = new LiteStorage<string>(decorator, "files", "chunks");

        storage.Upload("one", "one.txt", new MemoryStream(new byte[] { 1, 2, 3 }));
        Assert.AreEqual(1, decorator.TypedCollectionCalls);
        Assert.AreEqual("one.txt", storage.FindById("one").Filename);
        using var downloaded = new MemoryStream();
        storage.Download("one", downloaded);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, downloaded.ToArray());
        Assert.IsTrue(storage.Delete("one"));
    }

    // Implements the pre-PR interface. Adding a new required member breaks this fixture at compile time,
    // just as it would break an application's existing database wrapper.
    private sealed class LegacyDatabaseDecorator : ILiteDatabase
    {
        private readonly LiteDatabase _database;

        public LegacyDatabaseDecorator(LiteDatabase database) => _database = database;
        public int TypedCollectionCalls { get; private set; }
        public BsonMapper Mapper => _database.Mapper;
        public ILiteStorage<string> FileStorage => _database.FileStorage;
        public ILiteCollection<T> GetCollection<T>(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            TypedCollectionCalls++;
            return _database.GetCollection<T>(name, autoId);
        }
        public ILiteCollection<T> GetCollection<T>() => _database.GetCollection<T>();
        public ILiteCollection<T> GetCollection<T>(BsonAutoId autoId) => _database.GetCollection<T>(autoId);
        public ILiteCollection<BsonDocument> GetCollection(string name, BsonAutoId autoId = BsonAutoId.ObjectId) => _database.GetCollection(name, autoId);
        public ILiteStorage<T> GetStorage<T>(string filesCollection = "_files", string chunksCollection = "_chunks") => _database.GetStorage<T>(filesCollection, chunksCollection);
        public bool BeginTrans() => _database.BeginTrans();
        public bool Commit() => _database.Commit();
        public bool Rollback() => _database.Rollback();
        public IEnumerable<string> GetCollectionNames() => _database.GetCollectionNames();
        public bool CollectionExists(string name) => _database.CollectionExists(name);
        public bool DropCollection(string name) => _database.DropCollection(name);
        public bool RenameCollection(string oldName, string newName) => _database.RenameCollection(oldName, newName);
        public IBsonDataReader Execute(TextReader commandReader, BsonDocument parameters = null!) => _database.Execute(commandReader, parameters);
        public IBsonDataReader Execute(string command, BsonDocument parameters = null!) => _database.Execute(command, parameters);
        public IBsonDataReader Execute(string command, params BsonValue[] args) => _database.Execute(command, args);
        public void Checkpoint() => _database.Checkpoint();
        public long Rebuild(RebuildOptions options = null!) => _database.Rebuild(options);
        public BsonValue Pragma(string name) => _database.Pragma(name);
        public BsonValue Pragma(string name, BsonValue value) => _database.Pragma(name, value);
        public int UserVersion { get => _database.UserVersion; set => _database.UserVersion = value; }
        public TimeSpan Timeout { get => _database.Timeout; set => _database.Timeout = value; }
        public bool UtcDate { get => _database.UtcDate; set => _database.UtcDate = value; }
        public long LimitSize { get => _database.LimitSize; set => _database.LimitSize = value; }
        public int CheckpointSize { get => _database.CheckpointSize; set => _database.CheckpointSize = value; }
        public Collation Collation => _database.Collation;
        public void Dispose() { }
    }
}
