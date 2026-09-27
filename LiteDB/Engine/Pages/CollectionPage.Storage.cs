namespace LiteDB.Engine
{
    internal partial class CollectionPage
    {
        private const uint StorageMagic = 0x3143444C; // LDC1
        private const int StorageOffset = 52;
        private const uint VectorSectionMagic = 0x3156444C; // LDV1
        private const int VectorSectionOffset = 68;

        /// <summary>
        /// Collection pages written before the vector-section marker carry a vector section only
        /// when the file already stored vector metadata (v9) while they were written. v8 files never
        /// do, nor do the unmarked pages a v8 file keeps through checksum conversion. A conversion
        /// recorded before the legacy version byte existed keeps the previous interpretation.
        /// </summary>
        internal static bool UnmarkedPagesHaveVectorSection(byte fileVersion, byte legacyVersion) =>
            fileVersion >= HeaderPage.VECTOR_FILE_VERSION && legacyVersion != HeaderPage.FILE_VERSION;

        private bool HasVectorSection(bool unmarkedVectorSection) =>
            Buffer.ReadUInt32(VectorSectionOffset) == VectorSectionMagic || unmarkedVectorSection;

        // v5 cleared the whole reserved area of every page it allocated or freed.
        private void MarkVectorSection() => Buffer.Write(VectorSectionMagic, VectorSectionOffset);
        internal uint SchemaRoot { get; private set; } = uint.MaxValue;
        internal uint SchemaTail { get; private set; } = uint.MaxValue;
        internal uint SchemaCount { get; private set; }

        private void ReadStorageMetadata()
        {
            // Legacy reserved bytes have no meaning unless the complete marker matches.
            if (Buffer.ReadUInt32(StorageOffset) != StorageMagic) return;
            SchemaRoot = Buffer.ReadUInt32(StorageOffset + 4);
            SchemaTail = Buffer.ReadUInt32(StorageOffset + 8);
            SchemaCount = Buffer.ReadUInt32(StorageOffset + 12);
            if (SchemaCount > StorageSchema.MaxSchemas || SchemaCount == 0 ||
                SchemaRoot == uint.MaxValue || SchemaTail == uint.MaxValue)
                throw new LiteException(LiteException.CORRUPT_DOCUMENT,
                    "Invalid schema metadata for collection page {0}, schema count {1}; document address unavailable.", PageID, SchemaCount);
        }

        internal void SetSchemaMetadata(uint root, uint tail, uint count)
        {
            SchemaRoot = root;
            SchemaTail = tail;
            SchemaCount = count;
            Buffer.Write(StorageMagic, StorageOffset);
            Buffer.Write(root, StorageOffset + 4);
            Buffer.Write(tail, StorageOffset + 8);
            Buffer.Write(count, StorageOffset + 12);
            IsDirty = true;
        }
    }
}
