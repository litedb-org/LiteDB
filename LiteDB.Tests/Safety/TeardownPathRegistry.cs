using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Tests.Safety
{
    /// <summary>A registered teardown path: a method carrying <see cref="TeardownPathAttribute"/>.</summary>
    internal sealed class TeardownPathInfo
    {
        public string Name { get; set; }
        public FaultDisposition Declared { get; set; }
        public string Basis { get; set; }
        public MethodBase Method { get; set; }
        public string Member => this.Method.DeclaringType?.Name + "." + this.Method.Name;
        public override string ToString() => this.Name;
    }

    /// <summary>
    /// A sweep driver: builds a realistic prior state for one registered teardown path and invokes
    /// it through <see cref="Entry"/>, the call whose outcome shows the path's disposition (the path
    /// itself, or a caller that passes its outcome through unchanged).
    /// </summary>
    internal sealed class TeardownDriver
    {
        public TeardownDriver(string path, string variant, TeardownMode mode, string entry, Action<TeardownCase> drive,
            Func<TeardownPrior> defaults = null, string notApplicable = null, Action<TeardownPrior> require = null)
        {
            this.Require = require ?? (_ => { });
            this.Path = path;
            this.Variant = variant;
            this.Mode = mode;
            this.Entry = entry;
            this.Drive = drive;
            this.Defaults = defaults ?? (() => new TeardownPrior());
            this.NotApplicable = notApplicable;
        }

        public string Path { get; }
        public string Variant { get; }
        public TeardownMode Mode { get; }
        public string Entry { get; }
        public Action<TeardownCase> Drive { get; }
        /// <summary>
        /// The prior state the xUnit sweep uses. Its true flags are also the elements the driver tolerates:
        /// the fuzz target randomizes those (and the document count, uploads, encryption and Shared peers).
        /// </summary>
        public Func<TeardownPrior> Defaults { get; }
        /// <summary>Forces the prior-state elements the driver cannot do without (after randomization).</summary>
        public Action<TeardownPrior> Require { get; }
        /// <summary>Why the driver cannot run on this runtime (the path's code differs there); null when it runs.</summary>
        public string NotApplicable { get; }
        public string Id => $"{this.Path}/{this.Variant}/{this.Mode.ToString().ToLowerInvariant()}";
        public override string ToString() => this.Id;
    }

    /// <summary>Reflection over the library: every registered teardown path and its declaration.</summary>
    internal static class TeardownPathRegistry
    {
        private static readonly Lazy<TeardownPathInfo[]> _paths = new Lazy<TeardownPathInfo[]>(Scan);

        public static IReadOnlyList<TeardownPathInfo> Paths => _paths.Value;

        public static TeardownPathInfo Find(string name) =>
            Paths.FirstOrDefault(path => string.Equals(path.Name, name, StringComparison.Ordinal));

        /// <summary>The path a step belongs to: its name without the last segment (<c>&lt;Path&gt;.&lt;step&gt;</c>).</summary>
        public static string OwnerOf(string step)
        {
            var dot = step.LastIndexOf('.');
            return dot > 0 ? step.Substring(0, dot) : step;
        }

        private static TeardownPathInfo[] Scan()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.DeclaredOnly;
            var found = new List<TeardownPathInfo>();
            foreach (var type in Types(typeof(LiteEngine).Assembly))
            {
                var members = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
                foreach (var member in members)
                {
                    var attribute = member.GetCustomAttribute<TeardownPathAttribute>(false);
                    if (attribute == null) continue;
                    found.Add(new TeardownPathInfo
                    {
                        Name = attribute.Name, Declared = (FaultDisposition)(int)attribute.Declared,
                        Basis = attribute.Basis, Method = member
                    });
                }
            }
            return found.OrderBy(path => path.Name, StringComparer.Ordinal).ToArray();
        }

        private static IEnumerable<Type> Types(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { return error.Types.Where(type => type != null); }
        }
    }
}
