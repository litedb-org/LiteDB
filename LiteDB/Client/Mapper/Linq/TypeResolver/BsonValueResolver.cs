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
        public string ResolveMethod(MethodInfo method) => null;

        public string ResolveMember(MemberInfo member)
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
                case "AsGuid": return "#";

                case "IsNull": return "IS_NULL(#)";
                case "IsArray": return "IS_ARRAY(#)";
                case "IsDocument": return "IS_DOCUMENT(#)";
                case "IsInt32": return "IS_INT32(#)";
                case "IsInt64": return "IS_INT64(#)";
                case "IsDouble": return "IS_DOUBLE(#)";
                case "IsDecimal": return "IS_DECIMAL(#)";
                case "IsNumber": return "IS_NUMBER(#)";
                case "IsBinary": return "IS_BINARY(#)";
                case "IsBoolean": return "IS_BOOLEAN(#)";
                case "IsString": return "IS_STRING(#)";
                case "IsObjectId": return "IS_OBJECTID(#)";
                case "IsGuid": return "IS_GUID(#)";
                case "IsDateTime": return "IS_DATETIME(#)";
                case "IsMinValue": return "IS_MINVALUE(#)";
                case "IsMaxValue": return "IS_MAXVALUE(#)";
            };

            return null;
        }

        public string ResolveCtor(ConstructorInfo ctor) => null;
    }
}