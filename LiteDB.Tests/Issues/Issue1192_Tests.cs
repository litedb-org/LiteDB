using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1192_Tests
    {
        private sealed class LegacyDictionary<TTag> : Hashtable
        {
        }

        private sealed class DiagnosticEnvelope
        {
            public int Id { get; set; }
            public string Label { get; set; }
            public Exception Failure { get; set; }
            public Action Callback { get; set; }
            public MemberInfo Member { get; set; }
        }

        [Fact]
        public void Generic_type_implementing_non_generic_IDictionary_serializes_every_entry()
        {
            var source = new LegacyDictionary<Issue1192_Tests>
            {
                ["integer"] = 42,
                ["text"] = "value",
                ["nested"] = new Hashtable { ["flag"] = true },
                ["sequence"] = new[] { 3, 5, 8 }
            };

            var mapper = new BsonMapper();
            BsonValue encoded = null;

            mapper.Invoking(x => encoded = x.Serialize(source))
                .Should().NotThrow("IDictionary support must depend on its implemented contract, not the concrete type's generic arity");

            encoded.Should().NotBeNull();
            encoded.IsDocument.Should().BeTrue();
            var document = encoded.AsDocument;
            document.Count.Should().Be(4);
            document["integer"].AsInt32.Should().Be(42);
            document["text"].AsString.Should().Be("value");
            document["nested"].AsDocument["flag"].AsBoolean.Should().BeTrue();
            document["sequence"].AsArray.Select(x => x.AsInt32).Should().Equal(3, 5, 8);
        }

        [Fact]
        public void Exception_with_delegate_and_member_metadata_preserves_ordinary_diagnostics()
        {
            var callbackInvocations = 0;
            var failure = CaptureFailure();
            failure.Data["error-code"] = 1192;
            failure.Data["context"] = "kept";

            var source = new DiagnosticEnvelope
            {
                Id = 17,
                Label = "diagnostic",
                Failure = failure,
                Callback = () => callbackInvocations++,
                Member = typeof(Issue1192_Tests).GetMethod(nameof(CallbackTarget), BindingFlags.Static | BindingFlags.NonPublic)
            };

            var mapper = new BsonMapper { SerializeNullValues = true };
            BsonValue encoded = null;

            mapper.Invoking(x => encoded = x.Serialize(source))
                .Should().NotThrow("unsupported delegate/member metadata must not make an Exception un-serializable");

            encoded.Should().NotBeNull();
            encoded.IsDocument.Should().BeTrue();
            var document = encoded.AsDocument;
            document["_id"].AsInt32.Should().Be(17);
            document["Label"].AsString.Should().Be("diagnostic");
            document["Failure"].IsDocument.Should().BeTrue("returning null for the entire Exception would hide its useful diagnostics");
            document["Failure"].AsDocument["Message"].AsString.Should().Be("reported failure");
            document["Failure"].AsDocument["Data"].AsDocument["error-code"].AsInt32.Should().Be(1192);
            document["Failure"].AsDocument["Data"].AsDocument["context"].AsString.Should().Be("kept");
            callbackInvocations.Should().Be(0, "serialization must inspect neither by invoking user delegates nor by dropping adjacent data");
        }

        private static Exception CaptureFailure()
        {
            try
            {
                throw new InvalidOperationException("reported failure");
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static void CallbackTarget()
        {
        }
    }
}
