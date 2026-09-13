using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class PredicateBuilder_Tests
    {
        #region Model

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public bool Active { get; set; }
            public int Age { get; set; }
        }

        #endregion

        [Fact(Skip = "Need review")]
        public void Usage_PredicateBuilder()
        {
            var p = PredicateBuilder.True<User>();

            p = p.And(x => x.Active);
            p = p.And(x => x.Age > 10);

            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var col = db.GetCollection<User>("user");

                col.Insert(new User { Active = true, Age = 11, Name = "user" });

                // using direct Expression
                var r1 = col.FindOne(x => x.Active && x.Age > 10);
                r1.Name.Should().Be("user");

                // using same expression but now with PredicateBuilder
                var r2 = col.FindOne(p);
                r2.Name.Should().Be("user");
            }
        }
    }

    // from http://www.albahari.com/nutshell/predicatebuilder.aspx
    public static class PredicateBuilder
    {
        public static Expression<Func<T, bool>> True<T>() { return f => true; }
        public static Expression<Func<T, bool>> False<T>() { return f => false; }

        public static Expression<Func<T, bool>> Or<T>(this Expression<Func<T, bool>> expr1,
                                                            Expression<Func<T, bool>> expr2)
        {
            var invokedExpr = Expression.Invoke(expr2, expr1.Parameters.Cast<Expression>());
            return Expression.Lambda<Func<T, bool>>
                  (Expression.OrElse(expr1.Body, invokedExpr), expr1.Parameters);
        }

        public static Expression<Func<T, bool>> And<T>(this Expression<Func<T, bool>> expr1,
                                                             Expression<Func<T, bool>> expr2)
        {
            var invokedExpr = Expression.Invoke(expr2, expr1.Parameters.Cast<Expression>());
            return Expression.Lambda<Func<T, bool>>
                  (Expression.AndAlso(expr1.Body, invokedExpr), expr1.Parameters);
        }
    }
}