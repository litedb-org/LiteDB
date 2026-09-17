using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LiteDB.SourceGenerator.Models;

internal sealed record DiagnosticLocationDescriptor(
    string FilePath,
    TextSpan SourceSpan,
    LinePositionSpan LineSpan)
{
    public static DiagnosticLocationDescriptor From(SyntaxNode targetNode)
    {
        var location = targetNode is TypeDeclarationSyntax typeDeclaration
            ? typeDeclaration.Identifier.GetLocation()
            : targetNode.GetLocation();
        var lineSpan = location.GetLineSpan();

        return new DiagnosticLocationDescriptor(lineSpan.Path, location.SourceSpan, lineSpan.Span);
    }

    public Location ToLocation() => Location.Create(FilePath, SourceSpan, LineSpan);
}
