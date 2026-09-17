using System;
using FluentAssertions;

namespace LiteDB.Tests.Mapper
{
    internal static class ExpressionParity
    {
        internal static BsonExpression WithSelectorDependency(BsonExpression expected)
        {
            // Explicit SQL MAP/FILTER retains its historical input-only metadata.
            // These LINQ cases must additionally account for selector parameters.
            expected.IsImmutable.Should().BeTrue();
            expected.IsImmutable = false;
            return expected;
        }

        internal static void AssertMetadata(BsonExpression actual, BsonExpression expected)
        {
            actual.Source.Should().Be(expected.Source);
            actual.Type.Should().Be(expected.Type, actual.Source);
            actual.IsScalar.Should().Be(expected.IsScalar, actual.Source);
            actual.IsImmutable.Should().Be(expected.IsImmutable, actual.Source);
            actual.IsVolatile.Should().Be(expected.IsVolatile, actual.Source);
            actual.IsANY.Should().Be(expected.IsANY, actual.Source);
            actual.UseSource.Should().Be(expected.UseSource, actual.Source);
            actual.Fields.Should().BeEquivalentTo(expected.Fields, actual.Source);
            if (expected.Left == null) actual.Left.Should().BeNull(actual.Source);
            else AssertMetadata(actual.Left, expected.Left);
            if (expected.Right == null) actual.Right.Should().BeNull(actual.Source);
            else AssertMetadata(actual.Right, expected.Right);
        }
    }

    internal sealed class DirectTranslationScope : IDisposable
    {
        private readonly bool _previousCache;
        private readonly bool _previousTokenizer;

        internal DirectTranslationScope()
        {
            _previousCache = BsonExpression.DisableCompilationCache;
            _previousTokenizer = Tokenizer.ForbidCreation;
            BsonExpression.DisableCompilationCache = true;
            Tokenizer.ForbidCreation = true;
        }

        public void Dispose()
        {
            BsonExpression.DisableCompilationCache = _previousCache;
            Tokenizer.ForbidCreation = _previousTokenizer;
        }
    }
}
