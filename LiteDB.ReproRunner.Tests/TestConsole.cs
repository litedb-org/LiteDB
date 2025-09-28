using System.Text;
using LiteDB.ReproRunner.Cli;

namespace LiteDB.ReproRunner.Tests;

internal sealed class TestConsole : IConsole, IDisposable
{
    private readonly StringWriter _stdout;
    private readonly StringWriter _stderr;

    public TestConsole()
    {
        _stdout = new StringWriter(new StringBuilder());
        _stderr = new StringWriter(new StringBuilder());
    }

    public TextWriter Out => _stdout;

    public TextWriter Error => _stderr;

    public string StandardOutput => _stdout.ToString();

    public string StandardError => _stderr.ToString();

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
    }
}
