using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedModeRetry_Tests
    {
        [Theory]
        [InlineData(unchecked((int)0x80070020), true)]
        [InlineData(unchecked((int)0x80070021), true)]
        [InlineData(unchecked((int)0x80070005), false)]
        [InlineData(unchecked((int)0x80070050), false)]
        [InlineData(unchecked((int)0x80131620), false)]
        public void Wrapped_native_errors_preserve_retry_classification_and_bound(int code, bool retryable)
        {
            var inner = new IOException("injected native failure", code);
            var wrapped = new DatabaseAdmissionException("database.db", inner);
            wrapped.HResult.Should().Be(code);
            var attempts = 0;
            Action run = () => SharedCoordinationFile.RetrySharingViolation<int>(() =>
            {
                attempts++;
                throw wrapped;
            });
            run.Should().Throw<DatabaseAdmissionException>().Which.Should().BeSameAs(wrapped);
            attempts.Should().Be(retryable && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 5 : 1);
        }

#if NET8_0_OR_GREATER
        [MappedFact]
        public void Transient_guard_failure_retries_attachment_without_mutating_database()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserved" });
            var original = File.ReadAllBytes(file);
            var attempts = 0;
            SharedCoordinationFile.CreationStage = (path, stage) =>
            {
                if (stage == "mode-locking" && ++attempts < 3)
                    throw new IOException("injected sharing violation", unchecked((int)0x80070020));
            };
            try
            {
                Action attach = () =>
                {
                    using var page = SharedCoordinationFile.RetrySharingViolation(() => SharedCoordinationPage.Open(file));
                };
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    attach();
                    attempts.Should().Be(3);
                }
                else
                {
                    attach.Should().Throw<DatabaseAdmissionException>().Which.HResult.Should().Be(unchecked((int)0x80070020));
                    attempts.Should().Be(1, "native Windows retries must not run on other platforms");
                }
            }
            finally { SharedCoordinationFile.CreationStage = null; }
            File.ReadAllBytes(file).Should().Equal(original);
            using (var page = SharedCoordinationPage.Open(file)) { }
            SharedCoordinationPage.TryRetire(file);
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("preserved");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Unsupported_control_names_report_missing_admission_without_read_only_claim(bool readOnly)
        {
            var filename = Path.Combine(Path.GetTempPath(), new string('x', 240));
            Action attach = () => { using var page = SharedCoordinationPage.Open(filename, readOnly: readOnly); };
            attach.Should().Throw<IOException>().WithMessage("Mapped attachment requires a mode admission lease.");
        }
#endif
    }
}
