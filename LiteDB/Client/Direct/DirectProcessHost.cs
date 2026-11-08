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
        private static readonly Dictionary<NativeAdmission.Entry, Thread> Opening = new Dictionary<NativeAdmission.Entry, Thread>();
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
            Settings.ReadTransform = DirectReadTransform.Wrap(settings.ReadTransform);
            _admission = admission;
            _registryKey = admission.RegistryKey;
            Settings.BypassDirectHost = true;
            Engine = new LiteEngine(Settings);
        }

        internal static DirectProcessHost Attach(EngineSettings settings)
        {
#if DEBUG || TESTING
            OwnershipAssertions.RequireNotHeld(Gate, "Path admission must precede the host registry.");
#endif
            NativeAdmission.Normalize(settings);
            // Existing files are fenced by native admission. Only creation needs
            // the path mutex, acquired BEFORE local gates (replacement uses that order).
            using var path = File.Exists(settings.Filename) ? null : NativeAdmission.EnterPath(settings.Filename);
            while (true)
            {
                DirectProcessHost waiting = null;
                NativeAdmission admission = null;
                NativeAdmission.Entry openingKey = null;
                lock (Gate)
                {
                    admission = NativeAdmission.Open(settings, shared: false, normalized: true, create: path != null);
                    try
                    {
                        if (Hosts.TryGetValue(admission.RegistryKey, out var host))
                        {
                            if (host._closed)
                            {
                                if (host._closingThread == Thread.CurrentThread)
                                    throw new InvalidOperationException("The Direct host is closing. Retry after Dispose returns.");
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
                        else if (Opening.TryGetValue(admission.RegistryKey, out var opener))
                        {
                            if (opener == Thread.CurrentThread)
                                throw new InvalidOperationException("Recursive opening of a Direct host is unsupported.");
                            // Wait releases the registry gate. The retained admission
                            // keeps the physical entry stable until we retry.
#if DEBUG || TESTING
                            BeforeHostWait?.Invoke(settings.Filename);
#endif
                            Monitor.Wait(Gate);
                        }
                        else
                        {
                            openingKey = admission.RegistryKey;
                            Opening.Add(openingKey, Thread.CurrentThread);
                        }
                    }
                    finally
                    {
                        if (openingKey == null) admission.Dispose();
                    }
                }
                if (openingKey != null)
                {
                    try
                    {
                        // Recovery/upgrade may open a replacement host. Never hold
                        // the global registry across storage, path admission or callbacks.
#if DEBUG || TESTING
                        BeforeHostConstruction?.Invoke(settings.Filename);
#endif
                        var host = new DirectProcessHost(settings, admission);
                        lock (Gate)
                        {
                            host._references = 1;
                            Hosts.Add(openingKey, host);
                        }
                        admission = null;
                        return host;
                    }
                    finally
                    {
                        try { admission?.Dispose(); }
                        finally
                        {
                            lock (Gate)
                            {
                                Opening.Remove(openingKey);
                                Monitor.PulseAll(Gate);
                            }
                        }
                    }
                }
                if (waiting != null) lock (waiting._lifecycle) { }
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
#if DEBUG || TESTING
            OwnershipAssertions.RequireNotHeld(Gate, "Maintenance path admission must precede the host registry.");
            OwnershipAssertions.RequireNotHeld(_lifecycle, "Maintenance path admission must precede lifecycle admission.");
#endif
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
        internal static Action<string> BeforeHostConstruction;
        internal static Action<string> BeforeHostWait;
        internal static Action<string> BeforeFinalClose;
        internal static Action<string> BeforeMaintenanceRelease;
#endif
    }
}
