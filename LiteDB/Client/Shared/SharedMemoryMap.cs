#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Runtime.InteropServices;

namespace LiteDB.Client.Shared
{
    // Lifetime is owned by the participant, which serializes release with leases.
    internal sealed unsafe class SharedMemoryMap : IDisposable
    {
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly SharedWakeSignal _wake;
        private byte* _pointer;
        private bool _acquired;
        internal readonly long Size;

        internal SharedMemoryMap(FileStream file, long size, Guid authority = default(Guid))
        {
            Size = size;
            if (authority != Guid.Empty) _wake = SharedWakeSignal.Open(authority);
            _map = MemoryMappedFile.CreateFromFile(file, null, size, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, leaveOpen: true);
            try
            {
                _view = _map.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
                byte* pointer = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                _acquired = true;
                _pointer = pointer + _view.PointerOffset;
                if (((long)_pointer & 7) != 0) throw new IOException("Unaligned Shared mapping.");
            }
            catch { this.Dispose(); throw; }
        }

        internal long Load(long offset) { this.Require(offset, 8); return Interlocked.Read(ref *(long*)(_pointer + offset)); }
        internal void Store(long offset, long value) { this.Require(offset, 8); Interlocked.Exchange(ref *(long*)(_pointer + offset), value); }
        internal byte[] Read(long offset, int count)
        {
            this.Require(offset, count);
            var result = new byte[count];
            _view.ReadArray(offset, result, 0, count);
            return result;
        }
        internal long CompareExchange(long offset, long value, long expected)
        { this.Require(offset, 8); return Interlocked.CompareExchange(ref *(long*)(_pointer + offset), value, expected); }
        internal long Increment(long offset)
        { this.Require(offset, 8); return Interlocked.Increment(ref *(long*)(_pointer + offset)); }

        internal void PrepareWake() => _wake?.Prepare();

        // The mapping remains pinned until every holder/request lease drains.
        internal void Wake()
        {
            Increment(SharedMemoryLayout.WriterWake);
            if (_wake != null)
            {
                _wake.Wake();
                return;
            }
            var result = syscall(FutexSyscall, (IntPtr)(_pointer + SharedMemoryLayout.WriterWake), 1, int.MaxValue, IntPtr.Zero);
            if (result < 0) throw new IOException("Shared writer futex wake failed: " + Marshal.GetLastPInvokeError());
        }
        internal void WaitWake(long observed, int milliseconds)
        {
            if (_wake != null)
            {
                if (Load(SharedMemoryLayout.WriterWake) != observed)
                {
                    ObserveWait("returned", 0);
                    return;
                }
                ObserveWait("syscall", milliseconds);
                _wake.Wait(milliseconds);
                ObserveWait("returned", 0);
                return;
            }
            var timeout = new Timespec { Seconds = 0, Nanoseconds = milliseconds * 1000000L };
            ObserveWait("syscall", milliseconds);
            var result = syscall(FutexSyscall, (IntPtr)(_pointer + SharedMemoryLayout.WriterWake), 0,
                unchecked((int)observed), (IntPtr)(&timeout));
            var error = result >= 0 ? 0 : Marshal.GetLastPInvokeError();
#if DEBUG || TESTING
            var simulate = SimulateWaitError;
            if (simulate != null)
            {
                LiteDB.Utils.Reachability.FaultPoint("SimulateWaitError");
                error = simulate(error);
            }
#endif
            ObserveWait("returned", error);
            if (error != 0 && error != 11 && error != 4 && error != 110)
                throw new IOException("Shared writer futex wait failed: " + error);
        }
#if DEBUG || TESTING
        internal IntPtr WakeAddress => (IntPtr)(_pointer + SharedMemoryLayout.WriterWake);
        internal Action<string, int> WaitObserved;
        internal Func<int, int> SimulateWaitError;
#endif
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        internal void ObserveWait(string stage, int value)
        {
#if DEBUG || TESTING
            WaitObserved?.Invoke(stage, value);
#endif
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec { internal long Seconds, Nanoseconds; }
        private static long FutexSyscall => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 98 : 202;
        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, IntPtr address, int operation, int value, IntPtr timeout);
        internal bool Matches(long offset, byte[] expected)
        {
            for (var i = 0; i < expected.Length; i += 8)
                if (Load(offset + i) != BitConverter.ToInt64(expected, i)) return false;
            return true;
        }
        internal void Write(long offset, byte[] bytes)
        {
            this.Require(offset, bytes.Length);
            _view.WriteArray(offset, bytes, 0, bytes.Length);
        }
        internal void Zero(long offset, long count)
        {
            this.Require(offset, count);
            new Span<byte>(_pointer + offset, checked((int)count)).Clear();
        }
        private void Require(long offset, long count)
        {
            if (!_acquired) throw new ObjectDisposedException(nameof(SharedMemoryMap));
            if (offset < 0 || count < 0 || offset > Size - count) throw new ArgumentOutOfRangeException(nameof(offset));
        }
        ~SharedMemoryMap() { this.Dispose(); }
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (_acquired) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _acquired = false; _pointer = null; }
            _view?.Dispose();
            _map?.Dispose();
            _wake?.Dispose();
        }
    }
}
#endif
