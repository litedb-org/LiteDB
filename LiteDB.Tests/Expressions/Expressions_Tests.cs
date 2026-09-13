using FluentAssertions;
using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class Expressions_Tests
    {
        [Fact]
        public void Expressions_Constants()
        {
            BsonValue K(string s)
            {
                return BsonExpression.Create(s).ExecuteScalar();
            }

            K(@"123").ExpectValue(123);
            K(@"null").ExpectValue(BsonValue.Null);
            K(@"15.9").ExpectValue(15.9);
            K(@"true").ExpectValue(true);
            K(@"false").ExpectValue(false);
            K(@"'my string'").ExpectValue("my string");
            K(@"""my string""").ExpectValue("my string");
            K(@"[1,2]").ExpectArray(1, 2);
            K(@"[]").ExpectJson("[]");
            K(@"{a:1}").ExpectJson("{a:1}");
            K(@"{a:true,i:0}").ExpectJson("{a:true,i:0}");
        }

        [Fact]
        public void Expression_Fields()
        {
            IEnumerable<string> F(string s)
            {
                return BsonExpression.Create(s).Fields;
            }

            // simple case
            F("$.Name").ExpectValues("Name");

            F("JustName").ExpectValues("JustName");
            F("$.[\"My First Name\"]").ExpectValues("My First Name");

            // only root field
            F("$.Name.First").ExpectValues("Name");
            F("$.Items[*].Type").ExpectValues("Items");

            // inside new document/array
            F("{ Active, _id }").ExpectValues("Active", "_id");
            F("{ Active, _id: 1 }").ExpectValues("Active");
            F("[ Active, _id, null, UPPER(Name.First)]").ExpectValues("Active", "_id", "Name");

            // no fields
            F("{ Active: 1, _id: 2 }").ExpectCount(0);
            F("123").ExpectCount(0);
            F("UPPER(@p0) = 'JOHN' OR YEAR(NOW()) = 2018").ExpectCount(0);

            // duplicate 
            F("{ Active: active, NewActive: active, Root: $ }").ExpectValues("active", "$");

            // case insensitive (only first field is return)
            F("{ Active: active, NewActive: ACTIVE }").ExpectValues("active");

            // with no root in array
            F("Items[0].Type = Age").ExpectValues("Items", "Age");

            // with root and MAP :: ($.Items[$.Root = 1] => @.Type) = $.Age
            F("Items[$.Root = 1].Type all = Age").ExpectValues("Items", "Root", "Age");

            // predicate + method
            F("_id = Age + YEAR(DATETIME(2000, 1, DAY(NewField))) AND UPPER(TRIM(Name)) = @0")
                .ExpectValues("_id", "Age", "NewField", "Name");

            // using root document
            F("$").ExpectValues("$");
            F("$ + _id").ExpectValues("$", "_id");

            // fields when using source (do simplify, when use * is same as $)
            F("*").ExpectValues("$");
            F("*._id").ExpectValues("_id");
            F("FIRST(MAP(* => (@.abc + $.name))) + _id").ExpectValues("$", "abc", "name", "_id");
        }

        [Fact]
        public void Expression_Immutable()
        {
            bool I(string s)
            {
                return BsonExpression.Create(s).IsImmutable;
            }

            // some immutable expression
            I("_id").ExpectValue(true);
            I("{ a: 1, n: UPPER(name) }").ExpectValue(true);
            I("GUID('00000000-0000-0000-0000-000000000000')").ExpectValue(true);

            // using method that are not immutable 
            I("_id + DAY(NOW())").ExpectValue(false);
            I("r + 10 > 10 AND GUID() = true").ExpectValue(false);
            I("r + 10 > 10 AND Name LIKE OBJECTID() + '%'").ExpectValue(false);
            I("_id > @0").ExpectValue(false);
        }

        [Fact]
        public void Expression_Type()
        {
            BsonExpressionType T(string s)
            {
                return BsonExpression.Create(s).Type;
            }

            T("1").ExpectValue(BsonExpressionType.Int);
            T("-1").ExpectValue(BsonExpressionType.Int);
            T("1.1").ExpectValue(BsonExpressionType.Double);
            T("-1.1").ExpectValue(BsonExpressionType.Double);
            T("''").ExpectValue(BsonExpressionType.String);
            T("null").ExpectValue(BsonExpressionType.Null);
            T("[ ]").ExpectValue(BsonExpressionType.Array);
            T("{ }").ExpectValue(BsonExpressionType.Document);
            T("true").ExpectValue(BsonExpressionType.Boolean);
            T("false").ExpectValue(BsonExpressionType.Boolean);

            T("@p0").ExpectValue(BsonExpressionType.Parameter);
            T("UPPER(@p0)").ExpectValue(BsonExpressionType.Call);

            T("1 + 1").ExpectValue(BsonExpressionType.Add);
            T("1 - 1").ExpectValue(BsonExpressionType.Subtract);
            T("1 * 1").ExpectValue(BsonExpressionType.Multiply);
            T("1 / 1").ExpectValue(BsonExpressionType.Divide);

            // math order
            T("1 + 1 / 3").ExpectValue(BsonExpressionType.Add);
            T("(1 + 1) / 3").ExpectValue(BsonExpressionType.Divide);

            // predicate
            T("1 = 1").ExpectValue(BsonExpressionType.Equal);
            T("1 > 1").ExpectValue(BsonExpressionType.GreaterThan);
            T("1 >= 1").ExpectValue(BsonExpressionType.GreaterThanOrEqual);
            T("1 < 1").ExpectValue(BsonExpressionType.LessThan);
            T("1 <= 1").ExpectValue(BsonExpressionType.LessThanOrEqual);
            T("'JOHN' LIKE 'J%'").ExpectValue(BsonExpressionType.Like);
            T("1 BETWEEN 0 AND 1").ExpectValue(BsonExpressionType.Between);
            T("1 IN [1,2]").ExpectValue(BsonExpressionType.In);
            T("1 != 1").ExpectValue(BsonExpressionType.NotEqual);

            T("1=1 OR 1=2").ExpectValue(BsonExpressionType.Or);
            T("2=1 AND 1=2").ExpectValue(BsonExpressionType.And);

            T("*").ExpectValue(BsonExpressionType.Source);

            // maps
            T("MAP(arr[*] => @)").ExpectValue(BsonExpressionType.Map);
            T("MAP(el.arr[*] => @)").ExpectValue(BsonExpressionType.Map);
            T("MAP(el.arr[*] => (@ + 10 + UPPER(@)))").ExpectValue(BsonExpressionType.Map);

            // shortcut
            T("arr[*].price").ExpectValue(BsonExpressionType.Map);
            T("*._id").ExpectValue(BsonExpressionType.Map);
        }

        [Fact]
        public void Expression_Format()
        {
            string F(string s)
            {
                return BsonExpression.Create(s).Source;
            }

            F("_id").ExpectValue("$._id");

            // Expression format
            F("_id").ExpectValue("$._id");
            F("a.b").ExpectValue("$.a.b");
            F("a[ @ + 1 = @ + 2].b").ExpectValue("MAP($.a[@+1=@+2]=>@.b)");
            F("a.['a-b']").ExpectValue("$.a.[\"a-b\"]");
            F("'single \"quote\\\' string'").ExpectValue("\"single \\\"quote' string\"");
            F("\"double 'quote\\\" string\"").ExpectValue("\"double 'quote\\\" string\"");
            F("{'a-b':1, \"x + 1\": 2, 'y': 3}").ExpectValue("{\"a-b\":1,\"x + 1\":2,y:3}");
            F("[1, 2 ,  { $guid : \"826944a6-72ec-4fc0-a1bc-9fd9f846c266\" }]")
                .ExpectValue("[1,2,{$guid:\"826944a6-72ec-4fc0-a1bc-9fd9f846c266\"}]");

            // And/Or
            F("1 =  true   and false > \"A\"").ExpectValue("1=true AND false>\"A\"");
            F("1 < 1 or \"two\" = \"two\" or three > three").ExpectValue("1<1 OR \"two\"=\"two\" OR $.three>$.three");
            F("( 1 + 2) = 3    and X  +  y = 9").ExpectValue("(1+2)=3 AND $.X+$.y=9");

            // Methods
            F("SUBSTRING( \"lite\" + \"db\", -4, 1 + 9 )").ExpectValue("SUBSTRING(\"lite\"+\"db\",-4,1+9)");

            // Array
            F("[a,b, 1, true , [ null, { \"x\" : 99 }] ]").ExpectValue("[$.a,$.b,1,true,[null,{x:99}]]");

            // Inner
            F("(10 * (1 + 2) - 5)").ExpectValue("(10*(1+2)-5)");

            // Map
            F("MAP(names[length(@) > 10] => upper(@))").ExpectValue("MAP($.names[LENGTH(@)>10]=>UPPER(@))");

            // Path/Source-Map
            F("items[*].id").ExpectValue("MAP($.items[*]=>@.id)");
            F("items[*].products[*].price").ExpectValue("MAP($.items[*]=>MAP(@.products[*]=>@.price))");
            F("sum(items[*].price  ) + 3").ExpectValue("SUM(MAP($.items[*]=>@.price))+3");

            // any/all
            F("items[*].id any=5").ExpectValue("MAP($.items[*]=>@.id) ANY=5");
            F("items[id > 99].id all between 5 and  'go'").ExpectValue("MAP($.items[@.id>99]=>@.id) ALL BETWEEN 5 AND \"go\"");

            // parameters
            F("items[ @0 ].price = 9").ExpectValue("$.items[@0].price=9");

            // double vs int
            F("5 % 3").ExpectValue("5%3");
            F("5.0 % 3").ExpectValue("5.0%3");
            F("5.00 % 3").ExpectValue("5.0%3");
            F("5.001 % 3").ExpectValue("5.001%3");

        }

        [Fact]
        public void Invalid_Expressions()
        {
            void Err(string s)
            {
                Assert.Throws<LiteException>(() =>
                {
                    var expr = BsonExpression.Create(s);
                });
            }

            Err("5 FOO < 1");
            Err("8 ++ 9");
            Err("10 + 5)");
            Err("10 + 5 -");
            Err("MAP(A => +)");
            Err("(25 + 15");
            Err("10 + 5 -.");
        }
    }
}