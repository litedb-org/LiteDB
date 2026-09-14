using System.IO;
using System.Linq;
using LiteDB.Engine;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2777_Tests
    {
        [Fact]
        public void Identical_plaintext_pages_do_not_disclose_repeated_ciphertext_blocks()
        {
            using var backing = new MemoryStream();
            using var crypto = new AesStream("pattern-regression-password", backing);
            var page = Enumerable.Repeat((byte)0x5a, 8192).ToArray();
            for (var i = 0; i < 4; i++) crypto.Write(page, 0, page.Length);
            crypto.Flush();
            var ciphertext = backing.ToArray();
            // First establish actual recoverability, so random output cannot satisfy confidentiality.
            crypto.Position = 0;
            for (var i = 0; i < 4; i++)
            {
                var recovered = new byte[8192];
                crypto.Read(recovered, 0, recovered.Length).Should().Be(recovered.Length);
                recovered.Should().Equal(page);
            }
            // Skip the clear encryption header. Compare interior blocks at different page positions.
            var first = ciphertext.Skip(8192 + 1024).Take(16).ToArray();
            for (var i = 1; i < 4; i++)
            {
                ciphertext.Skip(8192 + i * 8192 + 1024).Take(16).Should().NotEqual(first,
                    "the same plaintext at different page positions must not reveal its equality");
            }
        }
    }
}
