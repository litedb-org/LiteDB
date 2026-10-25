namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        private bool _retainedForCursor;

        internal void RetainForCursor()
        {
            // A read cursor must continue at its original WAL version after the
            // transaction creates a separate writable snapshot for the collection.
            _retainedForCursor = true;
        }
    }
}
