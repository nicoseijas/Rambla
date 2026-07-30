using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rambla.Generators;

/// <summary>
/// Emits observable properties for fields annotated with <c>[State]</c>, routing
/// their setters through <c>RamblaState.SetField</c> so writes participate in
/// batching and coalescing. The generator strips a leading underscore:
/// <c>_bid</c> produces <c>Bid</c>.
/// </summary>
/// <remarks>
/// V1 is deliberately minimal: a field yields exactly one property. No
/// <c>DependsOn</c>, validation, custom names or custom equality. It is fully
/// compile-time (no reflection, no runtime registration) and reports diagnostics
/// RMB001–RMB005 for misuse. Commands are a separate concern, emitted by
/// <see cref="StateCommandGenerator"/>.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class StateGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "Rambla.StateAttribute";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly DiagnosticDescriptor NotInstanceField = new(
        "RMB002", "[State] requires an instance field",
        "[State] can only be applied to instance fields; '{0}' is static", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NameCollision = new(
        "RMB003", "Generated property name collides",
        "The generated property name '{0}' collides with an existing member or another [State] field", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ReadonlyOrConst = new(
        "RMB004", "[State] does not support readonly or const fields",
        "[State] cannot be applied to the readonly or const field '{0}'", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                predicate: static (_, _) => true,
                transform: static (ctx, _) => Transform(ctx))
            .Where(static r => r is not null)
            .Select(static (r, _) => r!)
            .Collect();

        context.RegisterSourceOutput(results, static (spc, items) => Emit(spc, items));
    }

    private static FieldResult? Transform(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IFieldSymbol field)
        {
            return null;
        }

        Location location = field.Locations.FirstOrDefault() ?? Location.None;
        INamedTypeSymbol containingType = field.ContainingType;
        string typeName = containingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (field.IsStatic)
        {
            return FieldResult.Error(Diagnostic.Create(NotInstanceField, location, field.Name));
        }

        if (field.IsReadOnly || field.IsConst)
        {
            return FieldResult.Error(Diagnostic.Create(ReadonlyOrConst, location, field.Name));
        }

        string propertyName = DerivePropertyName(field.Name);
        if (propertyName.Length == 0 || propertyName == field.Name || GeneratorSupport.HasMemberNamed(containingType, propertyName))
        {
            return FieldResult.Error(Diagnostic.Create(NameCollision, location, propertyName));
        }

        if (!GeneratorSupport.DerivesFromRamblaState(containingType))
        {
            return FieldResult.Error(Diagnostic.Create(GeneratorSupport.NotRamblaState, location, typeName));
        }

        foreach (INamedTypeSymbol type in GeneratorSupport.SelfAndContainingTypes(containingType))
        {
            if (!GeneratorSupport.IsPartial(type))
            {
                return FieldResult.Error(Diagnostic.Create(GeneratorSupport.NotPartial, location, typeName, propertyName));
            }
        }

        var model = new FieldModel(
            TypeKey: containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace: GeneratorSupport.NamespaceOf(containingType),
            Nesting: GeneratorSupport.NestingOf(containingType),
            PropertyType: field.Type.ToDisplayString(TypeFormat),
            PropertyName: propertyName,
            FieldName: GeneratorSupport.EscapeIdentifier(field.Name),
            Location: location);

        return FieldResult.Ok(model);
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<FieldResult> items)
    {
        foreach (FieldResult result in items)
        {
            if (result.Diagnostic is { } diagnostic)
            {
                spc.ReportDiagnostic(diagnostic);
            }
        }

        var groups = items
            .Where(static r => r.Model is not null)
            .Select(static r => r.Model!)
            .GroupBy(static m => m.TypeKey);

        foreach (var group in groups)
        {
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            var emit = new System.Collections.Generic.List<FieldModel>();
            foreach (FieldModel model in group)
            {
                if (seen.Add(model.PropertyName))
                {
                    emit.Add(model);
                }
                else
                {
                    spc.ReportDiagnostic(Diagnostic.Create(NameCollision, model.Location, model.PropertyName));
                }
            }

            if (emit.Count == 0)
            {
                continue;
            }

            FieldModel first = emit[0];
            spc.AddSource(
                GeneratorSupport.HintName(first.TypeKey, ".State.g.cs"),
                BuildSource(first, emit));
        }
    }

    private static string BuildSource(FieldModel type, System.Collections.Generic.List<FieldModel> fields)
        => GeneratorSupport.BuildPartialType(
            "// <auto-generated/> — Rambla [State] source generator. Do not edit.",
            type.Namespace,
            type.Nesting,
            (sb, indent) =>
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    FieldModel f = fields[i];
                    Pad(sb, indent).Append("public ").Append(f.PropertyType).Append(' ').Append(f.PropertyName).AppendLine();
                    Pad(sb, indent).AppendLine("{");
                    // "this." keeps the ref on the field even when it is named
                    // "value" — bare, that would bind to the setter's parameter.
                    Pad(sb, indent + 1).Append("get => this.").Append(f.FieldName).AppendLine(";");
                    Pad(sb, indent + 1).Append("set => SetField(ref this.").Append(f.FieldName).AppendLine(", value);");
                    Pad(sb, indent).AppendLine("}");
                    if (i < fields.Count - 1)
                    {
                        sb.AppendLine();
                    }
                }
            });

    private static StringBuilder Pad(StringBuilder sb, int indent) => GeneratorSupport.Pad(sb, indent);

    private static string DerivePropertyName(string fieldName)
    {
        string raw = fieldName.TrimStart('_');
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
    }

    private sealed record FieldModel(
        string TypeKey,
        string? Namespace,
        ImmutableArray<TypeLayer> Nesting,
        string PropertyType,
        string PropertyName,
        string FieldName,
        Location Location);

    private sealed class FieldResult
    {
        private FieldResult(FieldModel? model, Diagnostic? diagnostic)
        {
            Model = model;
            Diagnostic = diagnostic;
        }

        public FieldModel? Model { get; }

        public Diagnostic? Diagnostic { get; }

        public static FieldResult Ok(FieldModel model) => new(model, null);

        public static FieldResult Error(Diagnostic diagnostic) => new(null, diagnostic);
    }
}
