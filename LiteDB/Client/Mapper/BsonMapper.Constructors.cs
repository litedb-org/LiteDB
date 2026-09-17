using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiteDB
{
    public partial class BsonMapper
    {
        private readonly ConditionalWeakTable<object, ConstructorMembers> _constructorMembers =
            new ConditionalWeakTable<object, ConstructorMembers>();

        private void PopulateMappedInstance(Type type, Type declaredType, object instance, BsonDocument doc)
        {
            // Hooks retain the full source document. Only base member population
            // suppresses constructor-bound setters, including decorated factories.
            try
            {
                if (instance is IDictionary dict)
                {
                    var schemaType = Reflection.IsDictionaryInterface(declaredType)
                        ? declaredType : !type.GetTypeInfo().IsInterface && instance.GetType().GetTypeInfo().IsGenericType &&
                            type.GetGenericArguments().Length >= 2 ? type : instance.GetType();
                    Reflection.GetDictionaryTypes(schemaType, out var keyType, out var valueType);

                    DeserializeDictionary(keyType, valueType, dict, doc);
                }
                else if (instance is System.Dynamic.ExpandoObject expando)
                {
                    var values = (System.Collections.Generic.IDictionary<string, object>)expando;
                    foreach (var element in doc.GetElements()) values[element.Key] = Deserialize(typeof(object), element.Value);
                }
                else
                {
                    DeserializeObject(type, instance, doc);
                }

            }
            finally
            {
                RemoveConstructorMembers(instance, _constructorScope.Value);
            }
        }

        private object CreateMappedInstance(Type type, EntityMapper entity, BsonDocument document,
            out bool complete)
        {
            complete = false;
            var instance = _typeInstantiator(type);
            var factory = entity.CreateInstance;

            if (instance == null && factory != null)
            {
                instance = factory(document);
            }

            if (instance == null && IsSystemIndexType(type))
            {
                complete = true;
                return DeserializeSystemIndex(type, document);
            }

            factory = entity.CreateInstance ?? GetTypeCtor(entity)
                ?? ((BsonDocument _) => Reflection.CreateInstance(entity.ForType));
            entity.CreateInstance = factory;

            if (instance == null)
            {
                instance = factory(document);
            }
            return instance;
        }

        private sealed class MappedConstructor
        {
            private readonly BsonMapper _mapper;
            private readonly ConstructorInfo _constructor;
            public MemberMapper[] Parameters { get; }

            public MappedConstructor(BsonMapper mapper, ConstructorInfo constructor, MemberMapper[] parameters)
            {
                _mapper = mapper;
                _constructor = constructor;
                Parameters = parameters;
            }

            public object Create(BsonDocument document)
            {
                var instance = _constructor.Invoke(Parameters.Select(member =>
                    member.Deserialize != null && document.TryGetValue(member.FieldName, out var value)
                        ? member.Deserialize(value, _mapper)
                        : _mapper.Deserialize(member.DataType, document[member.FieldName])).ToArray());
                _mapper._constructorScope.Value?.Register(instance, Parameters);
                return instance;
            }
        }
    }
}
