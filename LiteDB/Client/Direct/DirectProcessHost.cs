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
        // A published host owns the storage core until its final release removes the
        // entry. A strong registry root is required while an abandoned facade's
        // finalizer is pending; a weak target could disappear while its streams remain
        // open and let Attach construct a second core for the same physical file.
        private static readonly Dictionary<string, DirectProcessHost> Hosts =
            new Dictionary<string, DirectProcessHost>();
        private static readonly HashSet<string> Opening = new HashSet<string>();
        private static readonly object Gate = new object();
        private readonly object _lifecycle = new object();
        private Thread _closingThread;
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
            Engine = new LiteEngine(Settings, ownsDirectHost: true);
            _identity = admission.Identity;
        }

        internal static DirectProcessHost Attach(EngineSettings settings)
        {
            NativeAdmission.Normalize(settings);
            // Existing files are fenced by native admission. Only creation needs
            // the path mutex, acquired BEFORE local gates (replacement uses that order).
            using var path = File.Exists(settings.Filename) ? null : NativeAdmission.EnterPath(settings.Filename);
            while (true)
            {
                DirectProcessHost waiting = null;
                lock (Gate)
                {
                    var admission = NativeAdmission.Open(settings, shared: false, normalized: true, create: path != null);
                    try
                    {
                        if (Hosts.TryGetValue(admission.Identity, out var host))
                        {
                            if (host._closed)
                            {
                                host.Engine.ThrowBackgroundCheckpointFailure();
                                if (host.Engine.OwnsBackgroundCheckpoint || host._closingThread == Thread.CurrentThread)
                                    throw new InvalidOperationException("The Direct host is closing. Retry after Dispose returns.");
                                // Preserve the existing synchronous-close attachment
                                // contract, waiting on this host outside the static gate.
                                waiting = host;
                            }
                            else
                            {
                                if (host._maintaining) throw new InvalidOperationException("The Direct host is performing maintenance. Retry when it finishes.");
                                host.Engine.ValidateHost();
                                DirectHostSettings.RequireCompatible(host.Settings, settings, host.Engine.Pragma(Pragmas.COLLATION).AsString);
                                host._references++;
                                return host;
                            }
                        }
                        else
                        {
                            var identity = admission.Identity;
                            if (!Opening.Add(identity)) throw new InvalidOperationException("Recursive opening of a Direct host is unsupported.");
                            try { host = new DirectProcessHost(settings, admission); }
                            finally { Opening.Remove(identity); }
                            admission = null;
                            host._references = 1;
                            Hosts[host._identity] = host;
                            return host;
                        }
                    }
                    finally { admission?.Dispose(); }
                }
                lock (waiting._lifecycle) { }
            }
        }

        internal void ValidateRelease() => Engine.ValidateBackgroundCheckpointRelease();

        internal void Release(TransactionMonitor owner)
        {
            lock (_lifecycle)
            {
                bool final;
                lock (Gate)
                {
                    if (_closed) return;
                    final = _references == 1;
                    if (final)
                    {
                        _closed = true;
                        _closingThread = Thread.CurrentThread;
                    }
                }
                try
                {
                    try
                    {
                        if (final) owner?.CloseOwner();
                        else owner?.DisposeOwner();
                    }
                    catch (Exception error)
                    {
                        Engine.StopHost(error);
                        Engine.ThrowBackgroundCheckpointFailure(error);
                        throw;
                    }
                    finally
                    {
                        if (final)
                        {
#if DEBUG || TESTING
                            BeforeFinalClose?.Invoke(Settings.Filename);
#endif
                            Engine.Dispose();
                        }
                    }
                    Engine.ThrowBackgroundCheckpointFailure();
                }
                finally
                {
                    lock (Gate)
                    {
                        --_references;
                        if (final)
                        {
                            try { _admission.Dispose(); }
                            finally
                            {
                                _admission = null;
                                Hosts.Remove(_identity);
                            }
                        }
                    }
                }
            }
        }

        internal T Maintain<T>(Func<T> action)
        {
            using var path = NativeAdmission.EnterPath(Settings.Filename);
            lock (_lifecycle)
            {
                lock (Gate)
                {
                    if (_closed) throw new ObjectDisposedException(nameof(LiteDatabase));
                    if (_references != 1) throw new InvalidOperationException("Close other Direct facades before checkpointing or replacing database files.");
                    if (_maintaining) throw new InvalidOperationException("Recursive Direct maintenance is unsupported.");
                    _maintaining = true;
                }
                try { return action(); }
                finally
                {
                    lock (Gate)
                    {
                        _maintaining = false;
                        var next = _admission.Identity;
                        if (next != _identity)
                        {
                            Hosts.Remove(_identity);
                            Hosts[next] = this;
                            _identity = next;
                        }
                    }
                }
            }
        }

#if DEBUG || TESTING
        internal static Action<string> BeforeFinalClose;
#endif
    }
}
