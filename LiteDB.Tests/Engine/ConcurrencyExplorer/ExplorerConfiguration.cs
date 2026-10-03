using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>Connection mode of every connection the run opens.</summary>
    internal enum ExplorerMode { Direct, Shared }

    /// <summary>Maintenance a contender actor performs while another actor's operation is active.</summary>
    internal enum ExplorerMaintenance { None, Close, Rebuild, Fatal }

    /// <summary>What a user callback (input sequence, ReadTransform, upload stream) does when it runs.</summary>
    internal enum ExplorerCallback { None, SameConnection, Peer, OtherFile, Dispose }

    /// <summary>Whether another process writes the same file during the run (Shared only).</summary>
    internal enum ExplorerProcess { Single, ExternalWriter }

    /// <summary>
    /// One point of the dimension matrix. The signature is stable text
    /// (<c>mode=shared;access=legacy;maintenance=close;callback=peer;process=single;encrypted=false</c>)
    /// that appears in histories, failure artifacts, outcomes and known-finding fingerprints.
    /// </summary>
    internal sealed class ExplorerConfiguration
    {
        public ExplorerMode Mode { get; set; }
        /// <summary>Access kind name resolved by <see cref="ExplorerAccessKinds"/> (ordinary, legacy, handle, ...).</summary>
        public string Access { get; set; } = "ordinary";
        public ExplorerMaintenance Maintenance { get; set; }
        public ExplorerCallback Callback { get; set; }
        public ExplorerProcess Process { get; set; }
        public bool Encrypted { get; set; }

        public bool Shared => this.Mode == ExplorerMode.Shared;

        public string Signature =>
            "mode=" + Name(this.Mode) + ";access=" + this.Access + ";maintenance=" + Name(this.Maintenance) +
            ";callback=" + Name(this.Callback) + ";process=" + Name(this.Process) +
            ";encrypted=" + (this.Encrypted ? "true" : "false");

        public override string ToString() => this.Signature;

        public ExplorerConfiguration With(Action<ExplorerConfiguration> change)
        {
            var copy = (ExplorerConfiguration)this.MemberwiseClone();
            change(copy);
            return copy;
        }

        public static ExplorerConfiguration Parse(string signature)
        {
            var result = new ExplorerConfiguration();
            foreach (var pair in Pairs(signature))
            {
                switch (pair.Key)
                {
                    case "mode": result.Mode = Enum<ExplorerMode>(pair.Value); break;
                    case "access": result.Access = pair.Value; break;
                    case "maintenance": result.Maintenance = Enum<ExplorerMaintenance>(pair.Value); break;
                    case "callback": result.Callback = Enum<ExplorerCallback>(pair.Value); break;
                    case "process": result.Process = Enum<ExplorerProcess>(pair.Value); break;
                    case "encrypted": result.Encrypted = pair.Value == "true"; break;
                }
            }
            return result;
        }

        /// <summary>Lower-case, hyphenated dimension value (SameConnection becomes same-connection).</summary>
        internal static string Name<T>(T value) where T : struct
        {
            var text = value.ToString();
            var chars = new List<char>();
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsUpper(text[i]) && i > 0) chars.Add('-');
                chars.Add(char.ToLowerInvariant(text[i]));
            }
            return new string(chars.ToArray());
        }

        internal static T Enum<T>(string name) where T : struct =>
            System.Enum.GetValues(typeof(T)).Cast<T>().FirstOrDefault(value => Name(value) == name);

        internal static IEnumerable<KeyValuePair<string, string>> Pairs(string text) => (text ?? "")
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(new[] { '=' }, 2))
            .Where(part => part.Length == 2)
            .Select(part => new KeyValuePair<string, string>(part[0].Trim(), part[1].Trim()));
    }

    /// <summary>
    /// A schedule vector: the scenario, its variant (which permutation and release point the
    /// controller forces), the configuration and the seed bit. Class 1 evidence: rerunning a
    /// vector replays the same scheduling decisions.
    /// </summary>
    internal sealed class ExplorerVector
    {
        public string Scenario { get; set; }
        public int Variant { get; set; }
        public int Seed { get; set; }
        public ExplorerConfiguration Configuration { get; set; } = new ExplorerConfiguration();

        public override string ToString() =>
            "scenario=" + this.Scenario + ";variant=" + this.Variant.ToString(CultureInfo.InvariantCulture) +
            ";seed=" + this.Seed.ToString(CultureInfo.InvariantCulture) + ";" + this.Configuration.Signature;

        public static ExplorerVector Parse(string text)
        {
            var vector = new ExplorerVector { Configuration = ExplorerConfiguration.Parse(text) };
            foreach (var pair in ExplorerConfiguration.Pairs(text))
            {
                if (pair.Key == "scenario") vector.Scenario = pair.Value;
                else if (pair.Key == "variant") vector.Variant = int.Parse(pair.Value, CultureInfo.InvariantCulture);
                else if (pair.Key == "seed") vector.Seed = int.Parse(pair.Value, CultureInfo.InvariantCulture);
            }
            return vector;
        }
    }
}
