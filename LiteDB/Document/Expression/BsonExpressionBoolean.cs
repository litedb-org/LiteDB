using System.Runtime.CompilerServices;

namespace LiteDB
{
    internal static class BsonExpressionBoolean
    {
        // Plain Boolean BsonValues are immutable. Keep the shared results internal
        // to expression evaluation; callers still own their documents and arrays.
        private static readonly BsonValue True = new BsonValue(true);
        private static readonly BsonValue False = new BsonValue(false);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BsonValue FromBoolean(bool value)
        {
            return value ? True : False;
        }
    }
}
