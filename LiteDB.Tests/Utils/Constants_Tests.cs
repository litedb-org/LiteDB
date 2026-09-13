using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Utils
{
    public class Constants_Tests
    {
        [Fact]
        public void Ensure_Formats_Message()
        {
            var exception = Assert.Throws<LiteException>(() =>
                Constants.ENSURE(false, "value {0} / {1}", 7, "x"));

            exception.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            exception.Message.Should().Be("value 7 / x");
        }
    }
}
