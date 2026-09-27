namespace LiteDB.Fuzz.Targets;

internal sealed class MapperFuzzer : IFuzzTarget
{
    public string Name => "mapper";
    public string Description => "Object/BSON round trips for nullable, enum, collection, constructor, inheritance, and cyclic shapes.";

    public Task RunAsync(FuzzContext context)
    {
        var mapper = new BsonMapper { EnumAsInteger = (context.Seed & 1) == 0 };
        var cycleFailures = 0;
        while (context.Next())
        {
            var expected = Model(context.Random, context.Steps);
            var document = mapper.ToDocument(expected);
            var normalized = ExpectedDocument(expected, mapper.EnumAsInteger);
            context.Check(document.Equals(normalized),
                "Mapper serialization differed from the independent normalized BSON tree.");
            var actual = mapper.ToObject<FuzzModel>(document);
            var roundTrip = mapper.ToDocument(actual);
            context.Check(BsonSerializer.Serialize(document).SequenceEqual(BsonSerializer.Serialize(roundTrip)),
                "Mapper round trip changed the BSON representation.");
            context.Check(actual.Id == expected.Id && actual.Optional == expected.Optional && actual.State == expected.State,
                "Mapper scalar round trip mismatch.");
            context.Check(actual.Names.SequenceEqual(expected.Names) && actual.Scores.OrderBy(x => x.Key).SequenceEqual(expected.Scores.OrderBy(x => x.Key)),
                "Mapper collection round trip mismatch.");
            context.Check(actual.Child?.Value == expected.Child?.Value && actual.Constructor.Value == expected.Constructor.Value,
                "Mapper nested/constructor round trip mismatch.");

            var boxed = (FuzzContract)new FuzzDerived { Name = "derived", Rank = context.Steps };
            var interfaceValue = mapper.Serialize(typeof(FuzzContract), boxed);
            context.Check(interfaceValue.IsDocument && interfaceValue.AsDocument["Name"].AsString == "derived",
                "Interface-typed serialization lost public members.");

            if (context.Steps % 9 == 0)
            {
                var cyclic = new CycleNode { Name = "cycle" };
                cyclic.Next = cyclic;
                var error = Error(() => mapper.Serialize(typeof(CycleNode), cyclic));
                context.Check(error != null, "Cyclic graph unexpectedly serialized without a bounded failure.");
                cycleFailures++;
                context.Check(mapper.ToDocument(expected)["_id"].AsInt32 == expected.Id,
                    "A cyclic mapping failure poisoned later mapper use.");
            }
            if (context.Steps % 7 == 0) VerifyAttributedAndCustomMapping(context, mapper);
            if (context.Steps % 11 == 0) VerifyUserFailureIsolation(context, mapper, expected);
            context.Trace("mapper", new { expected.Id, mapper.EnumAsInteger, hasChild = expected.Child != null });
        }
        context.Metrics["cycleFailures"] = cycleFailures;
        context.Metrics["enumAsInteger"] = mapper.EnumAsInteger;
        return Task.CompletedTask;
    }

    private static BsonDocument ExpectedDocument(FuzzModel value, bool enumAsInteger)
    {
        var document = new BsonDocument { ["_id"] = value.Id };
        if (value.Optional.HasValue) document["Optional"] = value.Optional.Value;
        document["State"] = enumAsInteger ? (int)value.State : value.State.ToString();
        document["Names"] = new BsonArray(value.Names.Select(name => new BsonValue(name)));
        var scores = new BsonDocument();
        foreach (var pair in value.Scores) scores[pair.Key] = pair.Value;
        document["Scores"] = scores;
        if (value.Child != null) document["Child"] = new BsonDocument
        {
            ["Value"] = value.Child.Value, ["When"] = value.Child.When
        };
        document["Constructor"] = new BsonDocument { ["Value"] = value.Constructor.Value };
        document["Values"] = new BsonArray(value.Values.Select(number => new BsonValue(number)));
        return document;
    }

    private static void VerifyAttributedAndCustomMapping(FuzzContext context, BsonMapper mapper)
    {
        mapper.RegisterType<CustomValue>(value => new BsonDocument { ["encoded"] = value.Value * 3 },
            value => new CustomValue(value["encoded"].AsInt32 / 3));
        var source = new AttributedModel(17, " visible ", new CustomValue(9)) { Ignored = 123 };
        var actual = mapper.ToDocument(source);
        var expected = new BsonDocument
        {
            ["_id"] = 17, ["stored_name"] = "visible",
            ["Custom"] = new BsonDocument { ["encoded"] = 27 }
        };
        context.Check(actual.Equals(expected),
            "Attributes/custom serializer differed from the independent BSON tree.");
        var decoded = mapper.ToObject<AttributedModel>(actual);
        context.Check(decoded.Id == 17 && decoded.Name == "visible" && decoded.Custom.Value == 9 && decoded.Ignored == 0,
            "Attributed/custom mapping decoded the wrong object tree.");
    }

