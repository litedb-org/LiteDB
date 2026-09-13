using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteDB.Tests
{
    public class DataGen
    {
        /// <summary>
        /// Return fixed 1000 Person instances
        /// </summary>
        public static IEnumerable<Person> Person()
        {
            using (var stream = typeof(DataGen).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources.person.json"))
            {
                var reader = new StreamReader(stream);

                var docs = JsonSerializer.DeserializeArray(reader).Select(x => x.AsDocument);
                var id = 0;

                foreach (var doc in docs)
                {
                    yield return new Person
                    {
                        Id = ++id,
                        Name = doc["name"],
                        Age = doc["age"],
                        Phones = doc["phone"].AsString.Split('-'),
                        Email = doc["email"],
                        Date = doc["date"],
                        Active = doc["active"],
                        Address = new Address
                        {
                            Street = doc["street"],
                            City = doc["city"],
                            State = doc["state"]
                        }
                    };
                }
            }
        }

        /// <summary>
        /// Return Person instances
        /// </summary>
        public static IEnumerable<Person> Person(int start, int end)
        {
            foreach (var p in Person().Skip(start - 1).Take(end - start + 1))
            {
                yield return p;
            }
        }

        /// <summary>
        /// Return fixed 29353 Zips instances
        /// </summary>
        public static IEnumerable<Zip> Zip()
        {
            using (var stream = typeof(DataGen).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources.zip.json"))
            {
                var reader = new StreamReader(stream);

                var s = reader.ReadToEnd();

                return JsonSerializer.DeserializeArray(s)
                    .Select(x => x.AsDocument)
                    .Select(x => BsonMapper.Global.ToObject<Zip>(x));
            }
        }
    }
}