using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB
{
    public partial class BsonMapper
    {
        private const string SerializeAction = "serializing";
        private const string DeserializeAction = "deserializing";
        private const string UnsupportedMemberHint = " Use BsonIgnore or RegisterType for unsupported members.";
        private const string PathSeparator = " > ";

        /// <summary>
        /// Describe a member failure as one sentence: the member path from the outermost entity, then the original error.
        /// A failure that already carries a path (it happened in a nested entity) is extended, not wrapped again, so the
        /// original exception stays the direct InnerException however deep the member sits.
        /// </summary>
        private static LiteException MemberFailure(string action, Type entity, MemberMapper member, Exception error, BsonValue stored = null)
        {
            var segment = entity.Name + "." + member.MemberName;
            var nested = error as LiteException;
            var extends = nested?.MappingPath != null && nested.MappingAction == action;

            var path = extends ? new[] { segment }.Concat(nested.MappingPath).ToArray() : new[] { segment };
            var cause = extends ? nested.InnerException : error;
            var source = extends ? nested.MappingSource : stored?.Type.ToString();

            // a LiteException explains itself (depth limit, type not assignable); anything else is an unmappable member
            var hint = action == SerializeAction && !(cause is LiteException) ? UnsupportedMemberHint : "";
            var message = "Error " + action + " '" + FormatPath(path) + "'" + (source == null ? "" : " from " + source) + ": " +
                AsSentence(cause.Message) + hint;

            return new LiteException((cause as LiteException)?.ErrorCode ?? LiteException.MAPPING_ERROR, cause, message)
            {
                MappingAction = action,
                MappingPath = path,
                MappingSource = source
            };
        }

        /// <summary>
        /// A stored value of the wrong type passes through Deserialize unchanged and only fails in the compiled setter.
        /// Other exceptions belong to the user's setter and are left alone.
        /// </summary>
        private void SetMember(Type entity, MemberMapper member, object instance, object value, BsonValue stored)
        {
            try
            {
                member.Setter(instance, value);
            }
            catch (InvalidCastException error)
            {
                throw MemberFailure(DeserializeAction, entity, member, error, stored);
            }
        }

        /// <summary>
        /// "Node.Next > Node.Next > Node.Next" says nothing three times: repeated steps are counted instead.
        /// </summary>
        private static string FormatPath(IReadOnlyList<string> path)
        {
            var steps = new List<string>();

            for (var i = 0; i < path.Count;)
            {
                var run = 1;

                while (i + run < path.Count && path[i + run] == path[i]) run++;

                steps.Add(run == 1 ? path[i] : path[i] + " (x" + run + ")");
                i += run;
            }

            return string.Join(PathSeparator, steps);
        }

        private static string AsSentence(string text)
        {
            text = (text ?? "").Trim();

            return text.Length == 0 || ".!?".IndexOf(text[text.Length - 1]) >= 0 ? text : text + ".";
        }
    }
}
