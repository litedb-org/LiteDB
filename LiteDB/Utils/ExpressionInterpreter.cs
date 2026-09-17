using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace LiteDB
{
    internal static partial class ExpressionInterpreter
    {
        internal static object Evaluate(Expression expression, IList<ParameterExpression> parameters, object[] arguments)
        {
            object Eval(Expression node) => node == null ? null : Evaluate(node, parameters, arguments);
            object[] Args(IEnumerable<Expression> nodes) => nodes.Select(Eval).ToArray();
            switch (expression.NodeType)
            {
                case ExpressionType.Constant: return ((ConstantExpression)expression).Value;
                case ExpressionType.Parameter:
                    var index = parameters.IndexOf((ParameterExpression)expression);
                    if (index < 0) throw new InvalidOperationException("Unbound expression parameter.");
                    return arguments[index];
                case ExpressionType.MemberAccess:
                    var member = (MemberExpression)expression;
                    var target = Eval(member.Expression);
                    if (Nullable.GetUnderlyingType(member.Member.DeclaringType) != null)
                        return NullableMember(member.Member.Name, member.Member.DeclaringType, target, Array.Empty<object>());
                    if (member.Expression != null && target == null) throw new NullReferenceException();
                    return member.Member is FieldInfo field ? field.GetValue(target) :
                        Invoke(((PropertyInfo)member.Member).GetMethod, target, Array.Empty<object>());
                case ExpressionType.Call:
                    var call = (MethodCallExpression)expression;
                    var receiver = Eval(call.Object);
                    var callArguments = Args(call.Arguments);
                    if (call.Object != null && Nullable.GetUnderlyingType(call.Object.Type) != null && call.Method.Name != "GetType")
                        return NullableMember(call.Method.Name, call.Object.Type, receiver, callArguments);
                    if (call.Object != null && receiver == null) throw new NullReferenceException();
                    return Invoke(call.Method, receiver, callArguments);
                case ExpressionType.New:
                    var create = (NewExpression)expression;
                    return create.Constructor == null ? Activator.CreateInstance(create.Type) : Invoke(create.Constructor, null, Args(create.Arguments));
                case ExpressionType.Invoke: return EvaluateInvocation((InvocationExpression)expression, parameters, arguments);
                case ExpressionType.ListInit:
                    var list = (ListInitExpression)expression;
                    var collection = Eval(list.NewExpression);
                    foreach (var initializer in list.Initializers) Invoke(initializer.AddMethod, collection, Args(initializer.Arguments));
                    return collection;
                case ExpressionType.NewArrayInit:
                case ExpressionType.NewArrayBounds:
                    var newArray = (NewArrayExpression)expression;
                    var values = Args(newArray.Expressions);
                    if (expression.NodeType == ExpressionType.NewArrayBounds)
                        return Array.CreateInstance(newArray.Type.GetElementType(), values.Select(Convert.ToInt32).ToArray());
                    var array = Array.CreateInstance(newArray.Type.GetElementType(), values.Length);
                    for (var i = 0; i < values.Length; i++) array.SetValue(values[i], i);
                    return array;
                case ExpressionType.Conditional:
                    var conditional = (ConditionalExpression)expression;
                    return Eval((bool)Eval(conditional.Test) ? conditional.IfTrue : conditional.IfFalse);
                case ExpressionType.AndAlso:
                    var and = (BinaryExpression)expression;
                    return (bool)Eval(and.Left) && (bool)Eval(and.Right);
                case ExpressionType.OrElse:
                    var or = (BinaryExpression)expression;
                    return (bool)Eval(or.Left) || (bool)Eval(or.Right);
                case ExpressionType.Coalesce:
                    var coalesce = (BinaryExpression)expression;
                    if (coalesce.Conversion != null) throw Unsupported(expression);
                    var present = Eval(coalesce.Left);
                    var selectedType = coalesce.Left.Type;
                    if (present == null)
                    {
                        present = Eval(coalesce.Right);
                        selectedType = coalesce.Right.Type;
                    }
                    return EvaluateUnary(Expression.Convert(Expression.Constant(present, selectedType), coalesce.Type), present);
                case ExpressionType.ArrayIndex:
                    var access = (BinaryExpression)expression;
                    return ((Array)Eval(access.Left)).GetValue((int)Eval(access.Right));
                case ExpressionType.Index:
                    var indexer = (IndexExpression)expression;
                    var indexed = Eval(indexer.Object);
                    var indexes = Args(indexer.Arguments);
                    if (indexed == null) throw new NullReferenceException();
                    return indexer.Indexer == null ? ((Array)indexed).GetValue(indexes.Select(Convert.ToInt32).ToArray()) :
                        Invoke(indexer.Indexer.GetMethod, indexed, indexes);
                case ExpressionType.ArrayLength: return ((Array)Eval(((UnaryExpression)expression).Operand)).Length;
                case ExpressionType.Assign:
                    var assign = (BinaryExpression)expression;
                    var destination = (MemberExpression)assign.Left;
                    var instance = Eval(destination.Expression);
                    var assigned = Eval(assign.Right);
                    if (destination.Expression != null && instance == null) throw new NullReferenceException();
                    if (destination.Member is FieldInfo assignedField) assignedField.SetValue(instance, assigned);
                    else Invoke(((PropertyInfo)destination.Member).SetMethod, instance, new[] { assigned });
                    return assigned;
                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                case ExpressionType.TypeAs:
                case ExpressionType.Not:
                case ExpressionType.Negate:
                case ExpressionType.NegateChecked:
                case ExpressionType.UnaryPlus:
                    return EvaluateUnary((UnaryExpression)expression, Eval(((UnaryExpression)expression).Operand));
                case ExpressionType.Quote: return ((UnaryExpression)expression).Operand;
                default:
                    if (expression is BinaryExpression binary && binary.Method != null && !binary.IsLifted)
                        return Invoke(binary.Method, null, new[] { Eval(binary.Left), Eval(binary.Right) });
                    throw Unsupported(expression);
            }
        }

        private static object Invoke(MethodBase method, object target, object[] arguments)
        {
            try
            {
                return method is ConstructorInfo constructor ? constructor.Invoke(arguments) : ((MethodInfo)method).Invoke(target, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static NotSupportedException Unsupported(Expression expression) =>
            new NotSupportedException("Expression node " + expression.NodeType + " is not supported by the AOT interpreter.");
    }
}
