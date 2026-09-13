using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqBsonExpression_Tests
    {
        #region Model

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public double Salary { get; set; }
            public DateTime CreatedOn { get; set; }
            public bool Active { get; set; }

            public Address Address { get; set; }
            public List<Phone> Phones { get; set; }
            public Phone[] Phones2 { get; set; }

            public int[] PhoneNumbers { get; set; }

            [BsonField("USER_DOMAIN_NAME")]
            public string DomainName { get; }

            public int? Latitude { get; set; }

            /// <summary>
            /// This type will be render as new BsonDoctument { [key] = value }
            /// </summary>
            public IDictionary<string, Address> MetaData { get; set; }
        }

        public class Address
        {
            public string Street { get; set; }
            public int Number { get; set; }
            public City City { get; set; }

            public string InvalidMethod() => "will thow error in eval";
        }

        public class City
        {
            public string CityName { get; set; }
            public string Country { get; set; }
        }

        public class Phone
        {
            public PhoneType Type { get; set; }
            public int Prefix { get; set; }
            public int Number { get; set; }
        }

        public enum PhoneType
        {
            Mobile,
            Landline
        }

        public class Customer
        {
            public int CustomerId { get; set; }
            public string Name { get; set; }
        }

        public class Order
        {
            [BsonId]
            public int OrderNumber { get; set; }

            [BsonRef("customers")]
            public Customer Customer { get; set; }

            [BsonRef("users")]
            public List<User> Users { get; set; }
        }

        public class Account
        {
            [BsonId]
            public Guid Id { get; set; }

            [BsonRef("customers")]
            public Customer Customer { get; set; }
        }

        public class Product
        {
            [BsonId]
            public ObjectId Id { get; set; }

            public string Name { get; set; }
        }

        private static Address StaticProp { get; set; } = new Address { Number = 99 };
        private const int CONST_INT = 100;
        private string MyMethod() => "ok";
        private int MyIndex() => 5;

        #endregion

        private readonly BsonMapper _mapper = new BsonMapper();

        [Fact]
        public void Linq_Document_Navigation()
        {
            // document navigation
            TestExpr<User>(x => x.Id, "_id");
            TestExpr<User>(x => x.Name, "Name");
            TestExpr<User>(x => x.Address.Street, "Address.Street");
            TestExpr<User>(x => x.Address.City.Country, "Address.City.Country");
        }

        [Fact]
        public void Linq_Constants()
        {
            // only constants
            var today = DateTime.Today;
            var john = "JOHN";
            var a = new { b = new { c = "JOHN" } };

            // only constants
            TestExpr(x => 0, "@p0", 0);
            TestExpr(x => 1 + 1, "@p0", 2); // "1 + 1" will be resolved by LINQ before convert

            // values from variables
            TestExpr(x => today, "@p0", today);

            // values from deep object variables
            TestExpr(x => a.b.c, "@p0", a.b.c);
            TestExpr<User>(x => x.Address.Street == a.b.c, "(Address.Street = @p0)", a.b.c);

            // class constants
            TestExpr(x => CONST_INT, "@p0", CONST_INT);
            TestExpr(x => StaticProp.Number, "@p0", StaticProp.Number);

            // methods inside constants
            TestExpr(x => "demo".Trim(), "TRIM(@p0)", "demo");

            // execute method inside variables
            TestExpr(x => john.Trim(), "TRIM(@p0)", john);
            TestExpr(x => today.Day, "DAY(@p0)", today);

            // testing node stack using parameter-expression vs variable-expression
            TestExpr<User>(x => x.Name.Length > john.Length, "(LENGTH(Name) > LENGTH(@p0))", john);

            // calling external methods
            TestExpr<User>(x => x.Name == MyMethod(), "(Name = @p0)", MyMethod());
            TestExpr(x => MyMethod().Length, "LENGTH(@p0)", MyMethod());

            TestException<User, NotSupportedException>(() => x => x.Name == x.Address.InvalidMethod());
        }

        [Fact]
        public void Linq_Enumerables()
        {
            // access array items
            TestExpr<User>(x => x.Phones, "$.Phones");
            TestExpr<User>(x => x.Phones.AsEnumerable(), "$.Phones[*]");

            // where
            TestExpr<User>(x => x.Phones.Where(p => p.Prefix == 1), "FILTER($.Phones=>(@.Prefix=@p0))", 1);
            TestExpr<User>(x => x.Phones.Where(p => p.Prefix == x.Id), "FILTER($.Phones=>(@.Prefix=$._id))");

            // aggregate
            TestExpr<User>(x => x.Phones.Count(), "COUNT($.Phones)");
            TestExpr<User>(x => x.Phones.Min(), "MIN($.Phones)");
            TestExpr<User>(x => x.Phones.Max(), "MAX($.Phones)");
            TestExpr<User>(x => x.Phones.Select(p => p.Number).Sum(), "SUM(MAP($.Phones => @.Number))");

            // aggregate with map
            TestExpr<User>(x => x.Phones.Sum(w => w.Number), "SUM(MAP($.Phones => @.Number))");
            TestExpr<User>(x => x.Phones.Average(w => w.Number), "AVG(MAP($.Phones => @.Number))");
            TestExpr<User>(x => x.Phones.Max(w => w.Number), "MAX(MAP($.Phones => @.Number))");
            TestExpr<User>(x => x.Phones.Min(w => w.Number), "MIN(MAP($.Phones => @.Number))");

            // map
            TestExpr<User>(x => x.Phones.Select(y => y.Type), "MAP($.Phones => @.Type)");

            TestExpr<User>(x => x.Phones.Select(p => p.Number).Sum(), "SUM(MAP($.Phones => @.Number))");
            TestExpr<User>(x => x.Phones.Select(p => p.Number).Average(), "AVG(MAP($.Phones => @.Number))");

            // array/list
            TestExpr<User>(x => x.Phones.Where(w => w.Number == 5).ToArray(), "ARRAY(FILTER($.Phones=>(@.Number=@p0)))", 5);
            TestExpr<User>(x => x.Phones.ToList(), "ARRAY($.Phones)");

            // access using native array index (special "get_Item" eval index value)
            TestExpr<User>(x => x.Phones[1].Number, "$.Phones[1].Number");

            // fixed position
            TestExpr<User>(x => x.Phones[15], "$.Phones[15]");
            TestExpr<User>(x => x.Phones.ElementAt(1), "$.Phones[@p0]", 1);

            // call external method/props/const inside parameter expression
            var a = new { b = new { c = 123 } };

            // Items(int) generate eval value index
            TestExpr<User>(x => x.Phones[a.b.c].Number, "$.Phones[123].Number");
            TestExpr<User>(x => x.Phones[CONST_INT].Number, "$.Phones[100].Number");
            TestExpr<User>(x => x.Phones[MyIndex()].Number, "$.Phones[5].Number");

            // fixed position
            TestExpr<User>(x => x.Phones.First(), "$.Phones[0]");
            TestExpr<User>(x => x.Phones.Last(), "$.Phones[-1]");

            // contains
            TestExpr<User>(x => x.PhoneNumbers.Contains(1234), "PhoneNumbers ANY = @p0", 1234);
            TestExpr<User>(x => x.Phones2.Contains(new Phone { Number = 1 }), "Phones2 ANY = { Number: @p0 }", 1);

            // negated contains
            TestExpr<User>(x => !x.PhoneNumbers.Contains(1234), "(PhoneNumbers ANY = @p0) = false", 1234);
            TestExpr<User>(x => !x.Phones2.Contains(new Phone { Number = 1 }), "(Phones2 ANY = { Number: @p0 }) = false", 1);

            // fixed position with filter expression
            TestExpr<User>(x => x.Phones.First(p => p.Number == 1), "FIRST(FILTER($.Phones=>(@.Number=@p0)))", 1);

            // using any/all
            TestExpr<User>(x => x.Phones.Select(p => p.Number).Any(p => p == 1), "MAP(Phones => @.Number) ANY = @p0", 1);
            TestExpr<User>(x => x.Phones.Select(p => p.Number.ToString()).Any(p => p.StartsWith("51")),
                "MAP(Phones => STRING(@.Number)) ANY LIKE (@p0 + '%')", "51");
        }

        [Fact]
        public void Linq_Predicate()
        {
            // binary expressions
            TestPredicate<User>(x => x.Active == true, "(Active = @p0)", true);
            TestPredicate<User>(x => x.Salary > 50, "(Salary > @p0)", 50);
            TestPredicate<User>(x => x.Salary != 50, "(Salary != @p0)", 50);
            TestPredicate<User>(x => x.Salary == x.Id, "(Salary = _id)");
            TestPredicate<User>(x => x.Salary > 50 && x.Name == "John", "((Salary > @p0) AND (Name = @p1))", 50, "John");
            TestPredicate<User>(x => x.Salary > 50 & x.Name == "John", "((Salary > @p0) AND (Name = @p1))", 50, "John");
            TestPredicate<User>(x => x.Salary > 50 || x.Name == "John", "((Salary > @p0) OR (Name = @p1))", 50, "John");
            TestPredicate<User>(x => x.Salary > 50 | x.Name == "John", "((Salary > @p0) OR (Name = @p1))", 50, "John");

            // unary expressions
            TestPredicate<User>(x => x.Active, "(Active = true)");
            TestPredicate<User>(x => x.Active && x.Active, "(((Active) = true) AND ((Active) = true))");
            TestPredicate<User>(x => x.Active && x.Active && x.Active, "(((($.Active)=true) AND (($.Active)=true)) AND (($.Active)=true))");
            TestPredicate<User>(x => x.Active && !x.Active, "(((Active) = true) AND (Active = false))");
            TestPredicate<User>(x => !x.Active, "(Active = false)");
            TestPredicate<User>(x => !x.Active == true, "((Active = false) = @p0)", true);
            TestPredicate<User>(x => !(x.Salary == 50), "((Salary = @p0)) = false", 50);

            // test for precedence order
            TestPredicate<User>(x => x.Name.StartsWith("J") == false, "(Name LIKE (@p0 + '%') = @p1)", "J", false);

            // iif (c ? true : false)
            TestExpr<User>(x => x.Id > 10 ? x.Id : 0, "IIF((_id > @p0), _id, @p1)", 10, 0);

            // contains in Array/List
            var numbersArray = new int[] { 1, 2, 3 };
            var numbersList = new List<int>() { 1, 2, 3 };
            var numbersSet = new HashSet<int>() { 1, 2, 3 };
            var numbersArrayList = new ArrayList() { 1, 2, 3 };
            var numbersBson = new BsonArray(numbersArray.Select(x => new BsonValue(x)));

            TestExpr<User>(x => numbersArray.Contains(x.Id), "@p0 ANY = _id", numbersBson);
            TestExpr<User>(x => numbersList.Contains(x.Id), "@p0 ANY = _id", numbersBson);
            TestExpr<User>(x => numbersSet.Contains(x.Id), "@p0 ANY = _id", numbersBson);
            TestExpr<User>(x => numbersArrayList.Contains(x.Id), "@p0 ANY = _id", numbersBson);
        }

        [Fact]
        public void Linq_Nullables()
        {
            TestExpr<User>(x => x.Latitude, "Latitude");
            TestExpr<User>(x => x.Latitude + 200, "(Latitude + @p0)", 200);
            TestExpr<User>(x => x.Latitude.Value + 200, "(Latitude + @p0)", 200);

            TestPredicate<User>(x => x.Latitude > 0, "(Latitude > @p0)", 0);

            TestPredicate<User>(x => x.Latitude != null && x.Latitude > 0, "((Latitude != @p0) AND (Latitude > @p1))", BsonValue.Null, 0);
            TestPredicate<User>(x => x.Latitude.HasValue && x.Latitude > 0, "((((IS_NULL(Latitude) = false)) = true) AND (Latitude > @p0))", 0);
        }

        [Fact]
        public void Linq_Cast_Convert_Types()
        {
            // cast only fromType Double/Decimal to Int32/64

            // int cast/convert/parse
            TestExpr<User>(x => (int)x.Salary, "INT32(Salary)");
            TestExpr<User>(x => (int)x.Salary, "INT32(Salary)");
            TestExpr<User>(x => (double)x.Id, "_id");
            TestExpr(x => Convert.ToInt32("123"), "INT32(@p0)", "123");
            TestExpr(x => Int32.Parse("123"), "INT32(@p0)", "123");
        }

        [Fact]
        public void Linq_Methods()
        {
            // string instance methods
            TestExpr<User>(x => x.Name.ToUpper(), "UPPER(Name)");
            TestExpr<User>(x => x.Name.Substring(10), "SUBSTRING(Name, @p0)", 10);
            TestExpr<User>(x => x.Name.Substring(10, 20), "SUBSTRING(Name, @p0, @p1)", 10, 20);
            TestExpr<User>(x => x.Name.Replace('+', '-'), "REPLACE(Name, @p0, @p1)", "+", "-");
            TestExpr<User>(x => x.Name.IndexOf("m"), "INDEXOF(Name, @p0)", "m");
            TestExpr<User>(x => x.Name.IndexOf("m", 20), "INDEXOF(Name, @p0, @p1)", "m", 20);
            TestExpr<User>(x => x.Name.ToUpper().Trim(), "TRIM(UPPER(Name))");
            TestExpr<User>(x => x.Name.ToUpper().Trim().PadLeft(5, '0'), "LPAD(TRIM(UPPER($.Name)), @p0, @p1)", 5, "0");

            // string LIKE
            TestExpr<User>(x => x.Name.StartsWith("Mauricio"), "Name LIKE (@p0 + '%')", "Mauricio");
            TestExpr<User>(x => x.Name.Contains("Bezerra"), "Name LIKE ('%' + @p0 + '%')", "Bezerra");
            TestExpr<User>(x => x.Name.EndsWith("David"), "Name LIKE ('%' + @p0)", "David");
            TestExpr<User>(x => x.Name.StartsWith(x.Address.Street), "Name LIKE (Address.Street + '%')");

            // Equals
            TestExpr<User>(x => x.Name.Equals("John"), "Name = @p0", "John");
            TestExpr<User>(x => x.CreatedOn.Equals(new DateTime(2019, 1, 1)), "CreatedOn = DATETIME(@p0, @p1, @p2)", 2019, 1, 1);
            TestExpr<Account>(x => x.Id.Equals(Guid.Empty), $"_id = GUID(\"{Guid.Empty.ToString()}\")");
            TestExpr<Product>(x => x.Id.Equals(ObjectId.Empty), $"_id = OBJECTID(\"{ObjectId.Empty.ToString()}\")");
            TestExpr<User>(x => x.Salary.Equals(2000.0D), "Salary = @p0", 2000.0D);

            // string members
            TestExpr<User>(x => x.Address.Street.Length, "LENGTH(Address.Street)");
            TestExpr(x => string.Empty, "''");

            // string static methods
            TestExpr<User>(x => string.IsNullOrEmpty(x.Name), "(LENGTH(Name) = 0)");
            TestExpr<User>(x => string.IsNullOrWhiteSpace(x.Name), "(LENGTH(TRIM(Name)) = 0)");

            // guid initialize/converter
            TestExpr<User>(x => Guid.NewGuid(), "GUID()");
            TestExpr<User>(x => Guid.Empty, "GUID('00000000-0000-0000-0000-000000000000')");
            TestExpr<User>(x => Guid.Parse("1A3B944E-3632-467B-A53A-206305310BAC"), "GUID(@p0)", "1A3B944E-3632-467B-A53A-206305310BAC");

            // toString/Format
            TestExpr<User>(x => x.Id.ToString(), "STRING(_id)");
            TestExpr<User>(x => x.CreatedOn.ToString(), "STRING(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.ToString("yyyy-MM-dd"), "FORMAT(CreatedOn, @p0)", "yyyy-MM-dd");
            TestExpr<User>(x => x.Salary.ToString("#.##0,00"), "FORMAT(Salary, @p0)", "#.##0,00");

            // adding dates
            TestExpr<User>(x => x.CreatedOn.AddYears(5), "DATEADD('y', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddMonths(5), "DATEADD('M', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddDays(5), "DATEADD('d', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddHours(5), "DATEADD('h', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddMinutes(5), "DATEADD('m', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddSeconds(5), "DATEADD('s', @p0, CreatedOn)", 5);

            // using dates properties
            TestExpr<User>(x => x.CreatedOn.Year, "YEAR(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Month, "MONTH(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Day, "DAY(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Hour, "HOUR(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Minute, "MINUTE(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Second, "SECOND(CreatedOn)");

            TestExpr<User>(x => x.CreatedOn.Date, "DATETIME(YEAR(CreatedOn), MONTH(CreatedOn), DAY(CreatedOn))");

            TestExpr<User>(x => x.CreatedOn.ToUniversalTime(), "TO_UTC(CreatedOn)");

            // static date
            TestExpr<User>(x => DateTime.Now, "NOW()");
            TestExpr<User>(x => DateTime.UtcNow, "NOW_UTC()");
            TestExpr<User>(x => DateTime.Today, "TODAY()");

            // using regex
            TestExpr<User>(x => Regex.Split(x.Name, @"\s"), "SPLIT(Name, @p0, true)", @"\s");
            TestExpr<User>(x => Regex.IsMatch(x.Name, @"^K"), "IS_MATCH(Name, @p0)", @"^K");

        }

        [Fact]
        public void Linq_Dictionary_Index_Access()
        {
            // index value will be evaluate when "get_Item" method call
            TestExpr<User>(x => x.Phones[0].Number, "Phones[0].Number");
            TestExpr<User>(x => x.MetaData["KeyLocal"], "$.MetaData.KeyLocal");

            TestExpr<User>(x => x.MetaData["Key Local"].Street, "$.MetaData.['Key Local'].Street");
        }

        [Fact]
        public void Linq_New_Instance()
        {
            // new class
            TestExpr<User>(x => new { x.Name, x.Address }, "{ Name, Address }");
            TestExpr<User>(x => new { N = x.Name, A = x.Address }, "{ N: $.Name, A: $.Address }");

            // new array
            TestExpr<User>(x => new int[] { x.Id, 6, 7 }, "[_id, @p0, @p1]", 6, 7);

            // new fixed types
            TestExpr(x => new DateTime(2018, 5, 28), "DATETIME(@p0, @p1, @p2)", 2018, 5, 28);

            TestExpr(x => new Guid("1A3B944E-3632-467B-A53A-206305310BAC"), "GUID(@p0)", "1A3B944E-3632-467B-A53A-206305310BAC");

            // new instances with initializers
            TestExpr<User>(x => new User { Id = 1, Active = false }, "{ _id: @p0, Active: @p1 }", 1, false);

            // used in UpdateMany extend document
            TestExpr<User>(x => new User { Name = x.Name.ToUpper(), Salary = x.Salary * 2 }, "{ Name: UPPER($.Name), Salary: ($.Salary * @p0) }", 2);
        }

        [Fact]
        public void Linq_Composite_Key()
        {
            // using composite key new class initializer
            TestExpr<User>(x => x.Address == new Address { Number = 555 }, "(Address = { Number: @p0 })", 555);

            // using 2 levels
            TestExpr<User>(x => x.Address == new Address { Number = 1, City = new City { Country = "BR", CityName = "POA" } },
                "(Address = { Number: @p0, City: { Country: @p1, CityName: @p2 } })", 1, "BR", "POA");
        }

        [Fact]
        public void Linq_Coalesce()
        {
            TestExpr<User>(x => x.DomainName ?? x.Name, "COALESCE(USER_DOMAIN_NAME, Name)");

            TestExpr<City>(x => (x.CityName ?? x.Country) == DateTime.Now.Year.ToString(),
                "(COALESCE(CityName, Country) = STRING(YEAR(NOW())))");
        }

        [Fact]
        public void Linq_DbRef()
        {
            TestExpr<Order>(x => x.Customer.CustomerId == 123, "(Customer.$id = @p0)", 123);
            TestExpr<Order>(x => x.Customer.Name == "John", "(Customer.Name = @p0)", "John");

            TestExpr<Order>(x => x.Users.Select(u => u.Id).Any(id => id == 9), "MAP(Users => @.$id) ANY = @p0", 9);
            TestExpr<Order>(x => x.Users.Select(u => u.Name).Any(n => n == "U1"), "MAP(Users => @.Name) ANY = @p0  ", "U1");
        }

        [Fact]
        public void Linq_Complex_Expressions()
        {
            _mapper.EnumAsInteger = true;
            TestExpr<User>(x => new
            {
                CityName = x.Address.City.CityName,
                Count = x.Phones.Where(p => p.Type == PhoneType.Landline).Count(),
                List = x.Phones.Where(p => p.Number > x.Salary).Select(p => p.Number).ToArray()
            },
                @"
            {
                CityName: $.Address.City.CityName,
                Count: COUNT(FILTER($.Phones=>(@.Type=@p0))),
                List: ARRAY(MAP(FILTER($.Phones=>(@.Number>$.Salary))=>@.Number))
            }",
                (int)PhoneType.Landline);
        }

        [Fact]
        public void Linq_Enum_Expressions()
        {
            _mapper.EnumAsInteger = false;
            TestExpr<Phone>(p => p.Type == PhoneType.Landline, "($.Type=@p0)", PhoneType.Landline.ToString());

            _mapper.EnumAsInteger = true;
            TestExpr<Phone>(p => p.Type == PhoneType.Landline, "($.Type=@p0)", (int)PhoneType.Landline);
        }

        [Fact]
        public void Linq_BsonDocument_Navigation()
        {
            TestExpr<BsonValue>(x => x["name"].AsString, "$.name");
            TestExpr<BsonValue>(x => x["first"]["name"], "$.first.name");
            TestExpr<BsonValue>(x => x["arr"][0]["demo"], "$.arr[0].demo");
            TestExpr<BsonValue>(x => x["age"] == 1, "($.age = @p0)", 1);
        }

        [Fact]
        public void Linq_BsonDocument_Predicate()
        {
            TestPredicate<BsonValue>(x => x["age"] == 1, "($.age = @p0)", 1);
        }

        [Fact]
        public void Linq_Custom_Field_Name()
        {
            // first use
            TestExpr<User>(x => x.DomainName, "$.USER_DOMAIN_NAME");

            // in creation new class
            TestExpr<User>(x => new { x.DomainName }, "{ DomainName: $.USER_DOMAIN_NAME }");
        }

        [Fact]
        public void Linq_Array_Contains()
        {
            var ids = new int[] { 1, 2, 3 };
            //var ids = new List<int> { 1, 2, 3 }; // works too

            // the result are correct, but can be optimize (in QueryOptimzier) to `$._id IN @p0` (index will be used)
            TestExpr<User>(x => ids.Contains(x.Id), "@p0 ANY = $._id", new BsonArray { 1, 2, 3 });

            TestExpr<User>(x => ids.Where(q => q == x.Id).Count() > 0, "(COUNT(FILTER(@p0 => (@=$._id))) > @p1)", new BsonArray { 1, 2, 3 }, 0);
        }

        [Fact]
        public void Linq_Array_Any()
        {
            TestExpr<User>(x => x.Phones.Any(), "COUNT($.Phones) > 0");
        }

        [Fact]
        public void Linq_InvocationExpression()
        {
            Expression<Func<User, bool>> expr = x => x.Id >= 1 && x.Id <= 10;
            Expression<Func<User, bool>> exprLeft = x => x.Id >= 1;
            Expression<Func<User, bool>> exprRight = x => x.Id <= 10;

            var invokedExprRight = Expression.Invoke(exprRight, exprRight.Parameters.Cast<Expression>());

            Expression<Func<User, bool>> exprMerged = Expression.Lambda<Func<User, bool>>
                  (Expression.AndAlso(exprLeft.Body, invokedExprRight), exprLeft.Parameters);

            Test<User, bool>(expr, "(($._id>=@p0) AND ($._id<=@p1))", 1, 10);
            Test<User, bool>(exprMerged, "(($._id>=@p0) AND (((@._id<=@p1))=true))", 1, 10);
            //the right expr of exprMerged uses @ (instead of $) because the rootParameter is different for exprLeft and exprRight
        }

        #region Test helper

        /// <summary>
        /// Compare LINQ expression with expected BsonExpression
        /// </summary>
        [DebuggerHidden]
        private BsonExpression Test<T, K>(Expression<Func<T, K>> expr, BsonExpression expect, params BsonValue[] args)
        {
            var expression = _mapper.GetExpression(expr);

            expression.Source.Should().Be(expect.Source);

            expression.Parameters.Keys.Count.Should().Be(args.Length, "Number of parameter are different than expected");

            var index = 0;

            foreach (var par in args)
            {
                var pval = expression.Parameters["p" + (index++).ToString()];

                pval.Should().Be(par, $"Expression: {expect.Source}");
            }

            return expression;
        }

        [DebuggerHidden]
        private BsonExpression TestExpr(Expression<Func<object, object>> expr, BsonExpression expect, params BsonValue[] args)
        {
            return this.Test<object, object>(expr, expect, args);
        }

        [DebuggerHidden]
        private BsonExpression TestExpr<T>(Expression<Func<T, object>> expr, BsonExpression expect, params BsonValue[] args)
        {
            return this.Test<T, object>(expr, expect, args);
        }

        [DebuggerHidden]
        private BsonExpression TestPredicate<T>(Expression<Func<T, bool>> expr, BsonExpression expect, params BsonValue[] args)
        {
            return this.Test<T, bool>(expr, expect, args);
        }

        /// <summary>
        /// Execute test but expect an exception
        /// </summary>
        [DebuggerHidden]
        private void TestException<T, TException>(Func<Expression<Func<T, object>>> fn)
            where TException : Exception
        {
            var test = fn();

            this.Invoking(x => this.TestExpr<T>(test, "$")).Should().Throw<TException>();
        }

        #endregion
    }
}