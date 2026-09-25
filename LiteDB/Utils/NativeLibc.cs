using System;
using System.Runtime.InteropServices;

namespace LiteDB
{
    /// <summary>
    /// fsync and fcntl from the platform C library, bound by soname. CoreCLR maps the
    /// name "libc" itself, but other hosts (Mono, custom runtimes) probe "libc.so",
    /// which glibc images without libc6-dev only have as a linker script. Candidates are
    /// tried in platform order and the first that resolves is kept; netstandard2.0 has
    /// no NativeLibrary, so each soname is its own DllImport class.
    /// </summary>
    internal static class NativeLibc
    {
        internal delegate int FsyncCall(int descriptor);
        internal delegate int FcntlCall(int descriptor, int command);

        private sealed class Binding
        {
            internal Binding(string name, FsyncCall fsync, FcntlCall fcntl)
            {
                Name = name;
                Fsync = fsync;
                Fcntl = fcntl;
            }

            internal string Name { get; }
            internal FsyncCall Fsync { get; }
            internal FcntlCall Fcntl { get; }
        }

        private static readonly object _gate = new object();
        // Published once, in one volatile field: a reader sees either no result yet or the
        // complete one. Two separate fields could be observed reordered on ARM64, and a
        // stale "resolved, no binding" would turn native sync off for the process.
        private static volatile Binding _binding;
        private static readonly Binding _none = new Binding(null, null, null);

        /// <summary>The library that resolved, or null when none did (callers then use the runtime's sync).</summary>
        internal static string LibraryName
        {
            get
            {
                return Resolve()?.Name;
            }
        }

        internal static bool TryGet(out FsyncCall fsync, out FcntlCall fcntl)
        {
            var binding = Resolve();
            fsync = binding?.Fsync;
            fcntl = binding?.Fcntl;
            return binding != null;
        }

        private static Binding Resolve()
        {
            var resolved = _binding;
            if (resolved != null) return ReferenceEquals(resolved, _none) ? null : resolved;
            lock (_gate)
            {
                resolved = _binding;
                if (resolved != null) return ReferenceEquals(resolved, _none) ? null : resolved;
                Binding found = null;
                foreach (var candidate in Candidates())
                {
                    try
                    {
                        // An invalid descriptor fails with EBADF without touching any file;
                        // only resolution failures throw.
                        candidate.Fsync(-1);
                        found = candidate;
                        break;
                    }
                    catch (DllNotFoundException) { }
                    catch (EntryPointNotFoundException) { }
                    catch (BadImageFormatException) { }
                }
                _binding = found ?? _none;
                return found;
            }
        }

        private static Binding[] Candidates()
        {
            var glibc = new Binding("libc.so.6", LibcSo6.Fsync, LibcSo6.Fcntl);
            var generic = new Binding("libc", Libc.Fsync, Libc.Fcntl);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new[] { generic, new Binding("/usr/lib/libSystem.B.dylib", LibSystem.Fsync, LibSystem.Fcntl) };
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")))
            {
                return new[] { new Binding("libc.so.7", LibcSo7.Fsync, LibcSo7.Fcntl), generic };
            }
            return new[]
            {
                glibc,
                generic,
                new Binding("libc.musl-x86_64.so.1", MuslX64.Fsync, MuslX64.Fcntl),
                new Binding("libc.musl-aarch64.so.1", MuslArm64.Fsync, MuslArm64.Fcntl),
                new Binding("libc.musl-armv7.so.1", MuslArmv7.Fsync, MuslArmv7.Fcntl),
                new Binding("libc.musl-armhf.so.1", MuslArmhf.Fsync, MuslArmhf.Fcntl),
                new Binding("libc.musl-x86.so.1", MuslX86.Fsync, MuslX86.Fcntl),
            };
        }

        // fcntl is variadic; F_FULLFSYNC takes no argument, so only the fixed parameters are passed.
        private static class Libc
        {
            [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }

        private static class LibcSo6
        {
            [DllImport("libc.so.6", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("libc.so.6", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }

        private static class LibcSo7
        {
            [DllImport("libc.so.7", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("libc.so.7", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }

        private static class LibSystem
        {
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }

        private static class MuslX64
        {
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }

        private static class MuslArm64
        {
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }

        private static class MuslArmv7
        {
            [DllImport("libc.musl-armv7.so.1", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("libc.musl-armv7.so.1", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }

        private static class MuslArmhf
        {
            [DllImport("libc.musl-armhf.so.1", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("libc.musl-armhf.so.1", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }

        private static class MuslX86
        {
            [DllImport("libc.musl-x86.so.1", EntryPoint = "fsync", SetLastError = true)] internal static extern int Fsync(int descriptor);
            [DllImport("libc.musl-x86.so.1", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int descriptor, int command);
        }
    }
}
