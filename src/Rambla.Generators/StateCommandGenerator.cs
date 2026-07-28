using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Rambla.Generators;

/// <summary>
/// Emits an <c>AsyncStateCommand</c> for every method annotated with
/// <c>[StateCommand]</c>, together with the state that describes its run: the
/// busy flag, the last error, and a cancel command. <c>RefreshAsync</c> yields
/// <c>RefreshCommand</c>, <c>IsRefreshing</c>, <c>RefreshError</c> and
/// <c>CancelRefreshCommand</c>.
/// </summary>
/// <remarks>
/// The busy and error members are projections over the command — one source of
/// truth — marked dirty through <c>RamblaState.MarkDirty</c> when the run state
/// changes, so they notify on the same coalesced flush as any other property.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class StateCommandGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "Rambla.StateCommandAttribute";
    private const string TaskType = "global::System.Threading.Tasks.Task";
    private const string CancellationTokenType = "global::System.Threading.CancellationToken";
    private const string CommandType = "global::Rambla.AsyncStateCommand";

    private static readonly DiagnosticDescriptor NotInstanceMethod = new(
        "RMB006", "[StateCommand] requires an instance method",
        "[StateCommand] can only be applied to instance methods; '{0}' is static", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotTaskReturning = new(
        "RMB007", "[StateCommand] method must return Task",
        "'{0}' must return System.Threading.Tasks.Task to be a [StateCommand]", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedSignature = new(
        "RMB008", "[StateCommand] method has an unsupported signature",
        "'{0}' must take no parameters or a single CancellationToken, and cannot be generic", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NameCollision = new(
        "RMB009", "Generated command member name collides",
        "The generated member '{0}' collides with an existing member or another [StateCommand]", "Rambla",
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

    private static CommandResult? Transform(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method)
        {
            return null;
        }

        Location location = method.Locations.FirstOrDefault() ?? Location.None;
        INamedTypeSymbol containingType = method.ContainingType;
        string typeName = containingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (method.IsStatic)
        {
            return CommandResult.Error(Diagnostic.Create(NotInstanceMethod, location, method.Name));
        }

        if (method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != TaskType)
        {
            return CommandResult.Error(Diagnostic.Create(NotTaskReturning, location, method.Name));
        }

        bool takesToken;
        if (method.IsGenericMethod || method.Parameters.Length > 1)
        {
            return CommandResult.Error(Diagnostic.Create(UnsupportedSignature, location, method.Name));
        }

        if (method.Parameters.Length == 1)
        {
            if (method.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != CancellationTokenType)
            {
                return CommandResult.Error(Diagnostic.Create(UnsupportedSignature, location, method.Name));
            }

            takesToken = true;
        }
        else
        {
            takesToken = false;
        }

        if (!GeneratorSupport.DerivesFromRamblaState(containingType))
        {
            return CommandResult.Error(Diagnostic.Create(GeneratorSupport.NotRamblaState, location, typeName));
        }

        Options options = ReadOptions(context.Attributes);
        string baseName = options.Name ?? StripAsyncSuffix(method.Name);
        if (baseName.Length == 0)
        {
            return CommandResult.Error(Diagnostic.Create(NameCollision, location, method.Name));
        }

        var model = new CommandModel(
            TypeKey: containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace: GeneratorSupport.NamespaceOf(containingType),
            Nesting: GeneratorSupport.NestingOf(containingType),
            MethodName: method.Name,
            TakesToken: takesToken,
            CommandName: baseName + "Command",
            BusyName: options.BusyName ?? "Is" + Gerund(baseName),
            ErrorName: options.ErrorName ?? baseName + "Error",
            CancelName: "Cancel" + baseName + "Command",
            FieldName: "_" + LowerFirst(baseName) + "Command",
            FactoryName: "Create" + baseName + "Command",
            CancelPrevious: options.CancelPrevious,
            Location: location);

        foreach (string name in model.GeneratedNames)
        {
            if (GeneratorSupport.HasMemberNamed(containingType, name))
            {
                return CommandResult.Error(Diagnostic.Create(NameCollision, location, name));
            }
        }

        foreach (INamedTypeSymbol type in GeneratorSupport.SelfAndContainingTypes(containingType))
        {
            if (!GeneratorSupport.IsPartial(type))
            {
                return CommandResult.Error(
                    Diagnostic.Create(GeneratorSupport.NotPartial, location, typeName, model.CommandName));
            }
        }

        return CommandResult.Ok(model);
    }

    private static Options ReadOptions(ImmutableArray<AttributeData> attributes)
    {
        var options = new Options();

        foreach (AttributeData attribute in attributes)
        {
            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                switch (argument.Key)
                {
                    case nameof(Options.CancelPrevious):
                        options.CancelPrevious = argument.Value.Value is true;
                        break;
                    case nameof(Options.Name):
                        options.Name = AsName(argument.Value);
                        break;
                    case nameof(Options.BusyName):
                        options.BusyName = AsName(argument.Value);
                        break;
                    case nameof(Options.ErrorName):
                        options.ErrorName = AsName(argument.Value);
                        break;
                }
            }
        }

        return options;
    }

    private static string? AsName(TypedConstant value)
        => value.Value is string s && s.Length > 0 ? s : null;

    private static void Emit(SourceProductionContext spc, ImmutableArray<CommandResult> items)
    {
        foreach (CommandResult result in items)
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
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var emit = new List<CommandModel>();

            foreach (CommandModel model in group)
            {
                string? clash = model.GeneratedNames.FirstOrDefault(name => !seen.Add(name));
                if (clash is null)
                {
                    emit.Add(model);
                }
                else
                {
                    spc.ReportDiagnostic(Diagnostic.Create(NameCollision, model.Location, clash));
                }
            }

            if (emit.Count == 0)
            {
                continue;
            }

            CommandModel first = emit[0];
            spc.AddSource(
                GeneratorSupport.HintName(first.TypeKey, ".Commands.g.cs"),
                BuildSource(first, emit));
        }
    }

    private static string BuildSource(CommandModel type, List<CommandModel> commands)
        => GeneratorSupport.BuildPartialType(
            "// <auto-generated/> — Rambla [StateCommand] source generator. Do not edit.",
            type.Namespace,
            type.Nesting,
            (sb, indent) =>
            {
                for (int i = 0; i < commands.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.AppendLine();
                    }

                    WriteCommand(sb, indent, commands[i]);
                }
            });

    private static void WriteCommand(StringBuilder sb, int indent, CommandModel c)
    {
        Pad(sb, indent).Append("private ").Append(CommandType).Append("? ").Append(c.FieldName).AppendLine(";");
        sb.AppendLine();

        Doc(sb, indent, "Runs <see cref=\"" + c.MethodName + "\"/>. Errors land in <see cref=\"" + c.ErrorName + "\"/>.");
        Pad(sb, indent).Append("public ").Append(CommandType).Append(' ').Append(c.CommandName).AppendLine();
        Pad(sb, indent + 1).Append("=> EnsureCommand(ref ").Append(c.FieldName).Append(", ").Append(c.FactoryName).AppendLine(");");
        sb.AppendLine();

        Doc(sb, indent, "Whether <see cref=\"" + c.CommandName + "\"/> is running.");
        Pad(sb, indent).Append("public bool ").Append(c.BusyName).Append(" => ").Append(c.CommandName).AppendLine(".IsRunning;");
        sb.AppendLine();

        Doc(sb, indent, "The exception the last <see cref=\"" + c.CommandName + "\"/> run failed with, or null.");
        Pad(sb, indent).Append("public global::System.Exception? ").Append(c.ErrorName)
            .Append(" => ").Append(c.CommandName).AppendLine(".Error;");
        sb.AppendLine();

        Doc(sb, indent, "Cancels the running <see cref=\"" + c.CommandName + "\"/>.");
        Pad(sb, indent).Append("public global::System.Windows.Input.ICommand ").Append(c.CancelName)
            .Append(" => ").Append(c.CommandName).AppendLine(".CancelCommand;");
        sb.AppendLine();

        Pad(sb, indent).Append("private ").Append(CommandType).Append(' ').Append(c.FactoryName).AppendLine("()");
        Pad(sb, indent).AppendLine("{");

        Pad(sb, indent + 1).Append("var command = new ").Append(CommandType).AppendLine("(");
        Pad(sb, indent + 2).Append(c.TakesToken ? "token => " + c.MethodName + "(token)," : "_ => " + c.MethodName + "(),").AppendLine();
        Pad(sb, indent + 2).AppendLine("canExecute: null,");
        Pad(sb, indent + 2).Append("cancelPrevious: ").Append(c.CancelPrevious ? "true" : "false").AppendLine(",");
        Pad(sb, indent + 2).AppendLine("scheduler: Scheduler);");
        sb.AppendLine();

        Pad(sb, indent + 1).AppendLine("// One batch, so a transition notifies both projections in one flush.");
        Pad(sb, indent + 1).AppendLine("command.StateChanged += (_, _) =>");
        Pad(sb, indent + 1).AppendLine("{");
        Pad(sb, indent + 2).AppendLine("using (BeginUpdate())");
        Pad(sb, indent + 2).AppendLine("{");
        Pad(sb, indent + 3).Append("MarkDirty(nameof(").Append(c.BusyName).AppendLine("));");
        Pad(sb, indent + 3).Append("MarkDirty(nameof(").Append(c.ErrorName).AppendLine("));");
        Pad(sb, indent + 2).AppendLine("}");
        Pad(sb, indent + 1).AppendLine("};");
        sb.AppendLine();

        Pad(sb, indent + 1).AppendLine("return command;");
        Pad(sb, indent).AppendLine("}");
    }

    private static void Doc(StringBuilder sb, int indent, string summary)
    {
        Pad(sb, indent).Append("/// <summary>").Append(summary).AppendLine("</summary>");
    }

    private static StringBuilder Pad(StringBuilder sb, int indent) => GeneratorSupport.Pad(sb, indent);

    private static string StripAsyncSuffix(string methodName)
        => methodName.Length > 5 && methodName.EndsWith("Async", System.StringComparison.Ordinal)
            ? methodName.Substring(0, methodName.Length - 5)
            : methodName;

    private static string LowerFirst(string name)
        => char.ToLowerInvariant(name[0]) + name.Substring(1);

    /// <summary>
    /// The English <c>-ing</c> form of a verb, good enough for command names:
    /// <c>Save</c> → <c>Saving</c>, <c>Submit</c> → <c>Submitting</c>,
    /// <c>Refresh</c> → <c>Refreshing</c>. It cannot know where the stress falls,
    /// so it doubles a final consonant only for one-vowel-group verbs and the
    /// handful of endings that always take it; <c>BusyName</c> covers the rest.
    /// </summary>
    internal static string Gerund(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        string lower = name.ToLowerInvariant();

        if (lower.EndsWith("ie", System.StringComparison.Ordinal))
        {
            return name.Substring(0, name.Length - 2) + "ying";
        }

        if (lower.EndsWith("ing", System.StringComparison.Ordinal))
        {
            return name;
        }

        if (lower.Length > 1 && lower[lower.Length - 1] == 'e' && lower[lower.Length - 2] != 'e')
        {
            return name.Substring(0, name.Length - 1) + "ing";
        }

        if (ShouldDoubleFinalConsonant(lower))
        {
            return name + name[name.Length - 1] + "ing";
        }

        return name + "ing";
    }

    private static bool ShouldDoubleFinalConsonant(string lower)
    {
        if (lower.Length < 3)
        {
            return false;
        }

        char last = lower[lower.Length - 1];
        char beforeLast = lower[lower.Length - 2];
        char beforeThat = lower[lower.Length - 3];

        // Only a consonant-vowel-consonant ending doubles, and never w/x/y.
        if (IsVowel(last) || last is 'w' or 'x' or 'y' || !IsVowel(beforeLast) || IsVowel(beforeThat))
        {
            return false;
        }

        // A single vowel group means a one-syllable verb (Run, Stop, Set), which
        // always doubles. Longer verbs only do when the stress is on the last
        // syllable — undetectable here, so only the endings that always are.
        return VowelGroups(lower) == 1
            || lower.EndsWith("mit", System.StringComparison.Ordinal)
            || lower.EndsWith("cur", System.StringComparison.Ordinal)
            || lower.EndsWith("fer", System.StringComparison.Ordinal);
    }

    private static int VowelGroups(string lower)
    {
        int groups = 0;
        bool inGroup = false;
        foreach (char c in lower)
        {
            if (IsVowel(c))
            {
                if (!inGroup)
                {
                    groups++;
                    inGroup = true;
                }
            }
            else
            {
                inGroup = false;
            }
        }

        return groups;
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    private sealed class Options
    {
        public bool CancelPrevious { get; set; }

        public string? Name { get; set; }

        public string? BusyName { get; set; }

        public string? ErrorName { get; set; }
    }

    private sealed record CommandModel(
        string TypeKey,
        string? Namespace,
        ImmutableArray<TypeLayer> Nesting,
        string MethodName,
        bool TakesToken,
        string CommandName,
        string BusyName,
        string ErrorName,
        string CancelName,
        string FieldName,
        string FactoryName,
        bool CancelPrevious,
        Location Location)
    {
        public IEnumerable<string> GeneratedNames
        {
            get
            {
                yield return CommandName;
                yield return BusyName;
                yield return ErrorName;
                yield return CancelName;
                yield return FieldName;
                yield return FactoryName;
            }
        }
    }

    private sealed class CommandResult
    {
        private CommandResult(CommandModel? model, Diagnostic? diagnostic)
        {
            Model = model;
            Diagnostic = diagnostic;
        }

        public CommandModel? Model { get; }

        public Diagnostic? Diagnostic { get; }

        public static CommandResult Ok(CommandModel model) => new(model, null);

        public static CommandResult Error(Diagnostic diagnostic) => new(null, diagnostic);
    }
}
