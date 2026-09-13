using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class Storage_Tests
    {
        private readonly Random _rnd = new Random();
        private readonly byte[] _smallFile;
        private readonly byte[] _bigFile;
        private readonly string _smallHash;
        private readonly string _bigHash;

        public Storage_Tests()
        {
            _smallFile = new byte[_rnd.Next(100000, 200000)];
            _bigFile = new byte[_rnd.Next(400000, 600000)];

            _rnd.NextBytes(_smallFile);
            _rnd.NextBytes(_bigFile);

            _smallHash = this.HashFile(_smallFile);
            _bigHash = this.HashFile(_bigFile);
        }

        [Fact]
        public void Storage_Upload_Download()
        {
            using (var f = new TempFile())
            using (var db = new LiteDatabase(f.Filename))
                //using (var db = new LiteDatabase(@"c:\temp\file.db"))
            {
                var fs = db.GetStorage<int>("_files", "_chunks");

                var small = fs.Upload(10, "photo_small.png", new MemoryStream(_smallFile));
                var big = fs.Upload(100, "photo_big.png", new MemoryStream(_bigFile));

                _smallFile.Length.Should().Be((int) small.Length);
                _bigFile.Length.Should().Be((int) big.Length);

                var f0 = fs.Find(x => x.Filename == "photo_small.png").First();
                var f1 = fs.Find(x => x.Filename == "photo_big.png").First();

                this.HashFile(f0.OpenRead()).Should().Be(_smallHash);
                this.HashFile(f1.OpenRead()).Should().Be(_bigHash);

                // now replace small content with big-content
                var repl = fs.Upload(10, "new_photo.jpg", new MemoryStream(_bigFile));

                fs.Exists(10).Should().BeTrue();

                var nrepl = fs.FindById(10);

                nrepl.Chunks.Should().Be(repl.Chunks);

                // update metadata
                fs.SetMetadata(100, new BsonDocument {["x"] = 100, ["y"] = 99});

                // find using metadata
                var md = fs.Find(x => x.Metadata["x"] == 100).FirstOrDefault();

                md.Metadata["y"].AsInt32.Should().Be(99);
            }
        }

        private string HashFile(Stream stream)
        {
            var m = new MemoryStream();
            stream.CopyTo(m);
            return this.HashFile(m.ToArray());
        }

        private string HashFile(byte[] input)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(input);
                return Convert.ToBase64String(bytes);
            }
        }
    }
}