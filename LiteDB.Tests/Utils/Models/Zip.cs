using System;
using System.Collections.Generic;

namespace LiteDB.Tests
{
    // { 
    //     "_id": "01001", 
    //     "city": "AGAWAM", 
    //     "loc": [ -72.622739, 42.070206 ], 
    //     "pop": 15338, 
    //     "state": "MA"
    // }
    public class Zip : IEqualityComparer<Zip>, IComparable<Zip>
    {
        public string Id { get; set; }
        public string City { get; set; }
        public double[] Loc { get; set; }
        public string State { get; set; }
        public byte[] Payload { get; set; }

        public int CompareTo(Zip other)
        {
            return this.Id.CompareTo(other.Id);
        }

        public bool Equals(Zip x, Zip y)
        {
            return x.Id == y.Id &&
                   x.City == y.City &&
                   x.Loc == y.Loc &&
                   x.State == y.State;
        }

        public int GetHashCode(Zip obj)
        {
            return this.Id.GetHashCode();
        }

        public override string ToString()
        {
            return new BsonMapper().Serialize(this).ToString();
        }
    }
}