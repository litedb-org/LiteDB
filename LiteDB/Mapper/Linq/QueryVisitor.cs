using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using ParameterDictionary = System.Collections.Generic.Dictionary<System.Linq.Expressions.ParameterExpression, LiteDB.BsonValue>;

namespace LiteDB
{
    /// <summary>
    /// Class helper to create Queries based on Linq expressions
    /// </summary>
    internal class QueryVisitor<T>
    {
        private BsonMapper _mapper;
        private Type _type;
        private ParameterDictionary _parameters = new ParameterDictionary();
        private ParameterExpression _param = null;

        public QueryVisitor(BsonMapper mapper)
        {
            _mapper = mapper;
            _type = typeof(T);
        }

        public Query Visit(Expression<Func<T, bool>> predicate)
        {
            var lambda = predicate as LambdaExpression;

            _param = lambda.Parameters[0];

            return this.VisitExpression(lambda.Body);
        }

        private Query VisitExpression(Expression expr, string prefix = null)
        {
            try
            {
                // Single: x.Active
                if (expr is MemberExpression && expr.Type == typeof(bool))
                {
                    return Query.EQ(this.GetField(expr, prefix), new BsonValue(true));
                }
                // Not: !x.Active or !(x.Id == 1)
                else if (expr.NodeType == ExpressionType.Not)
                {
                    var unary = expr as UnaryExpression;

                    if (unary == null)
                    {
                        Console.WriteLine("Conversion expression {0} to Unary failed (Not)", expr);
                    }
                    else if (unary.Operand == null)
                    {
                        Console.WriteLine("Operand of Unary {0} expression {1} is null", unary, expr);
                    }

                    return Query.Not(this.VisitExpression(unary.Operand, prefix));
                }
                // Equals: x.Id == 1
                else if (expr.NodeType == ExpressionType.Equal)
                {
                    var bin = expr as BinaryExpression;

                    if (bin == null)
                    {
                        Console.WriteLine("Conversion expression {0} to Binary failed (Equal)", expr);
                    }
                    else if (bin.Right == null)
                    {
                        Console.WriteLine("Right side of binary expression {0} and expression {1} is null", bin, expr);
                    }
                    else if (bin.Left == null)
                    {
                        Console.WriteLine("Left side of binary expression {0} and expression {1} is null", bin, expr);
                    }

                    return new QueryEquals(this.GetField(bin.Left, prefix), this.VisitValue(bin.Right, bin.Left));
                }
                // NotEquals: x.Id != 1
                else if (expr.NodeType == ExpressionType.NotEqual)
                {
                    var bin = expr as BinaryExpression;
                    return Query.Not(this.GetField(bin.Left, prefix), this.VisitValue(bin.Right, bin.Left));
                }
                // LessThan: x.Id < 5
                else if (expr.NodeType == ExpressionType.LessThan)
                {
                    var bin = expr as BinaryExpression;
                    return Query.LT(this.GetField(bin.Left, prefix), this.VisitValue(bin.Right, bin.Left));
                }
                // LessThanOrEqual: x.Id <= 5
                else if (expr.NodeType == ExpressionType.LessThanOrEqual)
                {
                    var bin = expr as BinaryExpression;
                    return Query.LTE(this.GetField(bin.Left, prefix), this.VisitValue(bin.Right, bin.Left));
                }
                // GreaterThan: x.Id > 5
                else if (expr.NodeType == ExpressionType.GreaterThan)
                {
                    var bin = expr as BinaryExpression;
                    return Query.GT(this.GetField(bin.Left, prefix), this.VisitValue(bin.Right, bin.Left));
                }
                // GreaterThanOrEqual: x.Id >= 5
                else if (expr.NodeType == ExpressionType.GreaterThanOrEqual)
                {
                    var bin = expr as BinaryExpression;
                    return Query.GTE(this.GetField(bin.Left, prefix), this.VisitValue(bin.Right, bin.Left));
                }
                // And: x.Id > 1 && x.Name == "John"
                else if (expr.NodeType == ExpressionType.AndAlso)
                {
                    var bin = expr as BinaryExpression;

                    if (bin == null)
                    {
                        Console.WriteLine("Conversion expression {0} to Binary failed (AndAlso)", expr);
                    }
                    else if (bin.Right == null)
                    {
                        Console.WriteLine("Right side of binary expression {0} and expression {1} is null (AndAlso)", bin, expr);
                    }
                    else if (bin.Left == null)
                    {
                        Console.WriteLine("Left side of binary expression {0} and expression {1} is null (AndAlso)", bin, expr);
                    }

                    var left = this.VisitExpression(bin.Left, prefix);
                    var right = this.VisitExpression(bin.Right, prefix);

                    if (left == null)
                    {
                        Console.WriteLine("Conversion left of Binary {0} expression {1} to Query failed", bin.Left, expr);
                    }

                    if (right == null)
                    {
                        Console.WriteLine("Conversion right of Binary {0} expression {1} to Query failed", bin.Right, expr);
                    }

                    return Query.And(left, right);
                }
                // Or: x.Id == 1 || x.Name == "John"
                else if (expr.NodeType == ExpressionType.OrElse)
                {
                    var bin = expr as BinaryExpression;
                    var left = this.VisitExpression(bin.Left);
                    var right = this.VisitExpression(bin.Right);

                    return Query.Or(left, right);
                }
                // Constant: do nothing (at this point it's useful only to PredicateBuilder)
                else if (expr.NodeType == ExpressionType.Constant)
                {
                    var constant = expr as ConstantExpression;

                    if (constant.Value is bool)
                    {
                        var value = (bool)constant.Value;

                        return value ? Query.All() : new QueryEmpty();
                    }
                }
                // Invoke: call inner Lambda expression (used in PredicateBuilder)
                else if (expr.NodeType == ExpressionType.Invoke)
                {
                    var invocation = expr as InvocationExpression;
                    var lambda = invocation.Expression as LambdaExpression;
                    return this.VisitExpression(lambda.Body);
                }
                // MethodCall: x.Name.StartsWith("John")
                else if (expr is MethodCallExpression)
                {
                    var met = expr as MethodCallExpression;
                    var method = met.Method.Name;
// #if HAVE_TYPE_INFO
                    var type = met.Method.DeclaringType;
// #else
//                     var type = met.Method.ReflectedType;
// #endif
                    var paramType = met.Arguments[0] is MemberExpression ? (ExpressionType?)(met.Arguments[0] as MemberExpression).Expression.NodeType : null;

                    // StartsWith
                    if (method == "StartsWith")
                    {
                        var value = this.VisitValue(met.Arguments[0], null);

                        return Query.StartsWith(this.GetField(met.Object, prefix), value);
                    }
                    // Equals
                    else if (method == "Equals")
                    {
                        var value = this.VisitValue(met.Arguments[0], null);

                        return Query.EQ(this.GetField(met.Object, prefix), value);
                    }
                    // Contains (String): x.Name.Contains("auricio")
                    else if (method == "Contains" && type == typeof(string))
                    {
                        var value = this.VisitValue(met.Arguments[0], null);

                        return Query.Contains(this.GetField(met.Object, prefix), value);
                    }
                    // Contains (Enumerable): x.ListNumber.Contains(2)
                    else if (method == "Contains" && type == typeof(Enumerable))
                    {
                        var field = this.GetField(met.Arguments[0], prefix);
                        var value = this.VisitValue(met.Arguments[1], null);

                        return Query.EQ(field, value);
                    }
                    // Any (Enumerable): x.Customer.Any(z => z.Name.StartsWith("John"))
                    else if (method == "Any" && type == typeof(Enumerable) && paramType == ExpressionType.Parameter)
                    {
                        var field = this.GetField(met.Arguments[0]);
                        var lambda = met.Arguments[1] as LambdaExpression;

                        return this.VisitExpression(lambda.Body, field + ".");
                    }
                    // System.Linq.Enumerable methods (constant list items)
                    else if (type == typeof(Enumerable))
                    {
                        return ParseEnumerableExpression(met);
                    }
                }

                return new QueryLinq<T>(expr, _param, _mapper);
            }
            catch (NotSupportedException)
            {
                // when there is no linq implementation, use QueryLinq
                return new QueryLinq<T>(expr, _param, _mapper);
            }
        }

