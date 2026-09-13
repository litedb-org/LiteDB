using System;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue996_Tests
    {
        [Fact]
        public void LiteException_With_Explicit_Empty_Args_Preserves_Message_With_Braces()
        {
            var ex = Record.Exception(() => new LiteException(0, "Field {0} is invalid", Array.Empty<object>()));

            Assert.Null(ex);
            Assert.Equal("Field {0} is invalid", new LiteException(0, "Field {0} is invalid", Array.Empty<object>()).Message);
        }

        [Fact]
        public void LiteException_With_Inner_Exception_And_Empty_Args_Preserves_Message_With_Braces()
        {
            var inner = new InvalidOperationException("inner");

            var ex = Record.Exception(() => new LiteException(0, inner, "Field {0} is invalid"));

            Assert.Null(ex);
            var liteException = new LiteException(0, inner, "Field {0} is invalid");
            Assert.Same(inner, liteException.InnerException);
            Assert.Equal("Field {0} is invalid", liteException.Message);
        }

        [Fact]
        public void LiteException_With_Args_Still_Formats_Message()
        {
            var ex = new LiteException(0, "Field {0} is invalid", "Name");

            Assert.Equal("Field Name is invalid", ex.Message);
        }
    }
}
