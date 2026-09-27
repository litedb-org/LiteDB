#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using LiteDB.Engine;
using static LiteDB.Client.Coordinated.CoordinatorProtocol;

namespace LiteDB.Client.Coordinated
{
    internal sealed partial class CoordinatorHost
    {
        private BsonValue Execute(Stream stream, string op, BsonDocument r)
        {
            switch (op)
            {
                case "hello": return Environment.ProcessId;
                case "begin": return this.Run(e => e.BeginTrans());
                case "commit": return this.Run(e => e.Commit());
                case "rollback": return this.Run(e => e.Rollback());
                case "checkpoint": return this.Run(e => e.Checkpoint());
                case "insert": return this.Run(e => e.Insert(r["c"].AsString, Pull(stream), (BsonAutoId)r["a"].AsInt32));
                case "upsert": return this.Run(e => e.Upsert(r["c"].AsString, Pull(stream), (BsonAutoId)r["a"].AsInt32));
                case "update": return this.Run(e => e.Update(r["c"].AsString, Pull(stream)));
                case "updateMany":
                    return this.Run(e => e.UpdateMany(r["c"].AsString, Expression(r["t"]), Expression(r["p"])));
                case "delete": return this.Run(e => e.Delete(r["c"].AsString, Values(r["ids"])));
                case "deleteMany": return this.Run(e => e.DeleteMany(r["c"].AsString, Expression(r["p"])));
                case "dropCollection": return this.Run(e => e.DropCollection(r["n"].AsString));
                case "rename": return this.Run(e => e.RenameCollection(r["n"].AsString, r["nn"].AsString));
                case "ensureIndex":
                    return this.Run(e => e.EnsureIndex(r["c"].AsString, r["n"].AsString, Expression(r["e"]), r["u"].AsBoolean));
                case "dropIndex": return this.Run(e => e.DropIndex(r["c"].AsString, r["n"].AsString));
                case "pragma": return this.Run(e => e.Pragma(r["n"].AsString));
                case "setPragma": return this.Run(e => e.Pragma(r["n"].AsString, r["v"]));
                case "query":
                {
                    var (rows, collection) = this.Run(e =>
                    {
                        var reader = e.Query(r["c"].AsString, QueryFromBson(r["q"].AsDocument));
                        var name = reader.Collection;
                        return (Rows(reader), name);
                    });
                    // Send all but the last chunk here, outside the gate; the caller writes the last.
                    var chunks = Chunk(rows);
                    for (var i = 0; i < chunks.Count - 1; i++)
                        Write(stream, Ok(new BsonDocument { ["rows"] = chunks[i], ["more"] = true }));
                    return new BsonDocument
                    {
                        ["rows"] = chunks[chunks.Count - 1],
                        ["more"] = false,
                        ["c"] = collection ?? (BsonValue)BsonValue.Null
                    };
                }
                default: throw new NotSupportedException($"Unknown coordinator operation '{op}'.");
            }
        }

        /// <summary>
        /// Stream the client's documents into the engine one at a time. Before
        /// pulling the next document, the previous one's final <c>_id</c> goes back,
        /// so the client's generated-ID handoff runs before its enumerator advances,
        /// exactly as with a local engine. Every client message is solicited, so an
        /// engine error simply replaces the next solicitation.
        /// </summary>
        private static IEnumerable<BsonDocument> Pull(Stream stream)
        {
            BsonDocument previous = null;
            while (true)
            {
                BsonDocument message;
                try
                {
                    Write(stream, new BsonDocument { ["next"] = true, ["id"] = previous?["_id"] ?? BsonValue.Null });
                    message = Read(stream);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is TimeoutException)
                {
                    throw new CoordinatorLostException(ex);
                }
                if (message.ContainsKey("abort")) throw new LiteException(0, "The client aborted the operation: " + message["abort"].AsString);
                if (message.ContainsKey("end")) yield break;
                previous = message["doc"].AsDocument;
                yield return previous;
            }
        }
    }

    /// <summary>The peer's pipe failed; the session or client connection is unusable.</summary>
    internal sealed class CoordinatorLostException : IOException
    {
        internal CoordinatorLostException(Exception inner)
            : base("The coordinator connection was lost.", inner)
        {
        }
    }
}
#endif
