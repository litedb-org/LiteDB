#if NET8_0_OR_GREATER
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// Experimental coordinator wire format: each message is a little-endian
    /// length followed by one BSON document. Requests carry "op"; replies carry
    /// "ok" and either "r" or the error fields "code", "type" and "message".
    /// </summary>
    internal static class CoordinatorProtocol
    {
        private const int MAX_MESSAGE_BYTES = 64 * 1024 * 1024;

        internal static string PipeName(string filename) =>
            "litedb-coord-" + SharedMutexNameFactory.CreateUsingSha1(filename);

        internal static string MutexName(string filename) =>
            "coord-" + SharedMutexNameFactory.CreateUsingSha1(filename);

        internal static void Write(Stream stream, BsonDocument message)
        {
            var body = BsonSerializer.Serialize(message);
            var frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        /// <summary>Read one message. A closed pipe surfaces as <see cref="EndOfStreamException"/>.</summary>
        internal static BsonDocument Read(Stream stream, TimeSpan? timeout = null)
        {
            using var cancel = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
            var token = cancel?.Token ?? CancellationToken.None;
            var header = new byte[4];
            ReadExactly(stream, header, token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MAX_MESSAGE_BYTES)
                throw new IOException($"Invalid coordinator message length {length}.");
            var body = new byte[length];
            ReadExactly(stream, body, token);
            return BsonSerializer.Deserialize(body);
        }

        private static void ReadExactly(Stream stream, byte[] buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                int read;
                try { read = stream.ReadAsync(buffer, offset, buffer.Length - offset, token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { throw new TimeoutException("The coordinator peer did not answer in time."); }
                if (read == 0) throw new EndOfStreamException("The coordinator connection was closed.");
                offset += read;
            }
        }

        internal static BsonDocument Ok(BsonValue result = null) =>
            new BsonDocument { ["ok"] = true, ["r"] = result ?? BsonValue.Null };

        internal static BsonDocument Error(Exception ex) => new BsonDocument
        {
            ["ok"] = false,
            ["code"] = ex is LiteException lite ? lite.ErrorCode : 0,
            ["type"] = ex.GetType().FullName,
            ["message"] = ex.Message
        };

        /// <summary>Return the reply's result or rethrow the coordinator's error.</summary>
        internal static BsonValue Result(BsonDocument reply)
        {
            if (reply["ok"].AsBoolean) return reply["r"];
            var type = reply["type"].AsString;
            var message = reply["message"].AsString;
            if (type == typeof(LiteException).FullName || type.StartsWith("LiteDB.", StringComparison.Ordinal))
                throw new LiteException(reply["code"].AsInt32, message);
            if (type == typeof(ArgumentException).FullName || type == typeof(ArgumentNullException).FullName)
                throw new ArgumentException(message);
            if (type == typeof(NotSupportedException).FullName) throw new NotSupportedException(message);
            if (type == typeof(InvalidOperationException).FullName) throw new InvalidOperationException(message);
            throw new LiteException(0, $"{type}: {message}");
        }

        internal static BsonValue Expression(BsonExpression expression) => expression == null
            ? BsonValue.Null
            : new BsonDocument { ["s"] = expression.Source, ["p"] = expression.Parameters ?? new BsonDocument() };

        internal static BsonExpression Expression(BsonValue value) => value.IsNull
            ? null
            : BsonExpression.Create(value["s"].AsString, value["p"].AsDocument);

        internal static BsonDocument QueryToBson(Query query)
        {
            if (query.HasVectorFilter || query.VectorFilter != null || query.VectorScore != null)
                throw new NotSupportedException("Vector queries inside a coordinated transaction are not supported by the experimental coordinator.");
            return new BsonDocument
            {
                ["select"] = Expression(query.Select),
                ["includes"] = new BsonArray(query.Includes.Select(Expression)),
                ["where"] = new BsonArray(query.Where.Select(Expression)),
                ["orderBy"] = new BsonArray(query.OrderBy.Select(x => new BsonDocument { ["e"] = Expression(x.Expression), ["o"] = x.Order })),
                ["groupBy"] = Expression(query.GroupBy),
                ["having"] = Expression(query.Having),
                ["offset"] = query.Offset,
                ["limit"] = query.Limit,
                ["forUpdate"] = query.ForUpdate,
                ["into"] = query.Into == null ? BsonValue.Null : query.Into,
                ["intoAutoId"] = (int)query.IntoAutoId,
                ["explain"] = query.ExplainPlan
            };
        }

        internal static Query QueryFromBson(BsonDocument value)
        {
            var query = new Query
            {
                Select = Expression(value["select"]) ?? BsonExpression.Root,
                GroupBy = Expression(value["groupBy"]),
                Having = Expression(value["having"]),
                Offset = value["offset"].AsInt32,
                Limit = value["limit"].AsInt32,
                ForUpdate = value["forUpdate"].AsBoolean,
                Into = value["into"].IsNull ? null : value["into"].AsString,
                IntoAutoId = (BsonAutoId)value["intoAutoId"].AsInt32,
                ExplainPlan = value["explain"].AsBoolean
            };
            query.Includes.AddRange(value["includes"].AsArray.Select(Expression));
            query.Where.AddRange(value["where"].AsArray.Select(Expression));
            query.OrderBy.AddRange(value["orderBy"].AsArray.Select(x => new QueryOrder(Expression(x["e"]), x["o"].AsInt32)));
            return query;
        }

        internal static BsonArray Rows(IBsonDataReader reader)
        {
            var rows = new BsonArray();
            using (reader)
            {
                while (reader.Read()) rows.Add(reader.Current);
            }
            return rows;
        }

        internal static List<BsonValue> Values(BsonValue array) => array.AsArray.ToList();
    }
}
#endif
