using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Tests.Utils;
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
        public async Task Storage_Upload_Download()
        {
            await using (var db = DatabaseFactory.Create())
            {
                var fs = db.GetStorage<int>("_files", "_chunks");

                var small = await fs.UploadAsync(10, "photo_small.png", new MemoryStream(_smallFile));
                var big = await fs.UploadAsync(100, "photo_big.png", new MemoryStream(_bigFile));

                _smallFile.Length.Should().Be((int)small.Length);
                _bigFile.Length.Should().Be((int)big.Length);

                var f0 = await FirstAsync(fs.FindAsync(x => x.Filename == "photo_small.png"));
                var f1 = await FirstAsync(fs.FindAsync(x => x.Filename == "photo_big.png"));

                await using (var reader0 = await f0.OpenReadAsync())
                {
                    var hash = await this.HashFileAsync(reader0);
                    hash.Should().Be(_smallHash);
                }

                await using (var reader1 = await f1.OpenReadAsync())
                {
                    var hash = await this.HashFileAsync(reader1);
                    hash.Should().Be(_bigHash);
                }

                var repl = await fs.UploadAsync(10, "new_photo.jpg", new MemoryStream(_bigFile));

                (await fs.ExistsAsync(10)).Should().BeTrue();

                var nrepl = await fs.FindByIdAsync(10);

                nrepl.Chunks.Should().Be(repl.Chunks);

                await fs.SetMetadataAsync(100, new BsonDocument { ["x"] = 100, ["y"] = 99 });

                var md = await FirstAsync(fs.FindAsync(x => x.Metadata["x"] == 100));

                md.Metadata["y"].AsInt32.Should().Be(99);
            }
        }

        private static async Task<LiteFileInfo<int>> FirstAsync(IAsyncEnumerable<LiteFileInfo<int>> source)
        {
            await foreach (var item in source)
            {
                return item;
            }

            return null;
        }

        private async Task<string> HashFileAsync(Stream stream)
        {
            await using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return this.HashFile(memory.ToArray());
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
