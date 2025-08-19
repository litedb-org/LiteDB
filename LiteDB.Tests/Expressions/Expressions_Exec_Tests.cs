using AwesomeAssertions;
using System;
using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class ExpressionsExec_Tests
    {
        public BsonDocument J(string j) => JsonSerializer.Deserialize(j).AsDocument;

        [Fact]
        public void Expressions_Scalar_Path()
        {
            BsonDocument doc;

            BsonValue S(string s)
            {
                return BsonExpression.Create(s).ExecuteScalar(doc);
            }

            // cover the fix of "copy/paste" errors
            doc = new BsonDocument();
            S("[1, 6] ALL > 5").ExpectValue(false);
            S("[1, 6] ALL >= 5").ExpectValue(false);
            S("[1, 6] ALL < 5").ExpectValue(false);
            S("[1, 6] ALL <= 5").ExpectValue(false);
            S("[1, 5] ALL != 5").ExpectValue(false);

            // direct path navigation
            doc = J("{ a: 1, b: null, c: true, d:[1,2], e:{d:4} }");

            S("a").ExpectValue(1);
            S("b").ExpectValue(BsonValue.Null);
            S("c").ExpectValue(true);
            S("d").ExpectArray(1, 2);
            S("e").ExpectJson("{d:4}");

            // dive into subdocumentos
            doc = J("{ a: { b: { c: { d: 1 } } } }");

            S("a").ExpectJson("{ b: { c: { d: 1 } } }");
            S("a.b").ExpectJson("{ c: { d: 1 } }");
            S("a.b.c").ExpectJson("{ d: 1 }");
            S("a.b.c.d").ExpectValue(1);

            // Missing field
            doc = J("{ a: { b: 1 } }");

            S("b").ExpectValue(BsonValue.Null);
            S("a.c").ExpectValue(BsonValue.Null);
            S("x.j").ExpectValue(BsonValue.Null);

            // Array fixed position
            doc = J("{ a: [1, 2, 3] }");

            S("a[0]").ExpectValue(1);
            S("a[1]").ExpectValue(2);
            S("a[-1]").ExpectValue(3);
            S("a[99]").ExpectValue(BsonValue.Null);

            // Root and Current in array
            doc = J("{ a: [ { b: 1, c: 2 }, { b: 2, c: 3 } ], i: 0 }");

            S("FIRST(a[@.b = 1].c)").ExpectValue(2);
            S("FIRST(a[b = 2].c)").ExpectValue(3);

            // Complex field name
            doc = J("{ \"a b\": 1, \"c d\": { \"x y\": 2 }, x: { \"$y!z\\\"'\": 3 } }");

            S("$.[\"a b\"]").ExpectValue(1);
            S("$.['c d'].['x y']").ExpectValue(2);
            S("$.x.[\"$y!z\\\"'\"]").ExpectValue(3);

            // Object creation
            S("'lite' + \"db\"").ExpectValue("litedb");
            S("true").ExpectValue(true);
            S("123").ExpectValue(123);
            S("{ a: 1}").ExpectJson("{ a: 1}");
            S("{ b: 1+1 }").ExpectJson("{ b: 2 }");
            S("{'a-b':1, \"x+1\": 2, 'y': 3}").ExpectJson("{\"a-b\": 1, \"x+1\": 2, y: 3 }");

            // Document simplified notation declaration
            doc = J("{ a: 1, b: 2, c: 3, d: {e: 4, f: 5 }}");

            S("{ a }").ExpectJson("{ a: 1 }");
            S("{ a, c }").ExpectJson("{ a: 1, c: 3 }");
            S("{ d, z: d.e }").ExpectJson("{ d: { e: 4, f: 5 }, z: 4 }");

            // Document simplified notation with complex key
            doc = J("{ \"a b\": 1, c: 2 }");

            S("{ 'a b', z:  null }").ExpectJson("{ \"a b\": 1, z: null }");
            S("{ 'c' }").ExpectJson("{ c: 2 }");
        }

        [Fact]
        public void Expressions_Scalar_Operator()
        {
            BsonDocument doc;

            BsonValue S(string s, params BsonValue[] args)
            {
                return BsonExpression.Create(s, args).ExecuteScalar(doc);
            }

            // Operators order
            doc = J("{ a: 1, b: 2, c: 3 }");

            S("a + 1 - c").ExpectValue(-1);
            S("5 + c * 2").ExpectValue(11);
            S("(5 + c) * 2").ExpectValue(16);

            // Comparer operators
            doc = J("{ a: 1, b: 2, c: 3 }");

            S("1 = 1").ExpectValue(true);
            S("a = 1").ExpectValue(true);
            S("a > 0 and c = 3").ExpectValue(true);
            S("a = 0 or b = 1 or c = 2 or 1=1").ExpectValue(true);

            // Multi values equals
            doc = J("{ a: 5 }");

            S("ARRAY(MAP(ITEMS([3, 4, 5, 6]) => (@ = $.a)))").ExpectArray(false, false, true, false);
            S("ANY(MAP(ITEMS([3, 4, 5, 6]) => (@ = $.a)))").ExpectValue(true);

            // Between
            doc = J("{ a: 50, b: 'm' }");

            S("5 between 1 AND 10").ExpectValue(true);
            S("b between @0   and @1", "a", "z").ExpectValue(true);

            // Greater and Less
            S("1 < 3.0").ExpectValue(true);
            S("4 < 8").ExpectValue(true);
            S("3 >= 3").ExpectValue(true);
            S("2 <= 1").ExpectValue(false);
            S("2 <= 'a'").ExpectValue(true);

            // String Like
            S("'John' LIKE 'J'").ExpectValue(false);
            S("'John' LIKE 'J%'").ExpectValue(true);
            S("'John' LIKE @0", "%o%").ExpectValue(true);
            S("'John' LIKE 1").ExpectValue(false);

            doc = J("{ names: ['John', 'Joana', 'Carlos'] }");

            S("ARRAY(MAP(names[*] => (@ LIKE 'J%')))").ExpectArray(true, true, false);
            S("ARRAY(names[@ LIKE 'J%'])").ExpectArray("John", "Joana");

            // Array filter using root/current
            doc = J("{ a: 3, b: [{a: 2, x: 99}, {a: 3, x: 100}] }");

            S("FIRST(b[@.a=2].x)").ExpectValue(99);
            S("FIRST(b[@.a = $.a].x)").ExpectValue(100);

            // MongoDB examples
            doc = J("{ '_id' : 3, 'date' : 'October 30', 'temps' : [ 18, 6, 8 ] }");

            S("{ date, 'temps in Fahrenheit': ARRAY(MAP(temps[*] => (@ * (9/5)+32)))}")
                .ExpectJson("{ 'date' : 'October 30', 'temps in Fahrenheit' : [ 64.4, 42.8, 46.4 ]}");
        }

        [Fact]
        public void Expressions_Scalar_Methods()
        {
            BsonDocument doc;

            BsonValue S(string s, params BsonValue[] args)
            {
                return BsonExpression.Create(s, args).ExecuteScalar(doc);
            };

            doc = J("{}");

            // String functions
            S("'Lite' + 'DB' + 'v' + 5").ExpectValue("LiteDBv5");
            S("upper('liteDB')").ExpectValue("LITEDB");
            S("LOWER('LiteDB')").ExpectValue("litedb");
            S("UPPER('LiteDB' + '-ok')").ExpectValue("LITEDB-OK");
            S("UPPER('LiteDB') + '-ok'").ExpectValue("LITEDB-ok");
            S("UPPER(3)").ExpectValue(BsonValue.Null);
            S("SUBSTRING('LiteDB', 0, 2)").ExpectValue("Li");
            S("LOWER(SUBSTRING('LiteDB', 4))").ExpectValue("db");
            S("LPAD(STRING(27), 5, '0')").ExpectValue("00027");
            S("RPAD(STRING(27), 5, '0')").ExpectValue("27000");
            S("REPLACE('Hi', 'i', 'I')").ExpectValue("HI");
            S("ARRAY(MAP(ITEMS(['Lite', 'LiteDB']) => REPLACE(@, 'L', 'x')))").ExpectArray("xite", "xiteDB");

            // String in array items
            doc = J("{ arr: ['one', 'two'] }");

            S("ARRAY(MAP(arr[*] => UPPER(@)))").ExpectArray("ONE", "TWO");
            S("ARRAY(MAP(arr[*] => SUBSTRING(@, 0, 1)))").ExpectArray("o", "t");

            // JSON Parser
            S("JSON('{a:1, b:\"string\"}')").ExpectJson("{ a: 1, b: \"string\" }");
            S("JSON('\"string-only\"')").ExpectValue("string-only");
            S("JSON('error')").ExpectValue(BsonValue.Null);

            // EXTEND document
            doc = J("{a:1}");

            S("EXTEND($, {b:2})").ExpectJson("{a:1, b:2}");
            S("EXTEND($, {a:2})").ExpectJson("{a:2}");
            S("EXTEND($, {a:2, b:{c:2}})").ExpectJson("{a: 2, b: {c: 2} }");
            S("EXTEND({a:true}, {a: false})").ExpectJson("{a:false}");

            // DATE functions
            doc = J("{mydate:{$date: '2018-05-01T15:30:45Z'}}");

            S("YEAR($.mydate)").ExpectValue(2018);
            S("MONTH($.mydate)").ExpectValue(5);
            S("DAY($.mydate)").ExpectValue(1);

            // dateParts: "y|year", "M|month", "d|day", "h|hour", "m|minute", "s|second"

            S("DATEADD('d', 1, $.mydate)").ExpectValue(DateTime.Parse("2018-05-02T15:30:45Z"));
            S("DATEADD('d', -1, $.mydate)").ExpectValue(DateTime.Parse("2018-04-30T15:30:45Z"));
            S("DATEADD('M', 12, $.mydate)").ExpectValue(DateTime.Parse("2019-05-01T15:30:45Z"));


            S("DATEDIFF('M', $.mydate, DATE_UTC(2018, 6, 1))").ExpectValue(1);
            S("DATEDIFF('M', $.mydate, DATE_UTC(2018, 4, 1))").ExpectValue(-1);

            // Length Method
            doc = J("{a:'my string', empty: '', rnull: null, b: {$binary:'MTIz'}, arr: [1, 2, null]}");

            S("LENGTH(a)").ExpectValue(9);
            S("LENGTH(empty)").ExpectValue(0);
            S("LENGTH(null)").ExpectValue(0);
            S("LENGTH(miss)").ExpectValue(0);
            S("LENGTH(b)").ExpectValue(3);
            S("LENGTH(arr)").ExpectValue(3);
            S("LENGTH($)").ExpectValue(5);

            S("ROUND(DECIMAL(123.45678), 3)").ExpectValue(123.457m);
            S("ROUND(DOUBLE(123.45678), 3)").ExpectValue(123.457d);
        }

        [Fact]
        public void Expressions_Scalar_Parameters()
        {
            BsonDocument doc;

            BsonValue P(string s, params BsonValue[] args)
            {
                return BsonExpression.Create(s, args).ExecuteScalar(doc);
            }

            doc = J("{ arr: [1, 2, 3, 4, 5 ] }");

            P("@0", 10).ExpectValue(10);
            P("@0 + 15", 10).ExpectValue(25);
            P("UPPER(@0 + @1)", "lite", "db").ExpectValue("LITEDB");

            // parameter only filter = fixed index
            P("arr[@0]", 0).ExpectValue(1);

            // any other case: filter query
            P("ARRAY(arr[@ > @0])", 3).ExpectArray(4, 5);

            // using map
            P("ARRAY(MAP(ITEMS(@0) => (@ + @1)))", new BsonArray(new BsonValue[] { 10, 11, 12 }), 5)
                .ExpectArray(15, 16, 17);

            // sort ascending
            P("ARRAY(SORT(ITEMS(@0) => @, @1))", new BsonArray(new BsonValue[] { 30, 20, 50 }), 1)
                .ExpectArray(20, 30, 50);

            P("ARRAY(SORT(ITEMS(@0) => @, @1))", new BsonArray(new BsonValue[] { 30, 20, 50 }), "asc")
                .ExpectArray(20, 30, 50);

            // sort descending
            P("ARRAY(SORT(ITEMS(@0) => @, @1))", new BsonArray(new BsonValue[] { 30, 20, 50 }), -1)
                .ExpectArray(50, 30, 20);

            P("ARRAY(SORT(ITEMS(@0) => @, @1))", new BsonArray(new BsonValue[] { 30, 20, 50 }), "desc")
                .ExpectArray(50, 30, 20);
        }

        [Fact]
        public void Expressions_Enumerable_Expr()
        {
            List<BsonDocument> docs;

            IEnumerable<BsonValue> A(string s, params BsonValue[] args)
            {
                return BsonExpression.Create(s, args).Execute(docs);
            };

            docs = new List<BsonDocument>()
            {
                J("{ a: 1, b: 5, c: \"First\", arr: [1,2] }"),
                J("{ a: 2, b: 15, c: \"Second\", arr: [1, 3, 5, 9] }"),
                J("{ a: 5, b: 10, c: \"Last\", arr: [1, 5, 5] }")
            };

            A("SUM(*.a)").ExpectValues(8);
            A("*.b").ExpectValues(5, 15, 10);
            A("MAP(*.c => UPPER(@))", "FIRST", "SECOND", "LAST");

            A("SUM(*.arr[*])").ExpectValues(32);
            A("SUM(*.arr[@ < 2]) + 7").ExpectValues(10);

            A("JOIN(*.c, '#')").ExpectValues("First#Second#Last");

            // when use $ over multiple values, only first result are used
            A("JOIN(MAP(*.arr[*] => (@ + 1)), '-')").ExpectValues("2-3-2-4-6-10-2-6-6");

            // flaten
            A("*.arr[*]").ExpectValues(1, 2, 1, 3, 5, 9, 1, 5, 5);

            // Top Method
            A("TOP([1,2,3,4,5],3)").ExpectValues(1, 2, 3);
            A("TOP([1,2,3,4,5],0)").ExpectValues();
            A("TOP([1,2,3,4,5],-3)").ExpectValues();
            A("TOP([1,2,3,4,5],10)").ExpectValues(1, 2, 3, 4, 5);

            // Union Method
            A("UNION([1,2,3],4)").ExpectValues(1, 2, 3, 4);
            A("UNION([1,2,3],[4])").ExpectValues(1, 2, 3, 4);
            A("UNION([1,2,3],[4,5,6,7])").ExpectValues(1, 2, 3, 4, 5, 6, 7);
            A("UNION([1,2,3],[1,2,3])").ExpectValues(1, 2, 3);

            // Except Method
            A("EXCEPT([1,2,3],4)").ExpectValues(1, 2, 3);
            A("EXCEPT([1,2,3],1)").ExpectValues(2, 3);
            A("EXCEPT([1,2,3],[1,3])").ExpectValues(2);
            A("EXCEPT([1,2,3],[4,5])").ExpectValues(1, 2, 3);
        }

        [Fact]
        public void Expression_Multiple_And_Tests()
        {
            BsonValue B(params BsonExpression[] args)
            {
                return Query.And(args).ExecuteScalar();
            };

            B("5 > 3", "10 != 'a'", "1 = 1").ExpectValue(true);
            B("5 < 3", "10 != 'a'", "1 = 1").ExpectValue(false);
            B("5 > 3", "10 = 'a'", "1 = 1").ExpectValue(false);
            B("5 > 3", "10 != 'a'", "1 != 1").ExpectValue(false);
        }

        [Fact]
        public void Expression_Multiple_Or_Tests()
        {
            BsonValue B(params BsonExpression[] args)
            {
                return Query.Or(args).ExecuteScalar();
            };

            B("5 > 3", "10 != 'a'", "1 = 1").ExpectValue(true);
            B("5 < 3", "10 != 'a'", "1 = 1").ExpectValue(true);
            B("5 > 3", "10 = 'a'", "1 = 1").ExpectValue(true);
            B("5 < 3", "10 = 'a'", "1 != 1").ExpectValue(false);
        }

        [Fact]
        public void Expression_AndAlso_OrElse()
        {
            var ex1 = BsonExpression.Create("LENGTH($.x) >= 5 AND SUBSTRING($.x, 0, 5) = \"12345\"");
            var doc1 = new BsonDocument();

            // OK (true)
            doc1["x"] = "12345";
            ex1.ExecuteScalar(doc1);

            // KO (expected: false, actual: exception)
            doc1["x"] = "123";
            ex1.ExecuteScalar(doc1);
        }

        [Fact]
        public void Expression_Conditional_IIF()
        {
            var ex1 = BsonExpression.Create("IIF(LENGTH($.x) >= 5, SUBSTRING($.x, 0, 5), \"too-short\")");
            var doc1 = new BsonDocument();

            // OK ("12345")
            doc1["x"] = "123456789";
            var r1 = ex1.ExecuteScalar(doc1);

            r1.AsString.Should().Be("12345");

            // KO (expected: "too-short", actual: System.ArgumentOutOfRangeException: Index and length must refer to a location within the string.)
            doc1["x"] = "123";
            var r2 = ex1.ExecuteScalar(doc1);

            r2.AsString.Should().Be("too-short");
        }
    }
}