        private BsonValue VisitValue(Expression expr, Expression left)
        {
            // check if left side is an enum and convert to string before return
            Func<Type, object, BsonValue> convert = (type, value) =>
            {
                Type enumType = null;
                try
                {
                    enumType = (left as UnaryExpression) == null ? null : (left as UnaryExpression).Operand.Type;

                    if (enumType != null && enumType.GetTypeInfo().IsEnum)
                    {
                        var str = Enum.GetName(enumType, value);
                        return _mapper.Serialize(typeof(string), str, 0);
                    }

                    return _mapper.Serialize(type, value, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        "Failed to serialize value in QueryVisitor. Type={0}, Value={1}, EnumType={2}, LeftNodeType={3}, Exception={4}",
                        type?.FullName,
                        value,
                        enumType?.FullName,
                        left?.NodeType,
                        ex
                    );

                    throw;
                }
            };

            // its a constant; Eg: "fixed string"
            if (expr is ConstantExpression)
            {
                var value = (expr as ConstantExpression);

                return convert(value.Type, value.Value);
            }
            else if (expr is MemberExpression && _parameters.Count > 0)
            {
                var mExpr = (MemberExpression)expr;
                var mValue = this.VisitValue(mExpr.Expression, left);
                var value = mValue.AsDocument[mExpr.Member.Name];

                return convert(typeof(object), value);
            }
            else if (expr is ParameterExpression)
            {
                BsonValue result;
                if (_parameters.TryGetValue((ParameterExpression)expr, out result))
                {
                    return result;
                }
            }

            // execute expression
            object getterValue = null;
            try
            {
                var objectMember = Expression.Convert(expr, typeof(object));
                var getterLambda = Expression.Lambda<Func<object>>(objectMember);
                var getter = getterLambda.Compile();

                getterValue = getter();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "QueryVisitor getter failed. ExprType={0}, Expr={1}, ExprNodeType={2}, exception {3}",
                    expr?.Type,
                    expr,
                    expr?.NodeType,
                    ex
                );
                throw;
            }

