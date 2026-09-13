using System;
using System.Linq;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqEval_Tests
    {
        #region Model

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public DateTime Date { get; set; }
            public bool Active { get; set; }
            public Guid Ticket { get; set; }

            public Address Address { get; set; }
            public List<Phone> Phones { get; set; }
        }

        public class Address
        {
            public string Street { get; set; }
            public int Number { get; set; }
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

        #endregion

        private readonly BsonMapper _mapper = new BsonMapper();

        [Fact]
        public void Linq_Date_Eval()
        {
            // remove milliseconds from now (BSON format do not support milliseconds)
            var now = DateTime.Parse(DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss"));

            var u = new User {Date = now};

            // date properties
            Eval(u, x => x.Date.Year, u.Date.Year);
            Eval(u, x => x.Date.Month, u.Date.Month);
            Eval(u, x => x.Date.Day, u.Date.Day);
            Eval(u, x => x.Date.Hour, u.Date.Hour);
            Eval(u, x => x.Date.Minute, u.Date.Minute);
            Eval(u, x => x.Date.Second, u.Date.Second);
            Eval(u, x => x.Date.Date, u.Date.Date);

            // date method
            Eval(u, x => x.Date.AddYears(5), u.Date.AddYears(5));
            Eval(u, x => x.Date.AddMonths(5), u.Date.AddMonths(5));
            Eval(u, x => x.Date.AddDays(5), u.Date.AddDays(5));
            Eval(u, x => x.Date.AddHours(5), u.Date.AddHours(5));
            Eval(u, x => x.Date.AddMinutes(5), u.Date.AddMinutes(5));
            Eval(u, x => x.Date.AddSeconds(5), u.Date.AddSeconds(5));

            // date Now/Today
            Eval(u, x => x.Date.Year + DateTime.Now.Year, u.Date.Year + DateTime.Now.Year);
            Eval(u, x => x.Date.Year + DateTime.UtcNow.Year, u.Date.Year + DateTime.UtcNow.Year);
            Eval(u, x => x.Date.Year + DateTime.Today.Year, u.Date.Year + DateTime.Today.Year);
        }

        [Fact]
        public void Linq_Predicate_Eval()
        {
            var u = new User {Id = 1, Active = false};

            Eval(u, x => x.Id == 1, true);
            Eval(u, x => x.Id != 1, false);

            Eval(u, x => x.Id > 1, false);
            Eval(u, x => x.Id >= 1, true);

            Eval(u, x => x.Id < 1, false);
            Eval(u, x => x.Id <= 1, true);

            Eval(u, x => x.Id == 2 || !x.Active, true);
            Eval(u, x => x.Id == 2 && x.Active == true, false);
            Eval(u, x => x.Id == 2 && x.Active == true, false);

            // unary
            Eval(u, x => x.Active, false);
            Eval(u, x => !x.Active, true);
            Eval(u, x => !x.Active && !x.Active, true);
            Eval(u, x => x.Active || !x.Active, true);
        }

        [Fact]
        public void Linq_Document_Navigation_Eval()
        {
            var u = new User {Id = 1, Name = "John", Address = new Address {Number = 123, Street = "Ipiranga"}};

            // return root $
            Eval(u, x => x, u);

            // return property
            Eval(u, x => x.Address, u.Address);
            Eval(u, x => x.Address.Street, u.Address.Street);

            // checks "Name: " will not apply "Trim" as default option in BsonMapper
            Eval(u, x => "Name: " + x.Name, "Name: John");
        }

        [Fact]
        public void Linq_Math_Eval()
        {
            var u = new User {Id = 5};

            Eval(u, x => u.Id + 10 * 2, 25);
            Eval(u, x => (u.Id + 10) * 2, 30);

            Eval(u, x => Math.Abs(u.Id - 20), 15);
            Eval(u, x => Math.Round((double) u.Id / 3, 2), 1.67);
        }

        [Fact]
        public void Linq_Array_Navigation_Eval()
        {
            var u = new User
            {
                Phones = new List<Phone>
                {
                    new Phone {Number = 1, Prefix = 10, Type = PhoneType.Mobile},
                    new Phone {Number = 2, Prefix = 20, Type = PhoneType.Mobile},
                    new Phone {Number = 3, Prefix = 30, Type = PhoneType.Landline},
                }
            };

            // get full document inside
            //** Eval(u, x => x.Phones[0], u.Phones[0]);
            //** 
            //** Eval(u, x => x.Phones.Items().Prefix, 10, 20, 30);
            //** Eval(u, x => x.Phones.Items(0).Number, 1);
            //** Eval(u, x => x.Phones.Items(-1).Number, 3);
            //** Eval(u, x => x.Phones.Items(z => z.Prefix >= 20).Number, 2, 3);
        }

        /// <summary>
        /// Eval expression and check with expected
        /// </summary>
        [DebuggerHidden]
        private void Eval<T, K>(T entity, Expression<Func<T, K>> expr, params K[] expect)
        {
            var expression = _mapper.GetExpression(expr);
            var doc = _mapper.ToDocument<T>(entity);

            var results = expression.Execute(doc).ToArray();
            var index = 0;

            results.Length.Should().Be(expect.Length);

            foreach (var result in results)
            {
                var exp = _mapper.Serialize(typeof(K), expect[index++]);

                result.Should().Be(exp);
            }
        }
    }
}