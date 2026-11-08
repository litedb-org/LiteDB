#if NET8_0_OR_GREATER
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class TransactionHandleMappedLifetime_Tests
    {
        [OneFileFact]
        public void Close_drains_mapped_call_and_cursor_before_participant_release() =>
            TransactionHandleCloseMaintenance_Tests.Drain(2);

        [OneFileFact]
        public void Live_database_retains_mapped_handle_until_close() =>
            TransactionHandleCloseMaintenance_Tests.Retained(2);
    }
}
#endif
