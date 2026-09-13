using FluentAssertions;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class PersonGroupByData : IDisposable
    {
        private readonly Person[] _local;
        private readonly ILiteDatabase _db;
        private readonly ILiteCollection<Person> _collection;

        public PersonGroupByData()
        {
            _local = DataGen.Person(1, 1000).ToArray();
            _db = new LiteDatabase(new MemoryStream());
            _collection = _db.GetCollection<Person>();

            _collection.Insert(_local);
            _collection.EnsureIndex(x => x.Age);
        }

        public (ILiteCollection<Person>, Person[]) GetData() => (_collection, _local);

        public void Dispose()
        {
            _db.Dispose();
        }
    }
}