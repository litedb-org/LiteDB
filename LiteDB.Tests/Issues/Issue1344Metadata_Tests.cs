using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344Metadata_Tests
    {
        [Fact]
        public void Metadata_update_preserves_registered_file_serializers()
        {
            var ordinary = new BsonMapper();
            var mapper = new BsonMapper();
            mapper.RegisterType<LiteFileInfo<string>>(file =>
            {
                var doc = ordinary.ToDocument(file);
                doc["details"] = doc["metadata"];
                doc.Remove("metadata");
                return doc;
            }, value =>
            {
                var doc = new BsonDocument(value.AsDocument.GetElements().ToDictionary(item => item.Key, item => item.Value));
                doc["metadata"] = doc["details"];
                doc.Remove("details");
                return ordinary.ToObject<LiteFileInfo<string>>(doc);
            });
            using var db = new LiteDatabase(":memory:", mapper);
            using var source = new MemoryStream(new byte[] { 1, 2 });
            db.FileStorage.Upload("target", "kept", source);
            Assert.True(db.FileStorage.SetMetadata("target", new BsonDocument { ["answer"] = 42 }));
            Assert.Equal(42, db.FileStorage.FindById("target").Metadata["answer"].AsInt32);
            Assert.False(db.GetCollection("_files").FindById("target").ContainsKey("metadata"));
        }

        [Theory]
        [InlineData("details")]
        [InlineData("file metadata")]
        public void Metadata_update_preserves_custom_field_mapping(string fieldName)
        {
            var mapper = new BsonMapper();
            mapper.Entity<LiteFileInfo<string>>().Field(file => file.Metadata, fieldName);
            using var db = new LiteDatabase(":memory:", mapper);
            using var source = new MemoryStream(new byte[] { 1, 2 });
            db.FileStorage.Upload("target", "kept", source);
            Assert.True(db.FileStorage.SetMetadata("target", new BsonDocument { ["answer"] = 42 }));
            Assert.Equal(42, db.FileStorage.FindById("target").Metadata["answer"].AsInt32);
            var raw = db.GetCollection("_files").FindById("target");
            Assert.Equal(42, raw[fieldName]["answer"].AsInt32);
            Assert.False(raw.ContainsKey("metadata"));
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Metadata_update_preserves_a_concurrent_uploads_content(ConnectionType mode)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = mode });
            using (var source = new MemoryStream(new byte[] { 1, 2 })) db.FileStorage.Upload("target", "old", source);
            var bytes = Enumerable.Range(0, LiteFileStream<string>.MAX_CHUNK_SIZE + 17).Select(i => (byte)i).ToArray();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var updating = new ManualResetEventSlim();
            Exception uploadFailure = null;
            Exception metadataFailure = null;
            var updated = false;
            var upload = new Thread(() =>
            {
                try
                {
                    using var source = new Issue1344Delete_Tests.PausedSource(bytes, entered, release);
                    db.FileStorage.Upload("target", "new", source);
                }
                catch (Exception error) { uploadFailure = error; }
            }) { IsBackground = true };
            var update = new Thread(() =>
            {
                try
                {
                    updating.Set();
                    updated = db.FileStorage.SetMetadata("target", new BsonDocument { ["answer"] = 42 });
                }
                catch (Exception error) { metadataFailure = error; }
            }) { IsBackground = true };
            upload.Start();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                update.Start();
                Assert.True(updating.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(SpinWait.SpinUntil(() => (update.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)));
            }
            finally
            {
                release.Set();
                Assert.True(upload.Join(TimeSpan.FromSeconds(15)));
                if ((update.ThreadState & ThreadState.Unstarted) == 0) Assert.True(update.Join(TimeSpan.FromSeconds(15)));
            }
            Assert.Null(uploadFailure);
            Assert.Null(metadataFailure);
            Assert.True(updated);
            var info = db.FileStorage.FindById("target");
            Assert.Equal("new", info.Filename);
            Assert.Equal(bytes.Length, info.Length);
            Assert.Equal(42, info.Metadata["answer"].AsInt32);
            using var downloaded = new MemoryStream();
            db.FileStorage.Download("target", downloaded);
            Assert.Equal(bytes, downloaded.ToArray());
            Assert.True(db.FileStorage.SetMetadata("target", null));
            Assert.Empty(db.FileStorage.FindById("target").Metadata.GetElements());
            Assert.False(db.FileStorage.SetMetadata("missing", new BsonDocument()));
        }

        [Theory]
        [InlineData("_files")]
        [InlineData("_chunks")]
        public void Upload_keeps_its_existing_cursor_guard(string collection)
        {
            using var db = new LiteDatabase(":memory:");
            for (var i = 0; i < 3; i++)
            {
                using var source = new MemoryStream(new byte[] { (byte)i });
                db.FileStorage.Upload(i.ToString(), "old", source);
            }
            Assert.True(db.BeginTrans());
            using (var cursor = db.GetCollection(collection).FindAll().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                using var source = new MemoryStream(new byte[] { 9 });
                Assert.Throws<LiteException>(() => db.FileStorage.Upload("new", "new", source));
                var count = 1;
                while (cursor.MoveNext()) count++;
                Assert.Equal(3, count);
            }
            Assert.True(db.Commit());
            Assert.False(db.FileStorage.Exists("new"));
            Assert.Equal(3, db.FileStorage.FindAll().Count());
        }
    }
}
