// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// CalculatorGenerator.cs
//
// Roslyn IIncrementalGenerator entry. Scans the compilation for
// assembly-level [GeneratedCalculator] attributes, runs the existing
// CalculatorGenApi.Generate pipeline, and emits one calculator (+ optional
// self-test) per attribute instance.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FracturingFog.CalculatorGen.SourceGen;

[Generator(LanguageNames.CSharp)]
public sealed class CalculatorGenerator : IIncrementalGenerator
{
    private const string AttrFqn = GeneratedCalculatorAttributeSource.FullyQualifiedName;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Inject the attribute source into every consumer compile.
        context.RegisterPostInitializationOutput(ctx =>
            ctx.AddSource(
                GeneratedCalculatorAttributeSource.HintName,
                SourceText.From(GeneratedCalculatorAttributeSource.Source, Encoding.UTF8)));

        // 2. Pull all [assembly: GeneratedCalculator(...)] attribute instances.
        IncrementalValueProvider<ImmutableEquatableArray<CalcDecl>> decls =
            context.CompilationProvider.Select(static (compilation, _) =>
                ExtractAssemblyAttributes(compilation));

        // 3. For each declaration, generate + emit.
        context.RegisterSourceOutput(decls, static (spc, declArr) =>
        {
            foreach (CalcDecl decl in declArr.Items)
                Emit(spc, decl);
        });
    }

    private static ImmutableEquatableArray<CalcDecl> ExtractAssemblyAttributes(Compilation compilation)
    {
        INamedTypeSymbol? attrSym = compilation.GetTypeByMetadataName(AttrFqn);
        if (attrSym is null)
            return ImmutableEquatableArray<CalcDecl>.Empty;

        var found = new List<CalcDecl>();
        foreach (AttributeData attr in compilation.Assembly.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrSym))
                continue;

            string equation = attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is string eq ? eq : "";
            string name = attr.ConstructorArguments.Length > 1
                && attr.ConstructorArguments[1].Value is string n ? n : "";

            double bailout = 512.0;
            bool includeSelfTest = false;
            foreach (KeyValuePair<string, TypedConstant> kv in attr.NamedArguments)
            {
                switch (kv.Key)
                {
                    case "Bailout":         bailout         = ToDouble(kv.Value, 512.0); break;
                    case "IncludeSelfTest": includeSelfTest = ToBool(kv.Value, false);   break;
                }
            }

            Location loc = attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;
            found.Add(new CalcDecl(equation, name, bailout, includeSelfTest, loc));
        }
        return new ImmutableEquatableArray<CalcDecl>(found.ToArray());
    }

    private static double ToDouble(TypedConstant tc, double fallback)
        => tc.Value is double d ? d
         : tc.Value is float f ? (double)f
         : tc.Value is int i ? i
         : fallback;

    private static bool ToBool(TypedConstant tc, bool fallback)
        => tc.Value is bool b ? b : fallback;

    private static void Emit(SourceProductionContext spc, CalcDecl decl)
    {
        if (string.IsNullOrWhiteSpace(decl.Equation))
        {
            spc.ReportDiagnostic(Diagnostic.Create(DiagEquationEmpty, decl.Location, decl.Name));
            return;
        }
        if (string.IsNullOrWhiteSpace(decl.Name))
        {
            spc.ReportDiagnostic(Diagnostic.Create(DiagNameEmpty, decl.Location));
            return;
        }

        GenerateResult result = CalculatorGenApi.Generate(
            equation: decl.Equation,
            name: decl.Name,
            includeSelfTest: decl.IncludeSelfTest,
            bailoutRadius: decl.Bailout);

        if (!result.Ok)
        {
            spc.ReportDiagnostic(Diagnostic.Create(DiagParseFailed, decl.Location, decl.Name, result.Error));
            return;
        }

        spc.AddSource(
            $"{result.ClassName}.g.cs",
            SourceText.From(result.Source, Encoding.UTF8));

        if (decl.IncludeSelfTest && !string.IsNullOrEmpty(result.SelfTest))
        {
            spc.AddSource(
                $"{result.ClassName}SelfTest.g.cs",
                SourceText.From(result.SelfTest!, Encoding.UTF8));
        }
    }

    private readonly struct CalcDecl
    {
        public readonly string Equation;
        public readonly string Name;
        public readonly double Bailout;
        public readonly bool IncludeSelfTest;
        public readonly Location Location;

        public CalcDecl(string equation, string name, double bailout, bool includeSelfTest, Location location)
        {
            Equation = equation;
            Name = name;
            Bailout = bailout;
            IncludeSelfTest = includeSelfTest;
            Location = location;
        }
    }

    /// <summary>
    /// Wraps an array for use as an incremental-generator pipeline value.
    /// The pipeline relies on value equality to skip downstream work; the
    /// default <c>T[]</c> equality is reference, which would re-emit every
    /// build. Item-wise equality keeps the generator incremental.
    /// </summary>
    private readonly struct ImmutableEquatableArray<T> : System.IEquatable<ImmutableEquatableArray<T>>
    {
        public readonly T[] Items;
        public ImmutableEquatableArray(T[] items) { Items = items; }
        public static ImmutableEquatableArray<T> Empty => new ImmutableEquatableArray<T>(System.Array.Empty<T>());

        public bool Equals(ImmutableEquatableArray<T> other)
        {
            if (Items.Length != other.Items.Length) return false;
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < Items.Length; i++)
                if (!cmp.Equals(Items[i], other.Items[i])) return false;
            return true;
        }
        public override bool Equals(object? obj) => obj is ImmutableEquatableArray<T> o && Equals(o);
        public override int GetHashCode()
        {
            int h = 17;
            foreach (T t in Items)
                h = unchecked(h * 31 + (t?.GetHashCode() ?? 0));
            return h;
        }
    }

    private static readonly DiagnosticDescriptor DiagEquationEmpty = new(
        id: "CG001",
        title: "GeneratedCalculator equation is empty",
        messageFormat: "[GeneratedCalculator] for '{0}' has an empty equation string",
        category: "CalculatorGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DiagNameEmpty = new(
        id: "CG003",
        title: "GeneratedCalculator name is empty",
        messageFormat: "[GeneratedCalculator] missing a Name (second constructor arg)",
        category: "CalculatorGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DiagParseFailed = new(
        id: "CG002",
        title: "CalculatorGen parse failure",
        messageFormat: "Failed to parse equation for '{0}': {1}",
        category: "CalculatorGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
