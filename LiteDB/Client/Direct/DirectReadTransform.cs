using System;

namespace LiteDB.Client.Direct
{
    // Storage is rooted by the process host; application callbacks are rooted by
    // each facade instead. A callback may itself capture the public database.
    internal sealed class DirectReadTransform
    {
        private readonly WeakReference<Func<string, BsonValue, BsonValue>> _callback;
        private DirectReadTransform(Func<string, BsonValue, BsonValue> callback) =>
            _callback = new WeakReference<Func<string, BsonValue, BsonValue>>(callback);

        internal static Func<string, BsonValue, BsonValue> Wrap(Func<string, BsonValue, BsonValue> callback) =>
            callback == null ? null : new DirectReadTransform(callback).Invoke;

        internal static Func<string, BsonValue, BsonValue> Original(Func<string, BsonValue, BsonValue> callback) =>
            callback?.Target is DirectReadTransform weak ? weak.Get() : callback;

        private Func<string, BsonValue, BsonValue> Get() => _callback.TryGetTarget(out var callback) ? callback : null;
        private BsonValue Invoke(string collection, BsonValue value) =>
            (Get() ?? throw new ObjectDisposedException(nameof(LiteDatabase)))(collection, value);
    }
}
