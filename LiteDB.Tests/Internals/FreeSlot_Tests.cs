using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class FreeSlots_Tests
    {
        /// <summary>
        /// FreeBytes ranges on page slot for free list page
        /// 90% - 100% = 0 (7344 - 8160)
        /// 75% -  90% = 1 (6120 - 7343)
        /// 60% -  75% = 2 (4896 - 6119)
        /// 30% -  60% = 3 (2448 - 4895)
        ///  0% -  30% = 4 (0000 - 2447)
        /// </summary>
        [Fact]
        public void FreeIndexSlot_Ranges()
        {
            DataPage.FreeIndexSlot(0).Should().Be(4);
            DataPage.FreeIndexSlot(200).Should().Be(4);
            DataPage.FreeIndexSlot(2447).Should().Be(4);

            DataPage.FreeIndexSlot(2448).Should().Be(3);
            DataPage.FreeIndexSlot(4895).Should().Be(3);

            DataPage.FreeIndexSlot(4896).Should().Be(2);
            DataPage.FreeIndexSlot(6119).Should().Be(2);

            DataPage.FreeIndexSlot(6120).Should().Be(1);
            DataPage.FreeIndexSlot(7343).Should().Be(1);

            DataPage.FreeIndexSlot(7344).Should().Be(0);
            DataPage.FreeIndexSlot(8160).Should().Be(0);
        }

        [Fact]
        public void MinimumIndexSlot_Ranges()
        {
            DataPage.GetMinimumIndexSlot(1).Should().Be(3);
            DataPage.GetMinimumIndexSlot(200).Should().Be(3);
            DataPage.GetMinimumIndexSlot(2447).Should().Be(3);

            DataPage.GetMinimumIndexSlot(2448).Should().Be(2);
            DataPage.GetMinimumIndexSlot(4895).Should().Be(2);

            DataPage.GetMinimumIndexSlot(4896).Should().Be(1);
            DataPage.GetMinimumIndexSlot(6119).Should().Be(1);

            DataPage.GetMinimumIndexSlot(6120).Should().Be(0);
            DataPage.GetMinimumIndexSlot(7343).Should().Be(0);

            // need new page (returns -1)
            DataPage.GetMinimumIndexSlot(8160).Should().Be(-1);
            DataPage.GetMinimumIndexSlot(7344).Should().Be(-1);
        }
    }
}