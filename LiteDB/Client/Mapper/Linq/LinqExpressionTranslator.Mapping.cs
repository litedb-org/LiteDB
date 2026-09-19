using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace LiteDB
{
    internal sealed partial class LinqExpressionTranslator
    {
        private static readonly Dictionary<Type, ITypeResolver> _resolvers = new Dictionary<Type, ITypeResolver>
        {
            [typeof(BsonValue)] = new BsonValueResolver(),
            [typeof(BsonArray)] = new BsonValueResolver(),
            [typeof(BsonDocument)] = new BsonValueResolver(),
            [typeof(Convert)] = new ConvertResolver(),
            [typeof(DateTime)] = new DateTimeResolver(),
            [typeof(Int32)] = new NumberResolver("INT32"),
            [typeof(Int64)] = new NumberResolver("INT64"),
            [typeof(Decimal)] = new NumberResolver("DECIMAL"),
            [typeof(Double)] = new NumberResolver("DOUBLE"),
            [typeof(ICollection)] = new ICollectionResolver(),
            [typeof(IGrouping<,>)] = new GroupingResolver(),
            [typeof(Enumerable)] = new EnumerableResolver(),
            [typeof(MemoryExtensions)] = new MemoryExtensionsResolver(),
            [typeof(Guid)] = new GuidResolver(),
            [typeof(Math)] = new MathResolver(),
            [typeof(Regex)] = new RegexResolver(),
            [typeof(ObjectId)] = new ObjectIdResolver(),
            [typeof(String)] = new StringResolver(),
            [typeof(Nullable)] = new NullableResolver()
        };

        private static bool TryGetResolver(Type declaringType, out ITypeResolver typeResolver)
        {
            // get method declaring type - if is from any kind of list, read as Enumerable
            var isGrouping = declaringType?.IsGenericType == true && declaringType.GetGenericTypeDefinition() == typeof(IGrouping<,>);
            var isCollection = Reflection.IsCollection(declaringType);
            var isEnumerable = Reflection.IsEnumerable(declaringType);
            var isNullable = Reflection.IsNullable(declaringType);

            var type =
                isGrouping ? typeof(IGrouping<,>) :
                isCollection ? typeof(ICollection) :
                isEnumerable ? typeof(Enumerable) :
                isNullable ? typeof(Nullable) :
                declaringType;

            return _resolvers.TryGetValue(type, out typeResolver);
        }

        private static string Operator(ExpressionType nodeType)
        {
            switch (nodeType)
            {
                case ExpressionType.Add: return "+";
                case ExpressionType.Multiply: return "*";
                case ExpressionType.Subtract: return "-";
                case ExpressionType.Divide: return "/";
                case ExpressionType.Equal: return "=";
                case ExpressionType.NotEqual: return "!=";
                case ExpressionType.GreaterThan: return ">";
                case ExpressionType.GreaterThanOrEqual: return ">=";
                case ExpressionType.LessThan: return "<";
                case ExpressionType.LessThanOrEqual: return "<=";
                case ExpressionType.And: return "AND";
                case ExpressionType.AndAlso: return "AND";
                case ExpressionType.Or: return "OR";
                case ExpressionType.OrElse: return "OR";
            }

            throw new NotSupportedException($"Operator not supported {nodeType}");
        }

        private string ResolveMember(MemberInfo member, Type mappedType, out MemberMapper memberMapper)
        {
            var name = member.Name;

            // checks if parent field are not DbRef (checks for same dataType)
            var isParentDbRef = _dbRefType != null && member.DeclaringType.IsAssignableFrom(_dbRefType);

            // get class entity from mapper
            var entity = _mapper.GetEntityMapper(mappedType);
            entity.WaitForInitialization();

            // get mapped field from entity
            var field = entity.FindMember(member);

            memberMapper = field ?? throw new NotSupportedException($"Member {name} not found on BsonMapper for type {mappedType}.");
            // define if this field are DbRef (child will need check parent)
            _dbRefType = field.IsDbRef ? field.UnderlyingType : null;

            // if parent call is DbRef and are calling _id field, rename to $id
            var fieldName = _mapper.ResolveAbstractIdField(entity, field);
            MemberGuards?.Add(new LinqMemberGuard(_mapper, entity, field, fieldName));
            return isParentDbRef && fieldName == "_id" ? "$id" : fieldName;
        }

        internal static object Evaluate(Expression expression, params Type[] validTypes)
        {
            object value;
            if (expression is ConstantExpression constant)
            {
                value = constant.Value;
            }
            else if (expression is MemberExpression member)
            {
                var target = member.Expression == null ? null : Evaluate(member.Expression);
                value = member.Member is FieldInfo field ? field.GetValue(target) : ((PropertyInfo)member.Member).GetValue(target);
            }
            else
            {
                value = Expression.Lambda(expression).Compile().DynamicInvoke();
            }
            if (validTypes.Length > 0 && (value == null || !validTypes.Contains(value.GetType())))
                throw new NotSupportedException($"Expression {expression} must return one of: {string.Join(", ", validTypes.Select(x => x.Name))}");
            return value;
        }
    }
}
