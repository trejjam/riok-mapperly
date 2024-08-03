using Microsoft.CodeAnalysis.CSharp.Syntax;
using Riok.Mapperly.Descriptors.Mappings;
using Riok.Mapperly.Descriptors.Mappings.UserMappings;
using Riok.Mapperly.Diagnostics;

namespace Riok.Mapperly.Descriptors.MappingBuilders;

public static class InlineExpressionMappingBuilder
{
    /// <summary>
    /// Tries to inline a given mapping.
    /// This works as long as the user implemented methods
    /// follow the expression tree limitations:
    /// https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/expression-trees/#limitations
    /// </summary>
    /// <param name="ctx">The context.</param>
    /// <param name="mapping">The mapping.</param>
    /// <returns>The inlined mapping or <c>null</c> if it could not be inlined.</returns>
    public static INewInstanceMapping? TryBuildMapping(InlineExpressionMappingBuilderContext ctx, UserImplementedMethodMapping mapping)
    {
        if (mapping.Method.DeclaringSyntaxReferences is not [var methodSyntaxRef])
        {
            ctx.ReportDiagnostic(DiagnosticDescriptors.QueryableProjectionMappingCannotInline, mapping.Method);
            return null;
        }

        var methodSyntax = methodSyntaxRef.GetSyntax();
        if (methodSyntax is not MethodDeclarationSyntax { ExpressionBody: { } body, ParameterList.Parameters: [var sourceParameter] })
        {
            ctx.ReportDiagnostic(DiagnosticDescriptors.QueryableProjectionMappingCannotInline, mapping.Method);
            return null;
        }

        var semanticModel = ctx.GetSemanticModel(methodSyntax.SyntaxTree);
        if (semanticModel is null)
        {
            return null;
        }

        var inlineRewriter = new InlineExpressionRewriter(semanticModel, ctx.FindNewInstanceMapping);
        var bodyExpression = (ExpressionSyntax?)body.Expression.Accept(inlineRewriter);
        if (bodyExpression == null || !inlineRewriter.CanBeInlined)
        {
            ctx.ReportDiagnostic(DiagnosticDescriptors.QueryableProjectionMappingCannotInline, mapping.Method);
            return null;
        }

        return new UserImplementedInlinedExpressionMapping(mapping, sourceParameter, inlineRewriter.MappingInvocations, bodyExpression);
    }
}
