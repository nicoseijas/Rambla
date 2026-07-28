using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rambla.Generators;

/// <summary>One level of type nesting to re-declare around generated members.</summary>
internal sealed record TypeLayer(string Keyword, string Name);

/// <summary>
/// Symbol inspection and source-shell emission shared by the Rambla generators.
/// Both <c>[State]</c> and <c>[StateCommand]</c> answer the same questions — is
/// the type partial, does it derive from <c>RamblaState</c>, how is it nested —
/// and both wrap their members in the same namespace/partial-type shell.
/// </summary>
internal static class GeneratorSupport
{
    public const string BaseTypeMetadataName = "Rambla.RamblaState";

    /// <summary>Shared: a containing type that is not partial cannot receive generated members.</summary>
    public static readonly DiagnosticDescriptor NotPartial = new(
        "RMB001", "Containing type must be partial",
        "'{0}' must be partial for Rambla to generate '{1}'", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>Shared: generation only makes sense on a state.</summary>
    public static readonly DiagnosticDescriptor NotRamblaState = new(
        "RMB005", "Containing type must derive from RamblaState",
        "'{0}' must derive from Rambla.RamblaState to use Rambla source generation", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static bool HasMemberNamed(INamedTypeSymbol type, string name) => type.GetMembers(name).Length > 0;

    public static bool DerivesFromRamblaState(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::" + BaseTypeMetadataName)
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<INamedTypeSymbol> SelfAndContainingTypes(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            yield return t;
        }
    }

    public static bool IsPartial(INamedTypeSymbol type) => type.DeclaringSyntaxReferences.Any(static r =>
        r.GetSyntax() is TypeDeclarationSyntax decl && decl.Modifiers.Any(SyntaxKind.PartialKeyword));

    /// <summary>The nesting chain, outermost first, ready to be re-declared.</summary>
    public static ImmutableArray<TypeLayer> NestingOf(INamedTypeSymbol type) => SelfAndContainingTypes(type)
        .Reverse()
        .Select(static t => new TypeLayer(Keyword(t), NameWithTypeParameters(t)))
        .ToImmutableArray();

    public static string? NamespaceOf(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

    public static string Keyword(INamedTypeSymbol type)
    {
        if (type.IsRecord)
        {
            return type.TypeKind == TypeKind.Struct ? "record struct" : "record";
        }

        return type.TypeKind switch
        {
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => "class",
        };
    }

    public static string NameWithTypeParameters(INamedTypeSymbol type)
    {
        if (type.TypeParameters.Length == 0)
        {
            return type.Name;
        }

        return type.Name + "<" + string.Join(", ", type.TypeParameters.Select(static p => p.Name)) + ">";
    }

    public static StringBuilder Pad(StringBuilder sb, int indent) => sb.Append(' ', indent * 4);

    /// <summary>A file name derived from the type, unique per generator suffix.</summary>
    public static string HintName(string typeKey, string suffix)
    {
        var sb = new StringBuilder(typeKey.Length);
        foreach (char c in typeKey)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.Append(suffix).ToString();
    }

    /// <summary>
    /// Writes the namespace and partial-type shell around <paramref name="writeMembers"/>,
    /// which receives the builder and the indent level its members start at.
    /// </summary>
    public static string BuildPartialType(
        string header,
        string? ns,
        ImmutableArray<TypeLayer> nesting,
        Action<StringBuilder, int> writeMembers)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        int indent = 0;
        if (ns is not null)
        {
            sb.Append("namespace ").Append(ns).AppendLine();
            sb.AppendLine("{");
            indent = 1;
        }

        foreach (TypeLayer layer in nesting)
        {
            Pad(sb, indent).Append("partial ").Append(layer.Keyword).Append(' ').Append(layer.Name).AppendLine();
            Pad(sb, indent).AppendLine("{");
            indent++;
        }

        writeMembers(sb, indent);

        for (int i = 0; i < nesting.Length; i++)
        {
            indent--;
            Pad(sb, indent).AppendLine("}");
        }

        if (ns is not null)
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }
}
