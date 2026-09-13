using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Helper class to modify your entity mapping to document. Can be used instead attribute decorates
    /// </summary>
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
        /// Define which property will not be mapped to document
        /// </summary>
        public EntityBuilder<T> Ignore<K>(Expression<Func<T, K>> member)
        {
            return this.GetMember(member, (p) =>
            {
                _entity.WaitForInitialization();
                _entity.Members.Remove(p);
            });
        }

        /// <summary>
        /// Define a custom name for a property when mapping to document
        /// </summary>
        public EntityBuilder<T> Field<K>(Expression<Func<T, K>> member, string field)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return this.GetMember(member, (p) =>
            {
                p.FieldName = field;
            });
        }

        /// <summary>
        /// Define which property is your document id (primary key). Define if this property supports auto-id
        /// </summary>
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
        /// Define which property is your document id (primary key). Define if this property supports auto-id
        /// </summary>
        public EntityBuilder<T> Ctor(Func<BsonDocument, T> createInstance)
        {
            _entity.WaitForInitialization();
            _entity.CreateInstance = v => createInstance(v);

            return this;
        }

        /// <summary>
        /// Define a subdocument (or a list of) as a reference
        /// </summary>
        public EntityBuilder<T> DbRef<K>(Expression<Func<T, K>> member, string collection = null)
        {
            return this.GetMember(member, (p) =>
            {
                BsonMapper.RegisterDbRef(_mapper, p, _typeNameBinder, collection ?? _mapper.ResolveCollectionName(typeof(K)));
            });
        }

        /// <summary>
        /// Get a property based on a expression. Eg.: 'x => x.UserId' return string "UserId"
        /// </summary>
        private EntityBuilder<T> GetMember<TK, K>(Expression<Func<TK, K>> member, Action<MemberMapper> action)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));
            _entity.WaitForInitialization();
            
            var memb = _entity.GetMember(member);

            if (memb == null)
            {
                throw new ArgumentNullException($"Member '{member.GetPath()}' not found in type '{_entity.ForType.Name}' (use IncludeFields in BsonMapper)");
            }

            action(memb);

            return this;
        }
    }
}