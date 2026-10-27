using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB.Client.Direct
{
    // The registry pays only at open/dispose/maintenance. Engine facades share
    // storage fields, with a transaction-monitor owner view fixed at construction.
    internal sealed class DirectProcessHost
    {
        private static readonly Dictionary<string, WeakReference<DirectProcessHost>> Hosts =
            new Dictionary<string, WeakReference<DirectProcessHost>>();
        private static readonly HashSet<string> Opening = new HashSet<string>();
        private static readonly object Gate = new object();
        private NativeAdmission _admission;
        internal readonly EngineSettings Settings;
        internal readonly LiteEngine Engine;
        private int _references;
        private bool _closed, _maintaining;
        private string _identity;

        private DirectProcessHost(EngineSettings settings, NativeAdmission admission)
        {
            Settings = settings;
            _admission = admission;
            _identity = admission.Identity;
            Settings.BypassDirectHost = true;
            Engine = new LiteEngine(Settings);
            _identity = admission.Identity;
        }

        internal static DirectProcessHost Attach(EngineSettings settings)
        {
            NativeAdmission.Normalize(settings);
            // Existing files are fenced by native admission. Only creation needs
            // the path mutex, acquired BEFORE local gates (replacement uses that order).
            using var path = File.Exists(settings.Filename) ? null : NativeAdmission.EnterPath(settings.Filename);
            lock (Gate)
            {
                var admission = NativeAdmission.Open(settings, shared: false, normalized: true, create: path != null);
                try
                {
                    if (Hosts.TryGetValue(admission.Identity, out var weak) && weak.TryGetTarget(out var host))
                    {
                        if (host._closed) throw new InvalidOperationException("The Direct host is closing. Retry after Dispose returns.");
                        if (host._maintaining) throw new InvalidOperationException("The Direct host is performing maintenance. Retry when it finishes.");
                        host.Engine.ValidateHost();
                        DirectHostSettings.RequireCompatible(host.Settings, settings, host.Engine.Pragma(Pragmas.COLLATION).AsString);
                        host._references++;
                        return host;
                    }
                    var identity = admission.Identity;
                    if (!Opening.Add(identity)) throw new InvalidOperationException("Recursive opening of a Direct host is unsupported.");
                    try { host = new DirectProcessHost(settings, admission); }
                    finally { Opening.Remove(identity); }
                    admission = null;
                    host._references = 1;
                    Hosts[host._identity] = new WeakReference<DirectProcessHost>(host);
                    return host;
                }
                finally { admission?.Dispose(); }
            }
        }

        internal void Release(TransactionMonitor owner)
        {
            lock (Gate)
            {
                if (_closed) return;
                try
                {
                    if (_references > 1) owner?.DisposeOwner();
                    else owner?.CloseOwner();
                }
                catch (Exception error) { Engine.StopHost(error); throw; }
                finally
                {
                    if (--_references == 0)
                    {
                        _closed = true;
                        try
                        {
#if DEBUG || TESTING
                            BeforeFinalClose?.Invoke(Settings.Filename);
#endif
                            Engine.Dispose();
                        }
                        finally
                        {
                            Hosts.Remove(_identity);
                            _admission.Dispose();
                            _admission = null;
                        }
                    }
                }
            }
        }

        internal T Maintain<T>(Func<T> action)
        {
            using var path = NativeAdmission.EnterPath(Settings.Filename);
            lock (Gate)
            {
                if (_closed) throw new ObjectDisposedException(nameof(LiteDatabase));
                if (_references != 1) throw new InvalidOperationException("Close other Direct facades before checkpointing or replacing database files.");
                _maintaining = true;
                try { return action(); }
                finally
                {
                    _maintaining = false;
                    var next = _admission.Identity;
                    if (next != _identity)
                    {
                        Hosts.Remove(_identity);
                        Hosts[next] = new WeakReference<DirectProcessHost>(this);
                        _identity = next;
                    }
                }
            }
        }

#if DEBUG || TESTING
        internal static Action<string> BeforeFinalClose;
#endif
    }
}
