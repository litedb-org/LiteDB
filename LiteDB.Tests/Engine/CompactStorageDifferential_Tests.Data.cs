using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Document generation and structural comparison for the legacy/compact differential.
    /// </summary>
    public partial class CompactStorageDifferential_Tests
    {
        #region Deterministic generator

        private const string LongA = "CustomerIdentifierNumber";
        private const string LongB = "CustomerDisplayNameValue";
        private const string LongC = "OrderHeaderInformation";
        private const string LongD = "OrderLineItemCollection";
        private const string LongE = "MeasurementSeriesValues";
        private const string LongF = "OptionalMetadataPayload";

        internal const int EdgeScalarCases = 44;

        internal static BsonValue EdgeScalar(Random r) => EdgeScalar(r.Next(EdgeScalarCases), r);

        internal static BsonValue EdgeScalar(int kind, Random r)
        {
            switch (kind)
            {
                case 0: return BsonValue.Null;
                case 1: return BsonValue.MinValue;
                case 2: return BsonValue.MaxValue;
                case 3: return int.MinValue;
                case 4: return int.MaxValue;
                case 5: return long.MinValue;
                case 6: return long.MaxValue;
                case 7: return double.NaN;
                case 8: return double.PositiveInfinity;
                case 9: return double.NegativeInfinity;
                case 10: return -0.0;
                case 11: return 123456.789; // tiny doubles are covered by CompactStorageQueryParity_Tests
                case 12: return double.MaxValue;
                case 13: return 1.00m;
                case 14: return decimal.MaxValue;
                case 15: return decimal.MinValue;
                case 16: return 0.0000000000000000000000000001m;
                case 17: return -0.000m;
                case 18: return "";
                case 19: return "héllo 世界 😀";
                case 20: return "embedded\0null";
                case 21: return new string('x', 3000 + r.Next(50));
                case 22: return new byte[0];
                case 23: return Bytes(r, 17);
                case 24: return Guid.Empty;
                case 25: return new Guid(Bytes(r, 16));
                case 26: return ObjectId.Empty;
                case 27: return new ObjectId(Bytes(r, 12));
                case 28: return true;
                case 29: return false;
                case 30: return DateTime.MinValue;
                case 31: return DateTime.MaxValue;
                case 32: return new DateTime(2026, 9, 1, 2, 3, 4, 567, DateTimeKind.Utc);
                case 33: return new DateTime(2026, 3, 29, 2, 30, 0, DateTimeKind.Local);
                case 34: return new DateTime(1901, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
                case 35: return new DateTime(638000000000000123L, DateTimeKind.Utc); // sub-millisecond ticks
                case 36: return DateTime.MaxValue.AddTicks(-1);
                case 37: return DateTime.MinValue.AddTicks(1);
                case 38: return BsonValue.UnixEpoch;
                case 39: return new BsonVector(new[] { 1f, -0f, float.NaN, float.PositiveInfinity, float.Epsilon });
                case 40: return new BsonVector(new[] { (float)r.NextDouble(), 2f });
                case 41: return r.Next();
                case 42: return (long)r.Next() * 1000L;
                default: return r.NextDouble();
            }
        }

        private static byte[] Bytes(Random r, int count)
        {
            var bytes = new byte[count];
            r.NextBytes(bytes);
            return bytes;
        }

        private static BsonValue Id(int i)
        {
            switch (i % 6)
            {
                case 0: return i;
                case 1: return (long)i << 33;
                case 2: return "id-" + i.ToString("D6");
                case 3: return new Guid(i, 7, 9, 1, 2, 3, 4, 5, 6, 7, 8);
                case 4: return new ObjectId(i, 3, 5, 7);
                default: return (decimal)i + 0.5m;
            }
        }

        internal static BsonDocument Shape(Random r, BsonValue id, int shape)
        {
            var doc = new BsonDocument { ["_id"] = id };
            switch (shape)
            {
                case 0: // flat scalar record, value types change between documents
                case 1: // same names, optional fields dropped (ordered subsequence)
                    for (var f = 0; f < 12; f++)
                    {
                        if (shape == 1 && r.Next(3) == 0) continue;
                        doc["ScalarPropertyNumber" + f] = EdgeScalar(r);
                    }
                    break;
                case 2: // nested documents and arrays of documents
                    doc[LongA] = r.Next(100);
                    doc[LongB] = "name " + r.Next(5);
                    doc[LongC] = new BsonDocument
                    {
                        ["InnerIdentifier"] = r.Next(10),
                        ["InnerDescription"] = EdgeScalar(r),
                        ["_id"] = r.Next(3),
                        ["InnerNestedDocument"] = new BsonDocument { ["DeepValue"] = r.Next(4), ["DeepOther"] = EdgeScalar(r) }
                    };
                    var lines = new BsonArray();
                    for (var l = 0; l < r.Next(0, 6); l++)
                        lines.Add(new BsonDocument { ["ProductCode"] = "p" + r.Next(4), ["Quantity"] = r.Next(1, 9), ["UnitPrice"] = (decimal)r.Next(100) / 7m });
                    doc[LongD] = lines;
                    doc["Tags"] = new BsonArray(Enumerable.Range(0, r.Next(4)).Select(t => new BsonValue("t" + r.Next(5))));
                    doc["Matrix"] = new BsonArray { new BsonArray { 1, 2 }, new BsonArray(), new BsonArray { new BsonArray { 3 } } };
                    break;
                case 3: // large arrays: BSON pays for numeric index keys
                    doc[LongA] = r.Next(100);
                    doc[LongE] = new BsonArray(Enumerable.Range(0, 1500 + r.Next(10)).Select(v => new BsonValue(v * 0.25)));
                    doc["Labels"] = new BsonArray(Enumerable.Range(0, 200).Select(v => new BsonValue("L" + (v % 7))));
                    break;
                case 4: // deep nesting (below and above the compact nesting limit)
                    {
                        var depth = r.Next(2) == 0 ? 20 : 70;
                        BsonValue inner = new BsonDocument { ["LeafValueField"] = r.Next(9), ["_id"] = depth };
                        for (var d = 0; d < depth; d++)
                            inner = d % 3 == 0 ? (BsonValue)new BsonArray { inner, d } : new BsonDocument { ["NestedLevelField"] = inner, ["LevelNumber"] = d };
                        doc["DeepStructureRoot"] = inner;
                        doc[LongA] = r.Next(100);
                    }
                    break;
                case 5: // unicode, long and case-variant names
                    {
                        var upper = r.Next(2) == 0;
                        doc[upper ? "FIELDNAMEWITHCASE" : "FieldNameWithCase"] = r.Next(100);
                        doc["名前フィールド"] = "unicode name";
                        doc["ÜnïcödéFeld"] = EdgeScalar(r);
                        doc["Emoji😀Field"] = EdgeScalar(r);
                        doc[new string('n', 300)] = r.Next(10);
                        doc[new string('é', 300)] = r.Next(10); // 600 UTF-8 bytes: not admissible in a schema
                        doc[LongA] = r.Next(100);
                        doc[LongB] = EdgeScalar(r);
                    }
                    break;
                case 6: // one field per BsonType
                    doc["NullFieldValue"] = BsonValue.Null;
                    doc["MinKeyFieldValue"] = BsonValue.MinValue;
                    doc["MaxKeyFieldValue"] = BsonValue.MaxValue;
                    doc["Int32FieldValue"] = r.Next();
                    doc["Int64FieldValue"] = long.MinValue + r.Next();
                    doc["DoubleFieldValue"] = -0.0;
                    doc["DecimalFieldValue"] = 12.3400m;
                    doc["StringFieldValue"] = "s" + r.Next();
                    doc["BinaryFieldValue"] = Bytes(r, r.Next(3));
                    doc["GuidFieldValue"] = new Guid(Bytes(r, 16));
                    doc["ObjectIdFieldValue"] = new ObjectId(Bytes(r, 12));
                    doc["BooleanFieldValue"] = r.Next(2) == 0;
                    doc["DateTimeFieldValue"] = new DateTime(2020, 2, 29, 23, 59, 59, 999, DateTimeKind.Local);
                    doc["VectorFieldValue"] = new BsonVector(new[] { 0.5f, (float)r.Next(10) });
                    doc["DocumentFieldValue"] = new BsonDocument();
                    doc["ArrayFieldValue"] = new BsonArray();
                    break;
                case 7: // multi-page document
                    doc[LongA] = r.Next(100);
                    doc[LongF] = Bytes(r, 30000 + r.Next(100));
                    doc["ArrayOfBinaries"] = new BsonArray(Enumerable.Range(0, 40).Select(_ => new BsonValue(Bytes(r, 300))));
                    break;
                default: // empties and nulls versus missing
                    doc["EmptyDocumentField"] = new BsonDocument();
                    doc["EmptyArrayField"] = new BsonArray();
                    doc["ExplicitNullField"] = BsonValue.Null;
                    doc["ArrayOfEmpties"] = new BsonArray { new BsonDocument(), new BsonArray(), BsonValue.Null, "" };
                    if (r.Next(2) == 0) doc[LongF] = r.Next();
                    doc[LongA] = r.Next(100);
                    break;
            }
            return doc;
        }

        internal static List<BsonDocument> Generate(int seed, int count, int firstId = 0)
        {
            var r = new Random(seed);
            // Every shape appears in consecutive pairs so schema admission can happen.
            return Enumerable.Range(firstId, count).Select(i => Shape(r, Id(i), (i / 2) % 9)).ToList();
        }

        #endregion

        #region Strict comparison

        internal static void Compare(BsonValue expected, BsonValue actual, string path, List<string> diffs)
        {
            if (diffs.Count > 20) return;
            if (expected.Type != actual.Type)
            {
                diffs.Add($"{path}: type {expected.Type} != {actual.Type}");
                return;
            }
            switch (expected.Type)
            {
                case BsonType.Document:
                    var e = expected.AsDocument.GetElements().ToArray();
                    var a = actual.AsDocument.GetElements().ToArray();
                    var ek = string.Join(",", e.Select(x => x.Key));
                    var ak = string.Join(",", a.Select(x => x.Key));
                    if (!string.Equals(ek, ak, StringComparison.Ordinal))
                    {
                        diffs.Add($"{path}: keys [{Trim(ek)}] != [{Trim(ak)}]");
                        return;
                    }
                    for (var i = 0; i < e.Length; i++) Compare(e[i].Value, a[i].Value, path + "." + e[i].Key, diffs);
                    break;
                case BsonType.Array:
                    var ea = expected.AsArray;
                    var aa = actual.AsArray;
                    if (ea.Count != aa.Count)
                    {
                        diffs.Add($"{path}: array length {ea.Count} != {aa.Count}");
                        return;
                    }
                    for (var i = 0; i < ea.Count; i++) Compare(ea[i], aa[i], path + "[" + i + "]", diffs);
                    break;
                case BsonType.Double:
                    if (BitConverter.DoubleToInt64Bits(expected.AsDouble) != BitConverter.DoubleToInt64Bits(actual.AsDouble))
                        diffs.Add($"{path}: double bits {expected.AsDouble:R} != {actual.AsDouble:R}");
                    break;
                case BsonType.Decimal:
                    if (!decimal.GetBits(expected.AsDecimal).SequenceEqual(decimal.GetBits(actual.AsDecimal)))
                        diffs.Add($"{path}: decimal {expected.AsDecimal} != {actual.AsDecimal}");
                    break;
                case BsonType.DateTime:
                    if (expected.AsDateTime.Ticks != actual.AsDateTime.Ticks || expected.AsDateTime.Kind != actual.AsDateTime.Kind)
                        diffs.Add($"{path}: date {expected.AsDateTime:o}/{expected.AsDateTime.Kind} != {actual.AsDateTime:o}/{actual.AsDateTime.Kind}");
                    break;
                case BsonType.Binary:
                    if (!expected.AsBinary.SequenceEqual(actual.AsBinary)) diffs.Add($"{path}: binary differs");
                    break;
                case BsonType.Vector:
                    var ev = expected.AsVector.Select(f => BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
                    var av = actual.AsVector.Select(f => BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
                    if (!ev.SequenceEqual(av)) diffs.Add($"{path}: vector differs");
                    break;
                case BsonType.String:
                    if (!string.Equals(expected.AsString, actual.AsString, StringComparison.Ordinal)) diffs.Add($"{path}: string differs");
                    break;
                default:
                    if (!expected.Equals(actual)) diffs.Add($"{path}: {expected} != {actual}");
                    break;
            }
        }

        private static string Trim(string s) => s.Length > 200 ? s.Substring(0, 200) + "..." : s;

        private static string Describe(BsonValue value)
        {
            if (!value.IsDocument) return Trim(value.ToString());
            var doc = value.AsDocument;
            return Trim(string.Join(", ", doc.GetElements().Select(e => e.Key + "=" + e.Value.Type + ":" + (e.Value.IsString ? Trim(e.Value.AsString) : e.Value.ToString()))));
        }

        internal static List<string> CompareLists(IReadOnlyList<BsonValue> expected, IReadOnlyList<BsonValue> actual, string label, bool ordered)
        {
            var diffs = new List<string>();
            if (expected.Count != actual.Count)
            {
                diffs.Add($"{label}: count {expected.Count} != {actual.Count}");
                var ek = new HashSet<string>(expected.Select(SortKey));
                var ak = new HashSet<string>(actual.Select(SortKey));
                foreach (var missing in expected.Where(x => !ak.Contains(SortKey(x))).Take(5)) diffs.Add($"{label}: only in Legacy: {Describe(missing)}");
                foreach (var extra in actual.Where(x => !ek.Contains(SortKey(x))).Take(5)) diffs.Add($"{label}: only in candidate: {Describe(extra)}");
                return diffs;
            }
            var e = ordered ? expected : expected.OrderBy(SortKey, StringComparer.Ordinal).ToList();
            var a = ordered ? actual : actual.OrderBy(SortKey, StringComparer.Ordinal).ToList();
            for (var i = 0; i < e.Count; i++) Compare(e[i], a[i], $"{label}[{i}]", diffs);
            return diffs;
        }

        private static string SortKey(BsonValue value) => Convert.ToBase64String(BsonSerializer.Serialize(new BsonDocument { ["v"] = value }));

        internal static List<BsonValue> Sql(LiteDatabase db, string sql)
        {
            var result = new List<BsonValue>();
            using (var reader = db.Execute(sql))
            {
                while (reader.Read()) result.Add(reader.Current);
            }
            return result;
        }

        #endregion

    }
}
