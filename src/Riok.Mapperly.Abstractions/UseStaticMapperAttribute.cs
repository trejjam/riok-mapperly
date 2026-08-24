using System.Diagnostics;

namespace Riok.Mapperly.Abstractions;

/// <summary>
/// Considers all static mapping methods provided by the type.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = true)]
[Conditional("MAPPERLY_ABSTRACTIONS_SCOPE_RUNTIME")]
public sealed class UseStaticMapperAttribute : Attribute
{
    /// <summary>
    /// Considers all static mapping methods provided by the <paramref name="mapperType"/>.
    /// </summary>
    /// <param name="mapperType">The type of which mapping methods will be included.</param>
    public UseStaticMapperAttribute(Type mapperType) { }

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

/// <summary>
/// Considers all static mapping methods provided by the generic type.
/// </summary>
/// <typeparam name="T">The type of which mapping methods will be included.</typeparam>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = true)]
[Conditional("MAPPERLY_ABSTRACTIONS_SCOPE_RUNTIME")]
public sealed class UseStaticMapperAttribute<T> : Attribute
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
