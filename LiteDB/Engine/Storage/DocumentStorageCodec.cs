using System;
using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>The private DataBlock format boundary; public BSON never uses this codec.</summary>
    internal static class DocumentStorageCodec
    {
        internal sealed class WritePlan
        {
            internal BsonDocument Document { get; }
            internal byte[] Payload { get; }
            internal int EncodedLength { get; }

            internal WritePlan(BsonDocument document, int length, byte[] payload = null)
            {
                Document = document;
                Payload = payload;
                EncodedLength = length;
            }
        }

        internal static WritePlan PrepareWrite(BsonDocument document, Snapshot snapshot)
        {
            var length = document.GetBytesCount(true);
            if (length > MAX_DOCUMENT_SIZE) throw new LiteException(0, "Document size exceed {0} limit", MAX_DOCUMENT_SIZE);
            if (snapshot.ShouldPrepareCompact(document, length))
            {
                using (var encoder = new CompactDocumentWriter(snapshot.Schemas, snapshot.ObserveShape))
                {
                    byte[] payload;
                    try { payload = encoder.Encode(document); }
                    catch (NotSupportedException) { payload = null; }
                    catch (System.Text.EncoderFallbackException) { payload = null; }
                    if (payload != null && payload.Length + 8 < length)
                    {
                        snapshot.ObserveCompactResult(true);
                        snapshot.CommitSchemas(encoder.Pending);
                        return new WritePlan(document, payload.Length, payload);
                    }
                    snapshot.ObserveCompactResult(false);
                }
            }
            return new WritePlan(document, length);
        }

        internal static void Write(WritePlan plan, BufferWriter writer)
        {
            if (plan.Payload == null) writer.WriteDocument(plan.Document, false);
            else writer.Write(plan.Payload, 0, plan.Payload.Length);
        }

        internal static Result<BsonDocument> Read(BufferReader reader, HashSet<string> fields = null,
            Func<SchemaCatalog> catalog = null, bool utcDate = false, string collection = null, PageAddress address = default)
        {
            var discriminator = reader.ReadInt32();
            if (discriminator >= 5) return reader.ReadDocument(fields, discriminator);
            CompactDocumentReader decoder = null;
            try
            {
                if (discriminator != CompactDocumentWriter.Magic || reader.ReadByte() != 1 || reader.ReadByte() != 0)
                    throw new InvalidDataException("Invalid compact magic/version/flags.");
                var length = reader.ReadInt32();
                if (length < 16 || length > MAX_DOCUMENT_SIZE) throw new InvalidDataException("Invalid compact document length.");
                decoder = new CompactDocumentReader(reader.ReadBytes(length - 10), catalog, utcDate);
                return decoder.Decode(fields);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is LiteException || ex is IOException || ex is ArgumentException || ex is InvalidOperationException || ex is InvalidCastException || ex is NullReferenceException || ex is IndexOutOfRangeException)
            {
                throw new LiteException(LiteException.CORRUPT_DOCUMENT, ex, "Corrupt compact document in collection '{0}', schema {1}, address {2}: {3}", collection, decoder?.SchemaId ?? 0, address, ex.Message);
            }
            finally { decoder?.Dispose(); }
        }
    }
}
