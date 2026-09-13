using FluentAssertions;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class GroupBy_Tests
    {
        [Fact(Skip = "Missing implement LINQ for GroupBy")]
        public void Query_GroupBy_Age_With_Count()
        {
            //**using var db = new PersonGroupByData();
            //**var (collection, local) = db.GetData();
            //**
            //**var r0 = local
            //**    .GroupBy(x => x.Age)
            //**    .Select(x => new { Age = x.Key, Count = x.Count() })
            //**    .OrderBy(x => x.Age)
            //**    .ToArray();
            //**
            //**var r1 = collection.Query()
            //**    .GroupBy("$.Age")
            //**    .Select(x => new { Age = x.Key, Count = x.Count() })
            //**    .ToArray();
            //**
            //**foreach (var r in r0.Zip(r1, (l, r) => new { left = l, right = r }))
            //**{
            //**    r.left.Age.Should().Be(r.right.Age);
            //**    r.left.Count.Should().Be(r.right.Count);
            //**}
        }

        [Fact(Skip = "Missing implement LINQ for GroupBy")]
        public void Query_GroupBy_Year_With_Sum_Age()
        {
            //** var r0 = local
            //**     .GroupBy(x => x.Date.Year)
            //**     .Select(x => new { Year = x.Key, Sum = x.Sum(q => q.Age) })
            //**     .OrderBy(x => x.Year)
            //**     .ToArray();
            //**
            //** var r1 = collection.Query()
            //**     .GroupBy(x => x.Date.Year)
            //**     .Select(x => new { Year = x.Key, Sum = x.Sum(q => q.Age) })
            //**     .ToArray();
            //**
            //** foreach (var r in r0.Zip(r1, (l, r) => new { left = l, right = r }))
            //** {
            //**     r.left.Year.Should().Be(r.right.Year);
            //**     r.left.Sum.Should().Be(r.right.Sum);
            //** }
        }

        [Fact(Skip = "Missing implement LINQ for GroupBy")]
        public void Query_GroupBy_Func()
        {
            //** var r0 = local
            //**     .GroupBy(x => x.Date.Year)
            //**     .Select(x => new { Year = x.Key, Count = x.Count() })
            //**     .OrderBy(x => x.Year)
            //**     .ToArray();
            //**
            //** var r1 = collection.Query()
            //**     .GroupBy(x => x.Date.Year)
            //**     .Select(x => new { x.Date.Year, Count = x })
            //**     .ToArray();
            //**
            //** foreach (var r in r0.Zip(r1, (l, r) => new { left = l, right = r }))
            //** {
            //**     Assert.Equal(r.left.Year, r.right.Year);
            //**     Assert.Equal(r.left.Count, r.right.Count);
            //** }
        }

        [Fact(Skip = "Missing implement LINQ for GroupBy")]
        public void Query_GroupBy_With_Array_Aggregation()
        {
            //** // quite complex group by query
            //** var r = collection.Query()
            //**     .GroupBy(x => x.Email.Substring(x.Email.IndexOf("@") + 1))
            //**     .Select(x => new
            //**     {
            //**         Domain = x.Email.Substring(x.Email.IndexOf("@") + 1),
            //**         Users = Sql.ToArray(new
            //**         {
            //**             Login = x.Email.Substring(0, x.Email.IndexOf("@")).ToLower(),
            //**             x.Name,
            //**             x.Age
            //**         })
            //**     })
            //**     .Limit(10)
            //**     .ToArray();
            //**
            //** // test first only
            //** Assert.Equal(5, r[0].Users.Length);
            //** Assert.Equal("imperdiet.us", r[0].Domain);
            //** Assert.Equal("delilah", r[0].Users[0].Login);
            //** Assert.Equal("Dahlia Warren", r[0].Users[0].Name);
            //** Assert.Equal(24, r[0].Users[0].Age);
        }
    }
}