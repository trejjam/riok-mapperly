using System.Diagnostics;

namespace Riok.Mapperly.Abstractions;

/// <summary>
/// Considers all accessible mapping methods provided by the type of this member.
/// Includes static and instance methods.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
[Conditional("MAPPERLY_ABSTRACTIONS_SCOPE_RUNTIME")]
public sealed class UseMapperAttribute : Attribute
{
    /// <summary>
    /// Whether to prevent mapping methods of the referenced mapper from being inlined
    /// into expression trees for queryable projection mappings.
    /// When <c>true</c>, the method calls are preserved as-is instead of being rebuilt in expression context.
    /// This only applies to expression / queryable projection mappings;
    /// regular (non-expression) mappings are unaffected.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool NoExpressionInlining { get; set; }
}
