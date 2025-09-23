using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.QueryTest
{
    public class Where_Tests : PersonQueryData
    {
        private readonly ITestOutputHelper _output;

        public Where_Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        class Entity
        {
            public string Name { get; set; }
            public int Size { get; set; }
        }

        [Fact(Timeout = 30000)]
        public async Task Query_Where_With_Parameter()
        {
            var testName = nameof(Query_Where_With_Parameter);

            _output.WriteLine($"starting {testName}");

            try
            {
                using var db = new PersonQueryData();
                var (collection, local) = db.GetData();

                var r0 = local
                    .Where(x => x.Address.State == "FL")
                    .ToArray();

                var r1 = collection.Query()
                    .Where(x => x.Address.State == "FL")
                    .ToArray();

                AssertEx.ArrayEqual(r0, r1, true);
            }
            finally
            {
                _output.WriteLine($"{testName} completed");
            }

            await Task.CompletedTask;
        }

        [Fact(Timeout = 30000)]
        public async Task Query_Multi_Where_With_Like()
        {
            var testName = nameof(Query_Multi_Where_With_Like);

            _output.WriteLine($"starting {testName}");

            try
            {
                using var db = new PersonQueryData();
                var (collection, local) = db.GetData();

                var r0 = local
                    .Where(x => x.Age >= 10 && x.Age <= 40)
                    .Where(x => x.Name.StartsWith("Ge"))
                    .ToArray();

                var r1 = collection.Query()
                    .Where(x => x.Age >= 10 && x.Age <= 40)
                    .Where(x => x.Name.StartsWith("Ge"))
                    .ToArray();

                AssertEx.ArrayEqual(r0, r1, true);
            }
            finally
            {
                _output.WriteLine($"{testName} completed");
            }

            await Task.CompletedTask;
        }

        [Fact(Timeout = 30000)]
        public async Task Query_Single_Where_With_And()
        {
            var testName = nameof(Query_Single_Where_With_And);

            _output.WriteLine($"starting {testName}");

            try
            {
                using var db = new PersonQueryData();
                var (collection, local) = db.GetData();

                var r0 = local
                    .Where(x => x.Age == 25 && x.Active)
                    .ToArray();

                var r1 = collection.Query()
                    .Where("age = 25 AND active = true")
                    .ToArray();

                AssertEx.ArrayEqual(r0, r1, true);
            }
            finally
            {
                _output.WriteLine($"{testName} completed");
            }

            await Task.CompletedTask;
        }

        [Fact(Timeout = 30000)]
        public async Task Query_Single_Where_With_Or_And_In()
        {
            var testName = nameof(Query_Single_Where_With_Or_And_In);

            _output.WriteLine($"starting {testName}");

            try
            {
                using var db = new PersonQueryData();
                var (collection, local) = db.GetData();

                var r0 = local
                    .Where(x => x.Age == 25 || x.Age == 26 || x.Age == 27)
                    .ToArray();

                var r1 = collection.Query()
                    .Where("age = 25 OR age = 26 OR age = 27")
                    .ToArray();

                var r2 = collection.Query()
                    .Where("age IN [25, 26, 27]")
                    .ToArray();

                AssertEx.ArrayEqual(r0, r1, true);
                AssertEx.ArrayEqual(r1, r2, true);
            }
            finally
            {
                _output.WriteLine($"{testName} completed");
            }

            await Task.CompletedTask;
        }

        [Fact(Timeout = 30000)]
        public async Task Query_With_Array_Ids()
        {
            var testName = nameof(Query_With_Array_Ids);

            _output.WriteLine($"starting {testName}");

            try
            {
                using var db = new PersonQueryData();
                var (collection, local) = db.GetData();

                var ids = new int[] { 1, 2, 3 };

                var r0 = local
                    .Where(x => ids.Contains(x.Id))
                    .ToArray();

                var r1 = collection.Query()
                    .Where(x => ids.Contains(x.Id))
                    .ToArray();

                AssertEx.ArrayEqual(r0, r1, true);
            }
            finally
            {
                _output.WriteLine($"{testName} completed");
            }

            await Task.CompletedTask;
        }
    }
}
