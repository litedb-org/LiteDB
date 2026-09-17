using System;
using System.Collections.Generic;
using System.Threading;

namespace LiteDB
{
    public partial class BsonMapper
    {
        private readonly AsyncLocal<ConstructorScope> _constructorScope = new AsyncLocal<ConstructorScope>();

        private sealed class ConstructorMembers
        {
            public ConstructorScope Scope { get; }
            public MemberMapper[] Members { get; }
            public ConstructorMembers(ConstructorScope scope, MemberMapper[] members)
            {
                Scope = scope;
                Members = members;
            }
        }

        private void RemoveConstructorMembers(object instance, ConstructorScope scope)
        {
            if (instance != null && _constructorMembers.TryGetValue(instance, out var entry) && entry.Scope == scope)
                _constructorMembers.Remove(instance);
        }

        private sealed class ConstructorScope : IDisposable
        {
            private readonly BsonMapper _mapper;
            private readonly ConstructorScope _previous;
            private readonly object _sync = new object();
            private List<object> _instances;
            private bool _disposed;

            public ConstructorScope(BsonMapper mapper)
            {
                _mapper = mapper;
                _previous = mapper._constructorScope.Value;
                mapper._constructorScope.Value = this;
            }

            public void Register(object instance, MemberMapper[] members)
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    _mapper._constructorMembers.Add(instance, new ConstructorMembers(this, members));
                    (_instances ??= new List<object>()).Add(instance);
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    _disposed = true;
                    // A decorating factory may retain its inner result and throw,
                    // or return another object. Clean every instance it constructed.
                    if (_instances != null)
                    {
                        foreach (var instance in _instances) _mapper.RemoveConstructorMembers(instance, this);
                        _instances.Clear();
                    }
                }
                _mapper._constructorScope.Value = _previous;
            }
        }
    }
}
