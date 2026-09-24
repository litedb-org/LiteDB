using System.Linq;

namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        internal void CheckVectorVersion(BsonValue value)
        {
            if (_header.FileVersion < HeaderPage.VECTOR_FILE_VERSION && ContainsVector(value)) RequireVectorVersion();
        }

        /// <summary>Promotion is durable and monotonic, even if this transaction rolls back.</summary>
        internal void RequireVectorVersion() => RequireFileVersion(HeaderPage.VECTOR_FILE_VERSION);

        internal void RequireFileVersion(byte requiredVersion)
        {
            if (_header.FileVersion >= requiredVersion) return;
            lock (_header)
            {
                if (_header.FileVersion >= requiredVersion) return;
                _disk.PromoteFileFormat(requiredVersion);
                _header.EnsureVersion(requiredVersion);
            }
        }

        private static bool ContainsVector(BsonValue value)
        {
            if (value.IsVector) return true;
            if (value.IsDocument) return value.AsDocument.Values.Any(ContainsVector);
            return value.IsArray && value.AsArray.Any(ContainsVector);
        }
    }
}
