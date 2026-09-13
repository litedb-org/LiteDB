using LiteDB;

internal static partial class Faker
{
    private static Random _random = new Random(420);

    public static string Fullname()
    {
        var names = _random.NextBool() ? _maleNames : _femaleNames;

        return names[_random.Next(names.Length - 1)] + " " + _surNames[_random.Next(_surNames.Length - 1)];
    }

    public static int Age()
    {
        return _random.Next(18, 96);
    }

    public static DateTime Birthday()
    {
        var oldest = DateTime.Today.AddYears(-110).Ticks;
        var now = DateTime.Now.Ticks;
        var range =  now - oldest;

        var date = new DateTime(oldest + _random.NextLong(0, range));

        return date;
    }

    public static string Lorem(int size, int end = -1)
    {
        return string.Join(" ", Enumerable.Range(1, end == -1 ? size : _random.Next(size, end))
            .Select(x => _lorem[_random.Next(_lorem.Length - 1)]));
    }

    public static int Next(int start, int end)
    {
        return _random.Next(start, end);
    }

    public static double NextDouble(double start, double end)
    {
        return start + (_random.NextDouble() * (end - start));
    }

    // https://stackoverflow.com/a/13095144/3286260
    public static long NextLong(this Random random, long min, long max)
    {
        if (max <= min)
            throw new ArgumentOutOfRangeException("max", "max must be > min!");

        //Working with ulong so that modulo works correctly with values > long.MaxValue
        ulong uRange = (ulong)(max - min);

        //Prevent a modolo bias; see https://stackoverflow.com/a/10984975/238419
        //for more information.
        //In the worst case, the expected number of calls is 2 (though usually it's
        //much closer to 1) so this loop doesn't really hurt performance at all.
        ulong ulongRand;
        do
        {
            byte[] buf = new byte[8];
            random.NextBytes(buf);
            ulongRand = (ulong)BitConverter.ToInt64(buf, 0);
        } while (ulongRand > ulong.MaxValue - ((ulong.MaxValue % uRange) + 1) % uRange);

        return (long)(ulongRand % uRange) + min;
    }

    public static bool NextBool(this Random random)
    {
        return random.NextSingle() >= 0.5;
    }

    public static string Departments() => _departments[_random.Next(0, _departments.Length - 1)];

    internal static BsonValue Created()
    {
        var oldest = DateTime.Today.AddYears(-5).Ticks;
        var now = DateTime.Now.Ticks;
        var range = now - oldest;

        var date = new DateTime(oldest + _random.NextLong(0, range));

        return date;
    }

    public static string Language() => _departments[_random.Next(0, _departments.Length - 1)];

    public static string Department() => _departments[_random.Next(0, _departments.Length - 1)];

    public static string Country() => _countries[_random.Next(0, _countries.Length - 1)];

    public static string Job() => _jobTitles[_random.Next(0, _jobTitles.Length - 1)];

    internal static string SkuNumber() =>
        string.Join("", Enumerable.Range(1, 8).Select(x => _skuDigits[_random.Next(0, _skuDigits.Length - 1)]));

}
