using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RobotBattle.Api;

namespace RobotBattle.Compiler;

public sealed record CompileDiagnostic(int Line, int Column, string Message);

public sealed class CompileResult
{
    public bool Success => Assembly is not null;
    public byte[]? Assembly { get; init; }
    /// <summary>Full name of the single Robot subclass found in the submission.</summary>
    public string? RobotTypeName { get; init; }
    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; init; } = Array.Empty<CompileDiagnostic>();
}

/// <summary>
/// Compiles untrusted robot source against a minimal reference set and rejects anything outside an API allowlist.
/// This is a first line of defence only; the compiled DLL must still run in an isolated process.
/// </summary>
public sealed class RobotCompiler
{
    public const int MaxSourceBytes = 64 * 1024;

    // Namespaces whose members robots may use. Everything else (IO, Net, Reflection, Threading, Diagnostics,
    // InteropServices, Runtime...) is rejected both on the syntax tree and again on the emitted IL.
    private static readonly HashSet<string> AllowedNamespaces = new()
    {
        "System", "System.Collections", "System.Collections.Generic", "System.Linq", "System.Text", "RobotBattle.Api",
    };

    // Types inside allowed namespaces that are still off limits.
    private static readonly HashSet<string> DeniedTypes = new()
    {
        "System.Type", "System.Activator", "System.Environment", "System.Console", "System.AppDomain",
        "System.AppContext", "System.GC", "System.IntPtr", "System.UIntPtr", "System.WeakReference",
        "System.WeakReference`1", "System.Buffer", "System.TypedReference", "System.RuntimeTypeHandle",
        "System.RuntimeMethodHandle", "System.RuntimeFieldHandle", "System.ArgIterator", "System.Delegate",
        "System.MulticastDelegate", "System.TimeProvider", "System.DateTime", "System.DateTimeOffset",
        "System.Diagnostics.Process", "System.OperatingSystem",
    };

    // Compiler-generated helper types that appear in IL of perfectly ordinary code.
    private static readonly HashSet<string> AllowedCompilerTypes = new()
    {
        "System.Runtime.CompilerServices.CompilationRelaxationsAttribute", "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute",
        "System.Runtime.CompilerServices.CompilerGeneratedAttribute", "System.Runtime.CompilerServices.IsReadOnlyAttribute",
        "System.Runtime.CompilerServices.IteratorStateMachineAttribute", "System.Runtime.CompilerServices.ExtensionAttribute",
        "System.Runtime.CompilerServices.IsExternalInit", "System.Runtime.CompilerServices.RefSafetyRulesAttribute",
        "System.Runtime.CompilerServices.NullableAttribute", "System.Runtime.CompilerServices.NullableContextAttribute",
        "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler", "System.Runtime.CompilerServices.ITuple",
        "System.Runtime.CompilerServices.TupleElementNamesAttribute", "System.Runtime.CompilerServices.IsByRefLikeAttribute",
        "System.Runtime.CompilerServices.RuntimeHelpers", "System.Runtime.CompilerServices.IsVolatile", "System.Diagnostics.DebuggableAttribute",
    };

