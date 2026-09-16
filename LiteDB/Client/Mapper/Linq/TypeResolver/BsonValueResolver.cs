using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    internal class BsonValueResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method) => null;

        public LinqExpressionBinding ResolveMember(MemberInfo member)
        {
            switch (member.Name)
            {
                case "AsArray":
                case "AsDocument":
                case "AsBinary":
                case "AsBoolean":
                case "AsString":
                case "AsInt32":
                case "AsInt64":
                case "AsDouble":
                case "AsDecimal":
                case "AsDateTime":
                case "AsObjectId":
                case "AsGuid": return c => c.Object();

                case "IsNull": return c => c.Call("IS_NULL", c.Object());
                case "IsArray": return c => c.Call("IS_ARRAY", c.Object());
                case "IsDocument": return c => c.Call("IS_DOCUMENT", c.Object());
                case "IsInt32": return c => c.Call("IS_INT32", c.Object());
                case "IsInt64": return c => c.Call("IS_INT64", c.Object());
                case "IsDouble": return c => c.Call("IS_DOUBLE", c.Object());
                case "IsDecimal": return c => c.Call("IS_DECIMAL", c.Object());
                case "IsNumber": return c => c.Call("IS_NUMBER", c.Object());
                case "IsBinary": return c => c.Call("IS_BINARY", c.Object());
                case "IsBoolean": return c => c.Call("IS_BOOLEAN", c.Object());
                case "IsString": return c => c.Call("IS_STRING", c.Object());
                case "IsObjectId": return c => c.Call("IS_OBJECTID", c.Object());
                case "IsGuid": return c => c.Call("IS_GUID", c.Object());
                case "IsDateTime": return c => c.Call("IS_DATETIME", c.Object());
                case "IsMinValue": return c => c.Call("IS_MINVALUE", c.Object());
                case "IsMaxValue": return c => c.Call("IS_MAXVALUE", c.Object());
            };

            return null;
        }

        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}