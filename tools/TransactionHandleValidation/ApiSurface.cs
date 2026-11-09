using System.Reflection;
using LiteDB;
using LiteDB.Engine;

public static class ApiSurface
{
    public static void Print()
    {
        foreach (var type in new[] { typeof(ILiteDatabase), typeof(ILiteEngine) })
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.MemberType + " " + m).OrderBy(m => m, StringComparer.Ordinal))
            Console.WriteLine("surface:" + type.FullName + ":" + member);
    }

    public static void CheckObsolete()
    {
        foreach (var type in new[] { typeof(ILiteDatabase), typeof(LiteDatabase) })
        {
            var actual = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<ObsoleteAttribute>() != null).ToArray();
            if (!actual.Select(m => m.Name).OrderBy(n => n).SequenceEqual(new[] { "BeginTrans", "Commit", "Rollback" }))
                throw new Exception("Unexpected obsolete member set: " + type);
            if (actual.Any(m => m.GetCustomAttribute<ObsoleteAttribute>().IsError))
                throw new Exception("Legacy API must remain warning-only");
        }
        Console.WriteLine("PASS exact obsolete declarations");
    }
}