    private static readonly HashSet<string> DeniedMembers = new() { "System.Object.GetType", "System.ValueType.GetType" };

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
        string[] wanted = { "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Linq" };
        var refs = new List<MetadataReference>();
        foreach (var name in wanted)
        {
            var path = tpa.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == name)
                ?? throw new InvalidOperationException($"Reference assembly {name} not found.");
            refs.Add(MetadataReference.CreateFromFile(path));
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(Robot).Assembly.Location));
        return refs.ToImmutableArray();
    });

    public CompileResult Compile(string source, string assemblyName = "PlayerRobot")
    {
        if (System.Text.Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            return Fail($"Source exceeds {MaxSourceBytes / 1024} KB.");

        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, options);
        var compilation = CSharpCompilation.Create(
            assemblyName, new[] { tree }, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: false, optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable,
                // Overflow checks make arithmetic bugs throw instead of silently wrapping.
                checkOverflow: true,
                // Skip the default System.Private.CoreLib implicit usings and auto-references.
                metadataImportOptions: MetadataImportOptions.Public));

        var diags = new List<CompileDiagnostic>();
        foreach (var d in compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            diags.Add(ToDiag(d.Location, d.GetMessage()));
        if (diags.Count > 0) return new CompileResult { Diagnostics = diags };

        CheckForbiddenSymbols(compilation, tree, diags);
        if (diags.Count > 0) return new CompileResult { Diagnostics = diags };

        var robotTypes = FindRobotTypes(compilation);
        if (robotTypes.Count != 1)
            return Fail(robotTypes.Count == 0
                ? "No public, non-abstract class deriving from Robot with a parameterless constructor was found."
                : "Exactly one Robot subclass is allowed per submission; found " + robotTypes.Count + ".");

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
            return new CompileResult { Diagnostics = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => ToDiag(d.Location, d.GetMessage())).ToList() };

        var bytes = ms.ToArray();
        var ilProblems = CheckAssemblyReferences(bytes);
        if (ilProblems.Count > 0) return new CompileResult { Diagnostics = ilProblems.Select(p => new CompileDiagnostic(0, 0, p)).ToList() };

        return new CompileResult { Assembly = bytes, RobotTypeName = robotTypes[0] };
    }

    private static CompileResult Fail(string message) =>
        new() { Diagnostics = new[] { new CompileDiagnostic(0, 0, message) } };

    private static CompileDiagnostic ToDiag(Location loc, string message)
    {
        var pos = loc.GetLineSpan().StartLinePosition;
        return new CompileDiagnostic(pos.Line + 1, pos.Character + 1, message);
    }

    private static string NamespaceOf(ISymbol s) => s.ContainingNamespace?.ToDisplayString() ?? "";

    private static bool IsAllowed(ISymbol? symbol)
    {
        if (symbol is null) return true;
        // Walk to the declaring named type (or namespace) of the symbol.
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (symbol is ITypeSymbol ts && ts is not INamedTypeSymbol) return true; // arrays, type parameters
        if (symbol.Locations.Any(l => l.IsInSource)) return true;               // the player's own code
        if (symbol is INamespaceSymbol) return true;

        for (var t = type; t is not null; t = t.ContainingType)
            if (DeniedTypes.Contains(t.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None))) ||
                DeniedTypes.Contains(MetadataName(t))) return false;

        if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol &&
            DeniedMembers.Contains(MetadataName(symbol.ContainingType) + "." + symbol.Name)) return false;

        return AllowedNamespaces.Contains(NamespaceOf(symbol));
    }

    private static string MetadataName(INamedTypeSymbol? t) => t is null ? "" :
        (NamespaceOf(t) is { Length: > 0 } ns ? ns + "." : "") + t.MetadataName;

    private static void CheckForbiddenSymbols(CSharpCompilation compilation, SyntaxTree tree, List<CompileDiagnostic> diags)
    {
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var seen = new HashSet<(int, string)>();

        void Report(SyntaxNode node, ISymbol sym)
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition;
            if (seen.Add((node.SpanStart, sym.Name)))
                diags.Add(new CompileDiagnostic(line.Line + 1, line.Character + 1, $"'{sym.ToDisplayString()}' is not allowed in robot code."));
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case IdentifierNameSyntax or GenericNameSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                    or ElementAccessExpressionSyntax or InvocationExpressionSyntax:
                {
                    var info = model.GetSymbolInfo(node);
                    foreach (var sym in info.Symbol is null ? info.CandidateSymbols : ImmutableArray.Create(info.Symbol))
                        if (!IsAllowed(sym)) Report(node, sym);
                    // The static type of an expression can expose a forbidden type even when no member is named.
                    if (node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax)
                    {
                        var t = model.GetTypeInfo(node).Type;
                        if (t is INamedTypeSymbol nt && !IsAllowed(nt)) Report(node, nt);
                    }
                    break;
                }
                case AttributeSyntax attr:
                    var a = model.GetSymbolInfo(attr).Symbol;
                    if (a is not null && !IsAllowed(a.ContainingType)) Report(attr, a.ContainingType);
                    break;
                case UnsafeStatementSyntax or FixedStatementSyntax or StackAllocArrayCreationExpressionSyntax:
                    diags.Add(new CompileDiagnostic(node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, 1, "Unsafe code is not allowed."));
                    break;
                case MethodDeclarationSyntax m when m.Modifiers.Any(SyntaxKind.ExternKeyword):
                    diags.Add(new CompileDiagnostic(m.GetLocation().GetLineSpan().StartLinePosition.Line + 1, 1, "extern methods are not allowed."));
                    break;
            }
        }
    }

    private static List<string> FindRobotTypes(CSharpCompilation compilation)
    {
        var baseType = compilation.GetTypeByMetadataName("RobotBattle.Api.Robot")!;
        var found = new List<string>();
        void Visit(INamespaceOrTypeSymbol container)
        {
            foreach (var m in container.GetMembers())
            {
                if (m is INamespaceSymbol ns) Visit(ns);
                else if (m is INamedTypeSymbol t)
                {
                    if (t.DeclaredAccessibility == Accessibility.Public && !t.IsAbstract && !t.IsStatic &&
                        InheritsFrom(t, baseType) && t.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                        found.Add(t.ToDisplayString());
                    Visit(t);
                }
            }
        }
        Visit(compilation.Assembly.GlobalNamespace);
        return found;
    }

    private static bool InheritsFrom(INamedTypeSymbol t, INamedTypeSymbol baseType)
    {
        for (var b = t.BaseType; b is not null; b = b.BaseType)
            if (SymbolEqualityComparer.Default.Equals(b, baseType)) return true;
        return false;
    }

    /// <summary>Backstop: scan the emitted metadata so a trick that fooled the syntax pass still can't reference a banned type.</summary>
    private static List<string> CheckAssemblyReferences(byte[] dll)
    {
        var problems = new List<string>();
        using var pe = new PEReader(new MemoryStream(dll));
        var md = pe.GetMetadataReader();

        string FullName(TypeReference tr)
        {
            var ns = md.GetString(tr.Namespace);
            return (ns.Length > 0 ? ns + "." : "") + md.GetString(tr.Name);
        }

        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            // Nested types are resolved through their declaring type, which is checked on its own.
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference) continue;
            var ns = md.GetString(tr.Namespace);
            var full = FullName(tr);
            if (AllowedCompilerTypes.Contains(full)) continue;
            if (!AllowedNamespaces.Contains(ns) || DeniedTypes.Contains(full))
                problems.Add($"Reference to '{full}' is not allowed.");
        }

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            if (FullName(tr) == "System.Runtime.CompilerServices.RuntimeHelpers" && md.GetString(mr.Name) is not ("InitializeArray" or "EnsureSufficientExecutionStack"))
                problems.Add($"Call to RuntimeHelpers.{md.GetString(mr.Name)} is not allowed.");
            if (DeniedMembers.Contains(FullName(tr) + "." + md.GetString(mr.Name)))
                problems.Add($"Call to '{FullName(tr)}.{md.GetString(mr.Name)}' is not allowed.");
        }

        return problems;
    }
}
