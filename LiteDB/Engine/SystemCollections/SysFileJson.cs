using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal class SysFileJson : SystemCollection
    {
        public SysFileJson() : base("$file_json")
        {
        }

        public override IEnumerable<BsonDocument> Input(BsonValue options)
        {
            var filename = GetOption(options, "filename")?.AsString ?? throw new LiteException(0, $"Collection ${this.Name} requires string as 'filename' or a document field 'filename'");
            var encoding = GetOption(options, "encoding", "utf-8").AsString;

            using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using (var reader = new StreamReader(fs, Encoding.GetEncoding(encoding)))
                {
                    var json = new JsonReader(reader);

                    var source = json.DeserializeArray()
                        .Select(x => x.AsDocument);

                    // read documents inside file and return one-by-one
                    foreach (var doc in source)
                    {
                        yield return doc;
                    }
                }
            }
        }

        public override int Output(IEnumerable<BsonDocument> source, BsonValue options)
        {
            var filename = GetOption(options, "filename")?.AsString ?? throw new LiteException(0, "Collection $file_json requires string as filename or a document field 'filename'");
            var pretty = GetOption(options, "pretty", false).AsBoolean;
            var indent = GetOption(options, "indent", 4).AsInt32;
            var encoding = GetOption(options, "encoding", "utf-8").AsString;
            var overwritten = GetOption(options, "overwritten", false).AsBoolean;

            var index = 0;
            FileStream fs = null;
            StreamWriter writer = null;
            JsonWriter json = null;

            try
            {
                foreach (var doc in source)
                {
                    if (index++ == 0)
                    {
                        fs = new FileStream(filename, overwritten ? FileMode.OpenOrCreate : FileMode.CreateNew);
                        writer = new StreamWriter(fs, Encoding.GetEncoding(encoding));
                        json = new JsonWriter(writer)
                        {
                            Pretty = pretty,
                            Indent = indent
                        };

                        writer.WriteLine("[");
                    }
                    else
                    {
                        writer.WriteLine(",");
                    }

                    json.Serialize(doc);
                }

                if (index > 0)
                {
                    writer.WriteLine();
                    writer.Write("]");
                    writer.Flush();
                }
            }
            finally
            {
                if (writer != null) writer.Dispose();
                if (fs != null) fs.Dispose();
            }

            return index;
        }
    }
}