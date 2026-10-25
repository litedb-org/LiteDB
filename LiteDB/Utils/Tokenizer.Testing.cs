using System;

namespace LiteDB
{
    internal partial class Tokenizer
    {
        partial void OnCreate();

#if TESTING
        [ThreadStatic]
        internal static bool ForbidCreation;

        partial void OnCreate()
        {
            if (ForbidCreation) throw new InvalidOperationException("This operation must not tokenize expression text.");
        }
#endif
    }
}
