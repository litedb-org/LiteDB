using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedModeAdmissionReview_Tests
    {
        [Fact]
        public void Unavailable_direct_guard_reports_public_exception_and_preserves_data()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var data = File.ReadAllBytes(file.Filename);
            var path = file.Filename + "-shared-mode";
            File.Delete(path);
            Directory.CreateDirectory(path);
            try
            {
                Action open = () => { using var db = new LiteDatabase(file.Filename); };
                open.Should().Throw<SharedModeConflictException>()
                    .WithMessage("*directory permits*").WithInnerException<UnauthorizedAccessException>();
                File.ReadAllBytes(file.Filename).Should().Equal(data);
            }
            finally { Directory.Delete(path); }
            using var cold = new LiteDatabase(file.Filename);
            cold.GetCollection("rows").Count().Should().Be(1);
        }

        [Fact]
        public void Direct_requires_file_locking_even_without_shared_artifacts()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var file = new TempFile();
            AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var before);
            AppContext.SetSwitch("System.IO.DisableFileLocking", true);
            try
            {
                Action open = () => { using var db = new LiteDatabase(file.Filename); };
                open.Should().Throw<PlatformNotSupportedException>().WithMessage("*Direct*");
                File.Exists(file.Filename).Should().BeFalse();
                File.Exists(file.Filename + "-shared-mode").Should().BeFalse();
            }
            finally { AppContext.SetSwitch("System.IO.DisableFileLocking", before); }
            using var cold = new LiteDatabase(file.Filename);
            cold.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        }

    }
}
