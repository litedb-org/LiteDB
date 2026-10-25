namespace LiteDB.Fuzz;

internal interface IFuzzTarget
{
    string Name { get; }
    string Description { get; }
    Task RunAsync(FuzzContext context);
}
