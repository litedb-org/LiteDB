#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Tests.Engine
{
    // Extends the byte-durability device with a directory. A successful directory
    // barrier persists all entries; before it, every independent entry operation
    // may survive or disappear in any order. Each same-directory rename is atomic
    // (both names change together), as required by the production protocol.
    // Images use opaque inode tokens. Replaying copies remaps those tokens in a
    // valid guard, never its paths or checks, because copies get fresh OS inodes.
    // No hardlinks are used: admission deliberately rejects them.
    internal sealed class RebuildDirectoryCrashDevice : IDisposable
    {
        internal sealed class Node
        {
            internal string Inode;
            internal byte[] Bytes;
        }
        internal sealed class Image
        {
            internal string Event;
            internal Dictionary<string, Node> Names;
        }
        private readonly string _directory;
        private readonly string _marker;
        private readonly Dictionary<string, string> _tokens = new Dictionary<string, string>();
        private readonly HashSet<string> _seen = new HashSet<string>();
        internal readonly List<Image> Images = new List<Image>();
        internal readonly HashSet<string> Transitions = new HashSet<string>();
        private Image _durable;
        private int _nextToken;
        internal string Stage;
        internal int Barriers, ReorderedStates;
        internal bool Armed;

        internal RebuildDirectoryCrashDevice(string filename)
        {
            _directory = Path.GetDirectoryName(filename);
            _marker = RebuildRecovery.GetMarkerFilename(filename);
        }

        internal Image Read()
        {
            var names = new Dictionary<string, Node>();
            foreach (var path in Directory.GetFiles(_directory).Where(p => !p.EndsWith("-shm")))
            {
                var bytes = File.ReadAllBytes(path);
                var metadata = path == _marker || path == _marker + ".pending";
                using var file = metadata ? null : new DatabaseFileLock(path, true, false);
                var token = metadata ? "guard" : Token(file.Identity);
                if (metadata)
                {
                    var fields = RebuildGuardRecord.Decode(bytes);
                    if (fields != null)
                    {
                        for (var i = 5; i < 8; i++)
                            if (fields[i] != "-") fields[i] = Token(fields[i]);
                        bytes = RebuildGuardRecord.Encode(fields);
                    }
                }
                names.Add(path, new Node { Inode = token, Bytes = bytes });
            }
            return new Image { Event = Stage, Names = names };
        }

        private string Token(string identity)
        {
            if (identity.StartsWith("inode-", StringComparison.Ordinal)) return identity;
            if (!_tokens.TryGetValue(identity, out var token))
                _tokens.Add(identity, token = "inode-" + _nextToken++);
            return token;
        }

        internal void Start()
        {
            _durable = Read();
            Add(_durable);
            Armed = true;
            NativeFileSync.SimulateDirectoryErrno = directory =>
            {
                directory.Should().Be(_directory);
                if (Armed) Barrier();
                return 0;
            };
        }

        private void Barrier()
        {
            Barriers++;
            var current = Read();
            IncludeReordering(_durable, current);
            _durable = current;
            Add(current);
        }

        internal void IncludeReordering(Image before, Image current)
        {
            var changes = Changes(before, current);
            if (changes.Count != 0) Transitions.Add(Stage);
            // Every subset enumerates arbitrary persistence/reordering of the
            // independent operations, including create/delete of unrelated entries.
            for (var mask = 0; mask < (1 << changes.Count); mask++)
            {
                var names = new Dictionary<string, Node>(before.Names);
                for (var bit = 0; bit < changes.Count; bit++)
                {
                    if ((mask & (1 << bit)) == 0) continue;
                    foreach (var path in changes[bit])
                    {
                        if (current.Names.TryGetValue(path, out var node)) names[path] = node;
                        else names.Remove(path);
                    }
                }
                Add(new Image { Event = Stage + ":unsynced:" + mask, Names = names });
                if (changes.Count > 1 && mask != 0 && mask != (1 << changes.Count) - 1) ReorderedStates++;
            }
        }

        private static List<string[]> Changes(Image before, Image after)
        {
            var paths = before.Names.Keys.Union(after.Names.Keys).Where(path =>
                !before.Names.TryGetValue(path, out var old) || !after.Names.TryGetValue(path, out var next) ||
                old.Inode != next.Inode || !old.Bytes.SequenceEqual(next.Bytes)).ToList();
            var changes = new List<string[]>();
            while (paths.Count != 0)
            {
                var path = paths[0];
                paths.RemoveAt(0);
                // Pair source removal with destination addition of the same inode.
                var other = before.Names.TryGetValue(path, out var source) && !after.Names.ContainsKey(path)
                    ? paths.FirstOrDefault(p => !before.Names.ContainsKey(p) && after.Names.TryGetValue(p, out var n) &&
                        n.Inode == source.Inode && n.Bytes.SequenceEqual(source.Bytes)) : null;
                if (other == null && after.Names.TryGetValue(path, out var target) && !before.Names.ContainsKey(path))
                    other = paths.FirstOrDefault(p => !after.Names.ContainsKey(p) && before.Names.TryGetValue(p, out var n) &&
                        n.Inode == target.Inode && n.Bytes.SequenceEqual(target.Bytes));
                if (other != null) { paths.Remove(other); changes.Add(new[] { path, other }); }
                else changes.Add(new[] { path });
            }
            changes.Count.Should().BeLessThan(10, "the bounded protocol must not create an unbounded state space");
            return changes;
        }

        internal void Add(Image image)
        {
            using var sha = SHA256.Create();
            var signature = string.Join("|", image.Names.OrderBy(p => p.Key).Select(p => p.Key + ":" +
                p.Value.Inode + ":" + Convert.ToBase64String(sha.ComputeHash(p.Value.Bytes))));
            if (_seen.Add(signature)) Images.Add(image);
        }

        internal void Restore(Image image)
        {
            Armed = false;
            foreach (var path in Directory.GetFiles(_directory)) File.Delete(path);
            _tokens.Clear();
            var physical = new Dictionary<string, string>();
            foreach (var entry in image.Names.Where(p => p.Value.Inode != "guard"))
            {
                File.WriteAllBytes(entry.Key, entry.Value.Bytes);
                using var file = new DatabaseFileLock(entry.Key, true, false);
                _tokens.Add(file.Identity, entry.Value.Inode);
                physical.Add(entry.Value.Inode, file.Identity);
            }
            foreach (var entry in image.Names.Where(p => p.Value.Inode == "guard"))
            {
                var fields = RebuildGuardRecord.Decode(entry.Value.Bytes);
                var bytes = entry.Value.Bytes;
                if (fields != null)
                {
                    for (var i = 5; i < 8; i++)
                        if (physical.TryGetValue(fields[i], out var identity)) fields[i] = identity;
                    bytes = RebuildGuardRecord.Encode(fields);
                }
                File.WriteAllBytes(entry.Key, bytes);
            }
            _durable = image;
        }

        internal void Resume() { Armed = true; }
        public void Dispose() { Armed = false; NativeFileSync.SimulateDirectoryErrno = null; }
    }
}
#endif
