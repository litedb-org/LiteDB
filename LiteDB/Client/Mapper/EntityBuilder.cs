using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Provides a fluent API for configuring entity-to-document mapping without using attribute decorators.
    /// </summary>
    /// <typeparam name="T">The entity type to configure mapping for.</typeparam>
    /// <remarks>
    /// This class allows you to programmatically configure how entities are mapped to BSON documents,
    /// providing an alternative to using attributes like <c>[BsonId]</c>, <c>[BsonField]</c>, and <c>[BsonIgnore]</c>.
    /// </remarks>
    public class EntityBuilder<T>
    {
        private readonly BsonMapper _mapper;
        private readonly EntityMapper _entity;
        private readonly ITypeNameBinder _typeNameBinder;

        internal EntityBuilder(BsonMapper mapper, ITypeNameBinder typeNameBinder)
        {
            _mapper = mapper;
            _typeNameBinder = typeNameBinder;
            _entity = mapper.GetEntityMapper(typeof(T));
        }

        /// <summary>
        /// Excludes a property from being mapped to the document.
        /// </summary>
        /// <typeparam name="K">The type of the property to ignore.</typeparam>
        /// <param name="member">An expression identifying the property to ignore (e.g., <c>x => x.PropertyName</c>).</param>
        /// <returns>The current <see cref="EntityBuilder{T}"/> instance for method chaining.</returns>
        /// <remarks>
        /// This is the fluent API equivalent of the <c>[BsonIgnore]</c> attribute.
        /// </remarks>
        public EntityBuilder<T> Ignore<K>(Expression<Func<T, K>> member)
        {
            return this.GetMember(member, (p) =>
            {
                _entity.WaitForInitialization();
                _entity.Members.Remove(p);
            });
        }

        /// <summary>
        /// Specifies a custom field name for a property when mapping to the document.
        /// </summary>
        /// <typeparam name="K">The type of the property.</typeparam>
        /// <param name="member">An expression identifying the property (e.g., <c>x => x.PropertyName</c>).</param>
        /// <param name="field">The custom field name to use in the BSON document.</param>
        /// <returns>The current <see cref="EntityBuilder{T}"/> instance for method chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace.</exception>
        /// <remarks>
        /// This is the fluent API equivalent of the <c>[BsonField("fieldName")]</c> attribute.
        /// </remarks>
        public EntityBuilder<T> Field<K>(Expression<Func<T, K>> member, string field)
        {
            // TODO: Isn't it better to throw ArgumentException?
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return this.GetMember(member, (p) =>
            {
                p.FieldName = field;
            });
        }

        /// <summary>
        /// Designates a property as the document ID (primary key) and optionally enables auto-ID generation.
        /// </summary>
        /// <typeparam name="K">The type of the ID property.</typeparam>
        /// <param name="member">An expression identifying the ID property (e.g., <c>x => x.Id</c>).</param>
        /// <param name="autoId">If <see langword="true"/>, enables automatic ID generation for new documents.</param>
        /// <returns>The current <see cref="EntityBuilder{T}"/> instance for method chaining.</returns>
        /// <remarks>
        /// <para>This is the fluent API equivalent of the <c>[BsonId]</c> attribute.</para>
        /// <para>The property will be mapped to the <c>_id</c> field in the BSON document.</para>
        /// <para>If another property was previously configured as the ID, it will be reverted to a regular field.</para>
        /// </remarks>
        public EntityBuilder<T> Id<K>(Expression<Func<T, K>> member, bool autoId = true)
        {
            return this.GetMember(member, (p) =>
            {
                _entity.WaitForInitialization();
                
                // if contains another _id, remove-it
                var oldId = _entity.Members.FirstOrDefault(x => x.FieldName == "_id");
        
                if (oldId != null)
                {
                    oldId.FieldName = _mapper.ResolveFieldName(oldId.MemberName);
                    oldId.AutoId = false;
                }

                p.FieldName = "_id";
                p.AutoId = autoId;
            });
        }

        /// <summary>
        /// Specifies a custom constructor function for creating instances of type <typeparamref name="T"/> during deserialization.
        /// </summary>
        /// <param name="createInstance">A function that takes a <see cref="BsonDocument"/> and returns a new instance of <typeparamref name="T"/>.</param>
        /// <returns>The current <see cref="EntityBuilder{T}"/> instance for method chaining.</returns>
        /// <remarks>
        /// This allows you to use custom object initialization logic, dependency injection, or factory patterns
        /// when deserializing documents into entity instances.
        /// </remarks>
        public EntityBuilder<T> Ctor(Func<BsonDocument, T> createInstance)
        {
            _entity.WaitForInitialization();
            _entity.CreateInstance = v => createInstance(v);

            return this;
        }

        /// <summary>
        /// Configures a property as a database reference (DbRef), storing only the referenced document's ID rather than embedding the full document.
        /// </summary>
        /// <typeparam name="K">The type of the referenced entity or collection of entities.</typeparam>
        /// <param name="member">An expression identifying the property to configure as a DbRef (e.g., <c>x => x.RelatedEntity</c>).</param>
        /// <param name="collection">Optional collection name for the referenced documents. If <see langword="null"/>, the collection name is resolved from type <typeparamref name="K"/>.</param>
        /// <returns>The current <see cref="EntityBuilder{T}"/> instance for method chaining.</returns>
        /// <remarks>
        /// <para>This is the fluent API equivalent of the <c>[BsonRef("collectionName")]</c> attribute.</para>
        /// <para>
        /// DbRef properties are serialized as documents containing <c>$id</c> and <c>$ref</c> fields instead of embedding the full referenced document.
        /// This is useful for creating relationships between documents across collections.
        /// </para>
        /// <para>Supports both single references and collections (e.g., <c>List&lt;K&gt;</c>).</para>
        /// </remarks>
        public EntityBuilder<T> DbRef<K>(Expression<Func<T, K>> member, string collection = null)
        {
            return this.GetMember(member, (p) =>
            {
                BsonMapper.RegisterDbRef(_mapper, p, _typeNameBinder, collection ?? _mapper.ResolveCollectionName(typeof(K)));
            });
        }

        /// <summary>
        /// Gets a member mapper based on a lambda expression and executes an action on it.
        /// </summary>
        /// <typeparam name="TK">The source type for the expression.</typeparam>
        /// <typeparam name="K">The property type.</typeparam>
        /// <param name="member">An expression identifying the property (e.g., <c>x => x.PropertyName</c>).</param>
        /// <param name="action">The action to execute on the <see cref="MemberMapper"/>.</param>
        /// <returns>The current <see cref="EntityBuilder{T}"/> instance for method chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="member"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the member is not found in the entity type.</exception>
        /// <remarks>
        /// If the member is not found, ensure that <see cref="BsonMapper.IncludeFields"/> is configured appropriately
        /// if you're trying to map fields instead of properties.
        /// </remarks>
        private EntityBuilder<T> GetMember<TK, K>(Expression<Func<TK, K>> member, Action<MemberMapper> action)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));
            _entity.WaitForInitialization();
            
            var memb = _entity.GetMember(member);

            if (memb == null)
            {
                // TODO: Isn't it better to throw InvalidOperationException?
                throw new ArgumentNullException($"Member '{member.GetPath()}' not found in type '{_entity.ForType.Name}' (use IncludeFields in BsonMapper)");
            }

            action(memb);

            return this;
        }
    }
}