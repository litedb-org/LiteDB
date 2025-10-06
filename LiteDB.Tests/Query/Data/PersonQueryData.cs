using System;
using System.Linq;
using LiteDB.Tests.Utils;

namespace LiteDB.Tests.QueryTest
{
    public class PersonQueryData : IDisposable
    {
        private readonly Person[] _local;
        private readonly ILiteDatabase _db;
        private readonly ILiteCollection<Person> _collection;

        public PersonQueryData()
        {
            _local = DataGen.Person().ToArray();

            _db = DatabaseFactory.Create();
            _collection = _db.GetCollection<Person>("person");
            _collection.Insert(this._local);
        }

        public (ILiteCollection<Person>, Person[]) GetData() => (_collection, _local);

        public void Dispose()
        {
            _db.Dispose();
        }
    }
}