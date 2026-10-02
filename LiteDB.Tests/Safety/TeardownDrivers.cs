using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// The registered sweep drivers. Every <c>[TeardownPath]</c> needs at least one; the sweep fails for
    /// a path without one ("new teardown path X has no sweep driver"). To add one, append a driver in
    /// the Direct or Shared file (or, for a path that exists only on another revision, in an overlay file
    /// that adds a partial <c>Overlay</c> method; see docs/teardown-sweep.md).
    /// </summary>
    internal static partial class TeardownDrivers
    {
        private static readonly Lazy<TeardownDriver[]> _all = new Lazy<TeardownDriver[]>(() =>
        {
            var drivers = Direct().Concat(Shared()).ToList();
            Overlay(drivers);
            return drivers.ToArray();
        });

        public static IReadOnlyList<TeardownDriver> All => _all.Value;

        public static IEnumerable<TeardownDriver> For(string path) =>
            All.Where(driver => string.Equals(driver.Path, path, StringComparison.Ordinal));

        /// <summary>Drivers for teardown paths that exist only on another revision, added by an overlay file.</summary>
        static partial void Overlay(List<TeardownDriver> drivers);
    }
}
