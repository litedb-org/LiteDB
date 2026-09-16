using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using LiteDB;

namespace LiteDB.Security.FrameworkTests
{
    public static class Program
    {
        public class Envelope
        {
            public object Value { get; set; }
            public Dictionary<string, object> Map { get; set; }
            public object[] Items { get; set; }
        }

        public class BaseEntity { public string Name { get; set; } }
        public class DerivedEntity : BaseEntity { public int Number { get; set; } }
        public class Container { public BaseEntity Value { get; set; } }

        private sealed class AllowListBinder : ITypeNameBinder
        {
            public string GetName(Type type)
            {
                if (type == typeof(DerivedEntity)) return "approved";
                throw new InvalidOperationException("Unregistered type");
            }

            public Type GetType(string name)
            {
                return name == "approved" ? typeof(DerivedEntity) : null;
            }
        }

        public static int Main(string[] args)
        {
            try
            {
                Check(args.Length == 1 && Environment.Version.Major == int.Parse(args[0]), "Wrong CLR runtime");
                var identity = typeof(BsonMapper).Assembly.GetName();
                Check(identity.Version.ToString() == "4.1.5.0", "Wrong assembly version");
                Check(BitConverter.ToString(identity.GetPublicKeyToken()) == "4E-E4-01-23-01-3C-9F-27", "Wrong signing key");
                Console.WriteLine(identity.FullName + " on CLR " + Environment.Version);

                foreach (var type in new[] { typeof(Process), typeof(ProcessStartInfo), typeof(ArrayList) })
                {
                    for (var shape = 0; shape < 4; shape++) CheckBlocked(type, shape);
                }
                CheckRoundTrip(false);
                CheckRoundTrip(true);
                CheckAssignability(false);
                CheckAssignability(true);
                var restricted = new BsonMapper { TypeNameBinder = new AllowListBinder() };
                ExpectError(() => restricted.ToObject<object>(new BsonDocument
                    { ["_type"] = typeof(DerivedEntity).AssemblyQualifiedName }), LiteException.INVALID_TYPED_NAME);
                Console.WriteLine("17 framework security and compatibility checks passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void CheckBlocked(Type unsafeType, int shape)
        {
            var attempted = false;
            var mapper = new BsonMapper(type =>
            {
                if (type == unsafeType)
                {
                    attempted = true;
                    throw new InvalidOperationException("Unsafe instantiator reached");
                }
                return Activator.CreateInstance(type);
            });
            var payload = new BsonDocument { ["_type"] = unsafeType.AssemblyQualifiedName };
            Action deserialize;
            switch (shape)
            {
                case 0: deserialize = () => mapper.ToObject<object>(payload); break;
                case 1: deserialize = () => mapper.ToObject<Envelope>(new BsonDocument { ["Value"] = payload }); break;
                case 2: deserialize = () => mapper.ToObject<Envelope>(new BsonDocument
                    { ["Map"] = new BsonDocument { ["entry"] = payload } }); break;
                default: deserialize = () => mapper.ToObject<Envelope>(new BsonDocument
                    { ["Items"] = new BsonArray { payload } }); break;
            }
            ExpectError(deserialize, unsafeType == typeof(Process) ? 215 : 218);
            Check(!attempted, "Unsafe instantiator was called");
        }

        private static void CheckRoundTrip(bool custom)
        {
            var mapper = new BsonMapper();
            if (custom) mapper.TypeNameBinder = new AllowListBinder();
            var document = mapper.ToDocument(new Container { Value = new DerivedEntity { Name = "safe", Number = 42 } });
            var expectedName = custom ? "approved" : typeof(DerivedEntity).FullName + ", " + typeof(DerivedEntity).Assembly.GetName().Name;
            Check(document["Value"].AsDocument["_type"].AsString == expectedName, "Discriminator changed");
            var value = (DerivedEntity)mapper.ToObject<Container>(document).Value;
            Check(value.Name == "safe" && value.Number == 42, "Round trip failed");
        }

        private static void CheckAssignability(bool custom)
        {
            var attempted = false;
            var mapper = new BsonMapper(type => { attempted = true; throw new InvalidOperationException(); });
            if (custom) mapper.TypeNameBinder = new AllowListBinder();
            ExpectError(() => mapper.ToObject<Container>(new BsonDocument
            {
                ["_type"] = custom ? "approved" : typeof(DerivedEntity).AssemblyQualifiedName
            }), LiteException.DATA_TYPE_NOT_ASSIGNABLE);
            Check(!attempted, "Unassignable instantiator was called");
        }

        private static void ExpectError(Action action, int code)
        {
            try { action(); }
            catch (LiteException error)
            {
                Check(error.ErrorCode == code, "Wrong rejection code: " + error.ErrorCode);
                return;
            }
            throw new InvalidOperationException("Expected rejection " + code);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
