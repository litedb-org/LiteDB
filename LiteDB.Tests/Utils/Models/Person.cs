using System;
using System.Collections.Generic;

namespace LiteDB.Tests
{
    public class Person : IEqualityComparer<Person>, IComparable<Person>
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Age { get; set; }
        public string[] Phones { get; set; }
        public string Email { get; set; }
        public Address Address { get; set; }
        public DateTime Date { get; set; }
        public bool Active { get; set; }

        public int CompareTo(Person other)
        {
            return this.Id.CompareTo(other.Id);
        }

        public bool Equals(Person x, Person y)
        {
            return x.Id == y.Id;
        }

        public int GetHashCode(Person obj)
        {
            return obj.Id.GetHashCode();
        }

        public override string ToString()
        {
            return new BsonMapper().Serialize(this).ToString();
        }
    }

    public class Address
    {
        public string Street { get; set; }
        public string City { get; set; }
        public string State { get; set; }
    }
}