            return convert(typeof(object), getterValue);
        }

        private Query CreateAndQuery(ref Query[] queries, int startIndex = 0)
        {
            var length = queries.Length - startIndex;

            if (length == 0) return new QueryEmpty();

            if (length == 1)
            {
                return queries[startIndex];
            }
            else
            {
                return Query.And(queries[startIndex], CreateOrQuery(ref queries, startIndex += 1));
            }
        }

        private Query CreateOrQuery(ref Query[] queries, int startIndex = 0)
        {
            var length = queries.Length - startIndex;

            if (length == 0) return new QueryEmpty();

            if (length == 1)
            {
                return queries[startIndex];
            }
            else
            {
                return Query.Or(queries[startIndex], CreateOrQuery(ref queries, startIndex += 1));
            }
        }

        private Query ParseEnumerableExpression(MethodCallExpression expr)
        {
            if (expr.Method.DeclaringType != typeof(Enumerable))
                throw new NotSupportedException("Cannot parse methods outside the System.Linq.Enumerable class.");

            var lambda = (LambdaExpression)expr.Arguments[1];
            var values = this.VisitValue(expr.Arguments[0], null).AsArray;
            var queries = new Query[values.Count];

            for (var i = 0; i < queries.Length; i++)
            {
                _parameters[lambda.Parameters[0]] = values[i];
                queries[i] = this.VisitExpression(lambda.Body);
            }

            _parameters.Remove(lambda.Parameters[0]);

            if (expr.Method.Name == "Any")
            {
                return CreateOrQuery(ref queries);
            }
            else if (expr.Method.Name == "All")
            {
                return CreateAndQuery(ref queries);
            }

            throw new NotSupportedException("Not implemented System.Linq.Enumerable method");
        }

        /// <summary>
        /// Based on a LINQ expression, returns document field mapped from class Property.
        /// Support multi level dotted notation: x => x.Customer.Name
        /// Prefix is used on array expression like: x => x.Customers.Any(z => z.Name == "John")
        /// </summary>
        public string GetField(Expression expr, string prefix = "", bool showArrayItems = false)
        {
            var property = prefix + expr.GetPath();
            var parts = property.Split('.');
            var fields = new string[parts.Length];
            var type = _type;
            var isdbref = false;

            // loop "first.second.last"
            for (var i = 0; i < parts.Length; i++)
            {
                var entity = _mapper.GetEntityMapper(type);
                var part = parts[i];
                var prop = entity.Members.Find(x => x.MemberName == part);

                if (prop == null) throw new NotSupportedException(property + " not mapped in " + type.Name);

                // if property is an IEnumerable, gets underlying type (otherwise, gets PropertyType)
                type = prop.UnderlyingType;

                fields[i] = prop.FieldName;

                if (showArrayItems && prop.IsList)
                {
                    fields[i] += "[*]";
                }

                if (prop.FieldName == "_id" && isdbref)
                {
                    isdbref = false;
                    fields[i] = "$id";
                }

                // if this property is DbRef, so if next property is _id, change to $id
                if (prop.IsDbRef) isdbref = true;
            }

            return string.Join(".", fields);
        }

        /// <summary>
        /// Convert a LINQ expression into a JSON path.
        /// x => x.Name ==> "$.Name"
        /// x => x.Items[0].Day ==> "$.Items[0].Day"
        /// x => x.Items[0].Day ==> "$.Items[0].Day"
        /// </summary>
        public string GetPath(Expression expr)
        {
            return "$." + this.GetField(expr, "", true);
        }
    }
}