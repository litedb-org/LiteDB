using System.Diagnostics.Tracing;
using System.Text.Json;

namespace Ab5021Benchmarks;

// Synchronous in-process delivery avoids EventPipe transport/decoder event loss.
// Diagnostic builds only enable the source; gate builds do not emit these events.
internal sealed class ProfileCounters : EventListener
{
    private readonly object _gate = new();
    private readonly List<object> _rows = new();
    private string? _scenario;
    private long _groups, _commits, _piggybacks, _appends, _starts, _ends, _native, _during;
    private bool _active;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name is "LiteDB-CommitProfile" or "LiteDB-ScenarioProfile")
            EnableEvents(source, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        // EventListener base construction can call this before field initialization.
        if (_gate == null) return;
        lock (_gate)
        {
            if (e.EventId == 0) throw new InvalidDataException("EventSource error: " + e.Message);
            if (e.EventSource.Name == "LiteDB-ScenarioProfile")
            {
                if (e.EventId == 1)
                {
                    if (_scenario != null) throw new InvalidDataException("Nested scenario");
                    _scenario = (string)e.Payload![0]!;
                    _groups = _commits = _piggybacks = _appends = _starts = _ends = _native = _during = 0;
                    _active = false;
                }
                else if (e.EventId == 2)
                {
                    if (_scenario != (string)e.Payload![0]! || _active || _commits != 5000 ||
                        _appends != 5000 || _groups + _piggybacks != 5000 ||
                        _starts != _groups || _ends != _groups || _native != _groups)
                        throw new InvalidDataException("Incomplete in-process counters: " + _scenario);
                    _rows.Add(new { scenario = _scenario, groups = _groups, commits = _commits,
                        piggybacks = _piggybacks, appends = _appends, starts = _starts,
                        ends = _ends, native = _native, appendsDuringSync = _during });
                    _scenario = null;
                }
                return;
            }
            if (_scenario == null) return;
            switch (e.EventId)
            {
                case 1: _groups++; _commits += Convert.ToInt64(e.Payload![0]); break;
                case 2: _piggybacks++; break;
                case 3: if ((string)e.Payload![0]! == "group-device") _native++; break;
                case 4:
                    if (_active) throw new InvalidDataException("Overlapping syncs");
                    _starts++; _active = true; break;
                case 6: _appends++; if (_active) _during++; break;
                case 7: _ends++; _active = false; break;
            }
        }
    }

    internal void Save(string path)
    {
        lock (_gate)
        {
            if (_scenario != null || _rows.Count != 8) throw new InvalidDataException("Expected eight complete scenarios");
            File.WriteAllText(path, JsonSerializer.Serialize(_rows, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