    private static void VerifyUserFailureIsolation(FuzzContext context, BsonMapper mapper,
        FuzzModel valid)
    {
        var getter = Error(() => mapper.ToDocument(new ThrowingGetter()));
        context.Check(getter is InvalidOperationException || getter?.InnerException is InvalidOperationException,
            $"Throwing getter escaped through unexpected exception {getter?.GetType().FullName}.");
        mapper.RegisterType<ThrowingCustom>(_ => throw new ApplicationException("serializer"), _ => new ThrowingCustom());
        var serializer = Error(() => mapper.Serialize(typeof(ThrowingCustom), new ThrowingCustom()));
        context.Check(serializer is ApplicationException,
            "Throwing custom serializer did not preserve its exception type.");
        var constructor = Error(() => mapper.ToObject<ThrowingConstructor>(
            new BsonDocument { ["value"] = 1 }));
        context.Check(constructor is LiteException or InvalidOperationException ||
            constructor?.InnerException is InvalidOperationException,
            $"Throwing constructor escaped through unexpected exception {constructor?.GetType().FullName}.");
        using (var db = new LiteDatabase(new MemoryStream()))
        {
            var rows = db.GetCollection("throwing_enumerable");
            var enumerable = Error(() => rows.InsertBulk(ThrowingDocuments()));
            context.Check(enumerable is InvalidOperationException,
                "Throwing bulk enumerable did not preserve its exception type.");
            context.Check(rows.Count() == 0, "Throwing bulk enumerable partially published documents.");
            rows.Insert(new BsonDocument { ["_id"] = 3 });
            context.Check(rows.Count() == 1, "Bulk enumerable failure left the database unusable.");
        }
        context.Check(mapper.ToDocument(valid).Equals(ExpectedDocument(valid, mapper.EnumAsInteger)),
            "A user mapping exception poisoned subsequent serialization.");
    }

    private static IEnumerable<BsonDocument> ThrowingDocuments()
    {
        yield return new BsonDocument { ["_id"] = 1 };
        yield return new BsonDocument { ["_id"] = 2 };
        throw new InvalidOperationException("enumerable");
    }

    private static FuzzModel Model(Random random, int id) => new()
    {
        Id = id,
        Optional = random.Next(3) == 0 ? null : random.Next(-1000, 1000),
        State = (FuzzEnum)random.Next(3),
        Names = Enumerable.Range(0, random.Next(0, 8)).Select(i => "n" + random.Next(100) + i).ToList(),
        Scores = Enumerable.Range(0, random.Next(0, 8)).ToDictionary(i => "k" + i, _ => random.Next()),
        Child = random.Next(3) == 0 ? null : new FuzzChild
        {
            Value = random.Next(),
            When = DateTime.UnixEpoch.AddTicks(random.NextInt64(0, TimeSpan.TicksPerDay * 365L))
        },
        Constructor = new ConstructorOwned("v" + random.Next()),
        Values = Enumerable.Range(0, random.Next(0, 8)).Select(_ => (decimal)random.NextDouble()).ToArray()
    };

    private static Exception Error(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private enum FuzzEnum { Zero, One, Two }

    private sealed class FuzzModel
    {
        public int Id { get; set; }
        public int? Optional { get; set; }
        public FuzzEnum State { get; set; }
        public List<string> Names { get; set; }
        public Dictionary<string, int> Scores { get; set; }
        public FuzzChild Child { get; set; }
        public ConstructorOwned Constructor { get; set; }
        public decimal[] Values { get; set; }
    }

    private sealed class FuzzChild
    {
        public int Value { get; set; }
        public DateTime When { get; set; }
    }

    private sealed class ConstructorOwned
    {
        public ConstructorOwned(string value) => Value = value;
        public string Value { get; }
    }

    private interface FuzzContract { string Name { get; } }

    private sealed class FuzzDerived : FuzzContract
    {
        public string Name { get; set; }
        public int Rank { get; set; }
    }

    private sealed class CycleNode
    {
        public string Name { get; set; }
        public CycleNode Next { get; set; }
    }

    private sealed record CustomValue(int Value);

    private sealed class AttributedModel
    {
        [BsonCtor]
        public AttributedModel(int id, string name, CustomValue custom)
        {
            Id = id; Name = name; Custom = custom;
        }
        public int Id { get; }
        [BsonField("stored_name")]
        public string Name { get; }
        public CustomValue Custom { get; }
        [BsonIgnore]
        public int Ignored { get; set; }
    }

    private sealed class ThrowingGetter
    {
        public int Id => 1;
        public string Value => throw new InvalidOperationException("getter");
    }

    private sealed class ThrowingCustom { }

    private sealed class ThrowingConstructor
    {
        [BsonCtor]
        public ThrowingConstructor(int value) => throw new InvalidOperationException("constructor");
    }
}
