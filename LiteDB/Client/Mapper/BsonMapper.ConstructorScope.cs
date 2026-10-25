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
            private readonly BsonDocument _document;
            private readonly MemberMapper[] _members;
            private readonly object[] _arguments;

            public ConstructorScope Scope { get; }

            public ConstructorMembers(ConstructorScope scope, BsonDocument document, MemberMapper[] members,
                object[] arguments)
            {
                Scope = scope;
                _document = document;
                _members = members;
                _arguments = arguments;
            }

            // The argument is only the member's stored value while populating from
            // the document the constructor read; an overriding mapper may pass another.
            public bool TryGetArgument(MemberMapper member, BsonDocument document, out object argument)
            {
                var index = ReferenceEquals(document, _document) ? Array.IndexOf(_members, member) : -1;
                argument = index < 0 ? null : _arguments[index];
                return index >= 0;
            }
        }

        private ConstructorMembers GetConstructorMembers(object instance)
        {
            var scope = _constructorScope.Value;
            return scope != null && instance != null && _constructorMembers.TryGetValue(instance, out var entry) &&
                entry.Scope == scope ? entry : null;
        }

        // A faithful constructor already holds the stored value, so setting it again would only
        // repeat setter side effects (#2303). Anything else - an ignored or transformed argument,
        // an unreadable member, a copied collection - takes the setter like a parameterless type.
        private static bool HoldsValue(MemberMapper member, object instance, object value)
        {
            if (member.Getter == null) return false;

            try
            {
                return Equals(member.Getter(instance), value);
            }
            catch (Exception)
            {
                // a getter that cannot run on the fresh instance proves nothing: set the member, as before #2303
                return false;
            }
        }

        private void RemoveConstructorMembers(object instance, ConstructorScope scope)
        {
            if (instance != null && scope != null && _constructorMembers.TryGetValue(instance, out var entry) && entry.Scope == scope)
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

            public void Register(object instance, BsonDocument document, MemberMapper[] members, object[] arguments)
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    _mapper._constructorMembers.Add(instance,
                        new ConstructorMembers(this, document, members, arguments));
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
