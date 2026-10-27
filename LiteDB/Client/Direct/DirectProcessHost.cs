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
        // Native admission retains this entry across inode replacement. Keying by
        // its changing physical identity would hide the live host during rebuild.
        private static readonly Dictionary<NativeAdmission.Entry, DirectProcessHost> Hosts =
            new Dictionary<NativeAdmission.Entry, DirectProcessHost>();
        private static readonly HashSet<NativeAdmission.Entry> Opening = new HashSet<NativeAdmission.Entry>();
        private static readonly object Gate = new object();
        private readonly object _lifecycle = new object();
        private Thread _closingThread;
        private NativeAdmission _admission;
        internal readonly EngineSettings Settings;
        internal readonly LiteEngine Engine;
        private int _references;
        private bool _closed, _maintaining;
        private readonly NativeAdmission.Entry _registryKey;

        private DirectProcessHost(EngineSettings settings, NativeAdmission admission)
        {
            Settings = settings;
            _admission = admission;
            _registryKey = admission.RegistryKey;
            Settings.BypassDirectHost = true;
            Engine = new LiteEngine(Settings);
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
                        if (Hosts.TryGetValue(admission.RegistryKey, out var host))
                        {
                            if (host._closed)
                            {
                                if (host._closingThread == Thread.CurrentThread)
                                    throw new InvalidOperationException("The Direct host is closing. Retry after Dispose returns.");
                                // Preserve the existing synchronous-close attachment
                                // contract, waiting on this host outside the static gate.
                                waiting = host;
                            }
                            else
                            {
                                if (host._maintaining)
                                {
                                    Utils.Reachability.Sometimes("refusal:direct-attach-during-maintenance");
                                    throw new InvalidOperationException("The Direct host is performing maintenance. Retry when it finishes.");
                                }
                                host.Engine.ValidateHost();
                                DirectHostSettings.RequireCompatible(host.Settings, settings, host.Engine.Pragma(Pragmas.COLLATION).AsString);
                                host._references++;
                                return host;
                            }
                        }
                        else
                        {
                            var key = admission.RegistryKey;
                            if (!Opening.Add(key)) throw new InvalidOperationException("Recursive opening of a Direct host is unsupported.");
                            try { host = new DirectProcessHost(settings, admission); }
                            finally { Opening.Remove(key); }
                            admission = null;
                            host._references = 1;
                            Hosts[host._registryKey] = host;
                            return host;
                        }
                    }
                    finally { admission?.Dispose(); }
                }
                lock (waiting._lifecycle) { }
            }
        }

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
                                Hosts.Remove(_registryKey);
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
                try
                {
                    var result = action();
#if DEBUG || TESTING
                    BeforeMaintenanceRelease?.Invoke(Settings.Filename);
#endif
                    return result;
                }
                finally
                {
                    lock (Gate)
                    {
                        _maintaining = false;
                    }
                }
            }
        }

#if DEBUG || TESTING
        internal static Action<string> BeforeFinalClose;
        internal static Action<string> BeforeMaintenanceRelease;
#endif
    }
}
