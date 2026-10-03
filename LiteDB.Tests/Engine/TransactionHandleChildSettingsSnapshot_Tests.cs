using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// <see cref="EngineSettings.SnapshotForTransactionHolder"/> copies every setting a Shared
    /// holder's child engine needs, or declares why not. A new <see cref="EngineSettings"/>
    /// property or field fails the test until it is classified here.
    /// </summary>
    public class TransactionHandleChildSettingsSnapshot_Tests
    {
        private enum Rule { Copied, CopiedByValue, Wiring }

        /// <summary>Classification of every instance property of <see cref="EngineSettings"/>.</summary>
        private static readonly Dictionary<string, (Rule Rule, string Reason)> Properties = new Dictionary<string, (Rule, string)>
        {
            ["CompactStorage"] = (Rule.Copied, null),
            ["MemoryProfile"] = (Rule.Copied, null),
            // Shared mode rejects caller streams before a holder exists; copied unchanged.
            ["DataStream"] = (Rule.Copied, null),
            ["LogStream"] = (Rule.Copied, null),
            ["TempStream"] = (Rule.Copied, null),
            ["Filename"] = (Rule.Copied, null),
            ["Password"] = (Rule.Copied, null),
            ["InitialSize"] = (Rule.Copied, null),
            ["IndexMigrationLimitSize"] = (Rule.Copied, null),
            ["CacheSize"] = (Rule.Copied, null),
            ["TransactionPageLimit"] = (Rule.Copied, null),
            ["ReadOnly"] = (Rule.Copied, null),
            ["LegacyIndexScan"] = (Rule.Copied, null),
            ["AutoRebuild"] = (Rule.Copied, null),
            ["Upgrade"] = (Rule.Copied, null),
            ["RejectInvalidLocalTime"] = (Rule.Copied, null),
            ["DurableCommits"] = (Rule.Copied, null),
            ["LocalTimeZone"] = (Rule.Copied, null),
            // Copied here; OpenTransactionResources then replaces it with a weak wrapper.
            ["ReadTransform"] = (Rule.Copied, null),
            ["SharedMutexNameStrategy"] = (Rule.Copied, null),
            ["SharedReaderFiles"] = (Rule.Copied, null),
            ["CheckpointStage"] = (Rule.Copied, null),
            ["SharedWriterTimeout"] = (Rule.Copied, null),
            ["SharedSelfWaitGrace"] = (Rule.Copied, null),
            ["SharedSlowWaitThreshold"] = (Rule.Wiring, "The child's waits are recorded and reported by the parent connection's recorder, with the parent's threshold."),
            ["SharedSlowWait"] = (Rule.Wiring, "Cleared by the holder: the parent connection's recorder reports the child's waits; the holder must not root the observer."),
            ["Collation"] = (Rule.CopiedByValue, "Serialized policy only: the application's Collation object (and its Culture) must not be rooted by the holder."),
            ["SharedReaderVersions"] = (Rule.Wiring, "Assigned by the child SharedEngine's constructor from its own reader registry."),
            ["AutoRebuildAllowed"] = (Rule.Wiring, "Assigned by the child SharedEngine's constructor."),
            ["CheckpointBackoff"] = (Rule.Wiring, "Replaced by the parent's back-off after the child is created."),
            ["SharedReadSnapshot"] = (Rule.Wiring, "Set only on snapshot read engines; a holder writes."),
            ["SharedDurability"] = (Rule.Wiring, "Replaced by the parent's durability state after the child is created."),
            ["SharedFileHandles"] = (Rule.Wiring, "Cleared by the holder: the child opens its own handles."),
            ["CloseCheckpointPages"] = (Rule.Wiring, "Assigned by the child SharedEngine's constructor."),
            ["CoordinationSignals"] = (Rule.Wiring, "Cleared by the holder: a holder never signals as a coordinator."),
        };

        /// <summary>Instance fields that are not auto-property backing fields.</summary>
        private static readonly Dictionary<string, string> Fields = new Dictionary<string, string>
        {
            ["_transactionPageLimit"] = "Backing field of TransactionPageLimit, compared through the property.",
            ["_sharedWriterTimeout"] = "Backing field of SharedWriterTimeout, compared through the property.",
            ["_sharedSelfWaitGrace"] = "Backing field of SharedSelfWaitGrace, compared through the property.",
            ["_sharedSlowWaitThreshold"] = "Backing field of SharedSlowWaitThreshold, classified through the property.",
        };

        // Present only in DEBUG/TESTING builds of LiteDB.
        private static readonly HashSet<string> TestingOnly = new HashSet<string> { "CheckpointStage" };

        private sealed class ApplicationSettings : EngineSettings
        {
            internal readonly object Facade = new object();
        }

        private sealed class Signals : ICoordinationSignals
        {
            public void StructuralBegin() { }
            public void StructuralEnd(int version) { }
            public void SlotReused() { }
            public void Committed(int version) { }
        }

        private static IEnumerable<PropertyInfo> AllProperties() => typeof(EngineSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        [Fact]
        public void Every_setting_is_classified()
        {
            var properties = AllProperties().Select(p => p.Name).ToArray();
            Assert.True(!properties.Except(Properties.Keys).Any(),
                "Classify the new EngineSettings properties: " + string.Join(", ", properties.Except(Properties.Keys)));
            var stale = Properties.Keys.Except(properties).Except(TestingOnly).ToArray();
            Assert.True(stale.Length == 0, "Stale classification: " + string.Join(", ", stale));
            var fields = typeof(EngineSettings).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.Name).Where(name => !name.EndsWith(">k__BackingField")).ToArray();
            Assert.True(!fields.Except(Fields.Keys).Any(), "Classify the new EngineSettings fields: " + string.Join(", ", fields.Except(Fields.Keys)));
            Assert.True(!Fields.Keys.Except(fields).Any(), "Stale field classification: " + string.Join(", ", Fields.Keys.Except(fields)));
            foreach (var entry in Properties.Where(p => p.Value.Rule != Rule.Copied))
                Assert.False(string.IsNullOrWhiteSpace(entry.Value.Reason), entry.Key + " needs a reason.");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Snapshot_copies_every_public_setting_or_declares_why_not(bool subclass)
        {
            var source = subclass ? new ApplicationSettings() : new EngineSettings();
            var defaults = new EngineSettings();
            foreach (var property in AllProperties())
            {
                var value = NonDefault(property, property.GetValue(defaults));
                property.SetValue(source, value);
                Assert.False(Equals(property.GetValue(defaults), property.GetValue(source)), property.Name + " kept its default.");
            }

            var snapshot = source.SnapshotForTransactionHolder();

            // Fields added by an application subclass are never retained.
            Assert.Equal(typeof(EngineSettings), snapshot.GetType());
            foreach (var property in AllProperties())
            {
                var (rule, _) = Properties[property.Name];
                object expected = property.GetValue(source), actual = property.GetValue(snapshot);
                switch (rule)
                {
                    case Rule.Copied:
                        Assert.True(Equals(expected, actual), $"{property.Name} was not copied: {expected} -> {actual}");
                        break;
                    case Rule.CopiedByValue:
                        Assert.NotSame(expected, actual);
                        Assert.Equal(expected.ToString(), actual.ToString());
                        break;
                    case Rule.Wiring:
                        // A subclass snapshot starts without the source's per-engine wiring; the
                        // exact-type clone carries it until the holder and child engine replace it.
                        if (subclass) Assert.True(Equals(property.GetValue(defaults), actual), $"{property.Name} retained: {actual}");
                        else Assert.True(Equals(expected, actual), $"{property.Name} was not cloned: {actual}");
                        break;
                }
            }
            GC.KeepAlive(((source as ApplicationSettings)?.Facade));
        }

        /// <summary>A value of the property's type that differs from <paramref name="current"/>.</summary>
        private static object NonDefault(PropertyInfo property, object current)
        {
            var type = property.PropertyType;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(bool)) return !(bool)(current ?? false);
            if (underlying == typeof(int)) return (int)(current ?? 0) + 7;
            if (underlying == typeof(long)) return (long)(current ?? 0L) + (1L << 20);
            if (underlying == typeof(string)) return "probe-" + property.Name;
            if (underlying.IsEnum)
                return Enum.GetValues(underlying).Cast<object>().First(value => !value.Equals(current));
            if (typeof(Stream).IsAssignableFrom(underlying)) return new MemoryStream();
            if (underlying == typeof(Collation)) return new Collation("en-US/None");
            if (underlying == typeof(TimeZoneInfo)) return TimeZoneInfo.Utc;
            if (underlying == typeof(TimeSpan)) return TimeSpan.FromMilliseconds(1234);
            if (underlying == typeof(ICoordinationSignals)) return new Signals();
            if (typeof(Delegate).IsAssignableFrom(underlying))
            {
                var invoke = underlying.GetMethod("Invoke");
                var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
                return Expression.Lambda(underlying, Expression.Default(invoke.ReturnType), parameters).Compile();
            }
            if (underlying.IsClass && !underlying.IsAbstract)
#if NETFRAMEWORK
                return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(underlying);
#else
                return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(underlying);
#endif
            throw new NotSupportedException("Add a non-default value for " + property.Name + " (" + type + ").");
        }
    }
}
