// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.DotNet.ApiSymbolExtensions;
using Microsoft.DotNet.ApiSymbolExtensions.Filtering;
using Microsoft.DotNet.ApiSymbolExtensions.Logging;
using Microsoft.DotNet.GenAPI.SyntaxRewriter;

namespace Microsoft.DotNet.GenAPI
{
    /// <summary>
    /// Processes assembly symbols to build corresponding structures in C# language.
    /// </summary>
    public sealed class CSharpFileBuilder : IAssemblySymbolWriter, IDisposable
    {
        private readonly ILog _logger;
        private readonly TextWriter _textWriter;
        private readonly ISymbolFilter _symbolFilter;
        private readonly string? _exceptionMessage;
        private readonly bool _includeAssemblyAttributes;
        private readonly AdhocWorkspace _adhocWorkspace;
        private readonly SyntaxGenerator _syntaxGenerator;
        private readonly IEnumerable<MetadataReference> _metadataReferences;

        public CSharpFileBuilder(ILog logger,
            ISymbolFilter symbolFilter,
            TextWriter textWriter,
            string? exceptionMessage,
            bool includeAssemblyAttributes,
            IEnumerable<MetadataReference> metadataReferences)
        {
            _logger = logger;
            _textWriter = textWriter;
            _symbolFilter = symbolFilter;
            _exceptionMessage = exceptionMessage;
            _includeAssemblyAttributes = includeAssemblyAttributes;
            _adhocWorkspace = new AdhocWorkspace();
            _syntaxGenerator = SyntaxGenerator.GetGenerator(_adhocWorkspace, LanguageNames.CSharp);
            _metadataReferences = metadataReferences;
        }

        /// <inheritdoc />
        public void WriteAssembly(IAssemblySymbol assemblySymbol)
        {
            CSharpCompilationOptions compilationOptions = new(OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable);
            Project project = _adhocWorkspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), assemblySymbol.Name, assemblySymbol.Name, LanguageNames.CSharp,
                compilationOptions: compilationOptions));
            project = project.AddMetadataReferences(_metadataReferences);

            IEnumerable<INamespaceSymbol> namespaceSymbols = EnumerateNamespaces(assemblySymbol).Where(_symbolFilter.Include);
            List<SyntaxNode> namespaceSyntaxNodes = new();

            foreach (INamespaceSymbol namespaceSymbol in namespaceSymbols.Order())
            {
                SyntaxNode? syntaxNode = Visit(namespaceSymbol);

                if (syntaxNode is not null)
                {
                    namespaceSyntaxNodes.Add(syntaxNode);
                }
            }

            SyntaxNode compilationUnit = _syntaxGenerator.CompilationUnit(namespaceSyntaxNodes)
                .WithAdditionalAnnotations(Formatter.Annotation, Simplifier.Annotation)
                .Rewrite(new TypeDeclarationCSharpSyntaxRewriter())
                .Rewrite(new BodyBlockCSharpSyntaxRewriter(_exceptionMessage))
                .NormalizeWhitespace();

            if (_includeAssemblyAttributes)
            {
                compilationUnit = GenerateAssemblyAttributes(assemblySymbol, compilationUnit);
            }

            compilationUnit = GenerateForwardedTypeAssemblyAttributes(assemblySymbol, compilationUnit);

            Document document = project.AddDocument(assemblySymbol.Name, compilationUnit);
            document = Simplifier.ReduceAsync(document).Result;
            document = Formatter.FormatAsync(document, DefineFormattingOptions()).Result;

            document.GetSyntaxRootAsync().Result!
                .Rewrite(new SingleLineStatementCSharpSyntaxRewriter())
                .Rewrite(new TypeForwardAttributeCSharpSyntaxRewriter())
                .WriteTo(_textWriter);
        }

        private SyntaxNode? Visit(INamespaceSymbol namespaceSymbol)
        {
            SyntaxNode namespaceNode = _syntaxGenerator.NamespaceDeclaration(namespaceSymbol.ToDisplayString());

            IEnumerable<INamedTypeSymbol> typeMembers = namespaceSymbol.GetTypeMembers().Where(_symbolFilter.Include);
            if (!typeMembers.Any())
            {
                return null;
            }

            foreach (INamedTypeSymbol typeMember in typeMembers.Order())
            {
                SyntaxNode typeDeclaration = _syntaxGenerator.DeclarationExt(typeMember, _symbolFilter)
                    .AddMemberAttributes(_syntaxGenerator, _symbolFilter, typeMember);

                typeDeclaration = Visit(typeDeclaration, typeMember);

                namespaceNode = _syntaxGenerator.AddMembers(namespaceNode, typeDeclaration);
            }

            return namespaceNode;
        }

        // Compare the equality of two method signatures for the purpose of emitting a "new"
        // keyword on a method's return type. This is *not* meant to be complete implementation,
        // but rather a heuristic to check that one method may hide another.
        private static bool SignatureEquals(IMethodSymbol? method, IMethodSymbol? baseMethod)
        {
            if (method is null || baseMethod is null)
            {
                return false;
            }

            if (method.Equals(baseMethod, SymbolEqualityComparer.Default))
            {
                return true;
            }

            if (method.Name != baseMethod.Name)
            {
                return false;
            }

            if (method.Arity != baseMethod.Arity || method.Parameters.Length != baseMethod.Parameters.Length)
            {
                return false;
            }

            // compare parameter types
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                if (!method.Parameters[i].Type.Equals(baseMethod.Parameters[i].Type, SymbolEqualityComparer.Default))
                {
                    return false;
                }
            }

            // TODO: GenAPI does not currently preserve __arglist as a parameter.
            // Add test case for this branch when that is fixed.
            if (method.IsVararg != baseMethod.IsVararg)
            {
                return false;
            }

            return true;
        }

        // Name hiding through inheritance occurs when classes or structs redeclare names that were inherited from base classes.This type of name hiding takes one of the following forms:
        // - A constant, field, property, event, or type introduced in a class or struct hides all base class members with the same name.
        // - A method introduced in a class or struct hides all non-method base class members with the same name, and all base class methods with the same signature(�7.6).
        // - An indexer introduced in a class or struct hides all base class indexers with the same signature(�7.6) .
        private bool HidesBaseMember(ISymbol member)
        {
            if (member.IsOverride)
            {
                return false;
            }

            if (member.ContainingType.BaseType is not INamedTypeSymbol baseType)
            {
                return false;
            }

            if (member is IMethodSymbol method)
            {
                // If they're methods, compare their names and signatures.
                return baseType.GetMembers(member.Name)
                    .Any(baseMember => _symbolFilter.Include(baseMember) &&
                         (baseMember.Kind != SymbolKind.Method ||
                          SignatureEquals(method, (IMethodSymbol)baseMember)));
            }
            else if (member is IPropertySymbol prop && prop.IsIndexer)
            {
                // If they're indexers, compare their signatures.
                return baseType.GetMembers(member.Name)
                    .Any(baseMember => baseMember is IPropertySymbol baseProperty &&
                         _symbolFilter.Include(baseMember) &&
                         (SignatureEquals(prop.GetMethod, baseProperty.GetMethod) ||
                          SignatureEquals(prop.SetMethod, baseProperty.SetMethod)));
            }
            else
            {
                // For all other kinds of members, compare their names.
                return baseType.GetMembers(member.Name)
                    .Any(_symbolFilter.Include);
            }
        }

        private SyntaxNode Visit(SyntaxNode namedTypeNode, INamedTypeSymbol namedType)
        {
            IEnumerable<ISymbol> members = namedType.GetMembers().Where(_symbolFilter.Include);

            // If it's a value type
            if (namedType.TypeKind == TypeKind.Struct)
            {
                namedTypeNode = _syntaxGenerator.AddMembers(namedTypeNode, namedType.SynthesizeDummyFields(_symbolFilter));
            }

            namedTypeNode = _syntaxGenerator.AddMembers(namedTypeNode, namedType.TryGetInternalDefaultConstructor(_symbolFilter));

            foreach (ISymbol member in members.Order())
            {
                // If the method is ExplicitInterfaceImplementation and is derived from an interface that was filtered out, we must filter out it either.
                if (member is IMethodSymbol method &&
                    method.MethodKind == MethodKind.ExplicitInterfaceImplementation &&
                    method.ExplicitInterfaceImplementations.Any(m => !_symbolFilter.Include(m.ContainingSymbol) ||
                        // if explicit interface implementation method has inaccessible type argument
                        m.ContainingType.HasInaccessibleTypeArgument(_symbolFilter)))
                {
                    continue;
                }
                // If the property is derived from an interface that was filter out, we must filtered out it either.
                if (member is IPropertySymbol property && !property.ExplicitInterfaceImplementations.IsEmpty &&
                    property.ExplicitInterfaceImplementations.Any(m => !_symbolFilter.Include(m.ContainingSymbol)))
                {
                    continue;
                }

                SyntaxNode memberDeclaration = _syntaxGenerator.DeclarationExt(member, _symbolFilter)
                    .AddMemberAttributes(_syntaxGenerator, _symbolFilter, member);

                if (member is INamedTypeSymbol nestedTypeSymbol)
                {
                    memberDeclaration = Visit(memberDeclaration, nestedTypeSymbol);
                }

                if (HidesBaseMember(member))
                {
                    DeclarationModifiers mods = _syntaxGenerator.GetModifiers(memberDeclaration);
                    memberDeclaration = _syntaxGenerator.WithModifiers(memberDeclaration, mods.WithIsNew(isNew: true));
                }

                try
                {
                    namedTypeNode = _syntaxGenerator.AddMembers(namedTypeNode, memberDeclaration);
                }
                catch (InvalidOperationException e)
                {
                    // re-throw the InvalidOperationException with the symbol that caused it.
                    throw new InvalidOperationException($"Adding member {member.ToDisplayString()} to the " +
                        $"named type {namedTypeNode} failed with an exception {e.Message}");
                }
            }

            return namedTypeNode;
        }

        private SyntaxNode GenerateAssemblyAttributes(IAssemblySymbol assembly, SyntaxNode compilationUnit)
        {
            foreach (AttributeData? attribute in assembly.GetAttributes().ExcludeNonVisibleOutsideOfAssembly(_symbolFilter))
            {
                compilationUnit = _syntaxGenerator.AddAttributes(compilationUnit, _syntaxGenerator.Attribute(attribute)
                    .WithTrailingTrivia(SyntaxFactory.LineFeed));
            }
            return compilationUnit;
        }

        private SyntaxNode GenerateForwardedTypeAssemblyAttributes(IAssemblySymbol assembly, SyntaxNode compilationUnit)
        {
            foreach (INamedTypeSymbol symbol in assembly.GetForwardedTypes().Where(_symbolFilter.Include))
            {
                if (symbol.TypeKind != TypeKind.Error)
                {
                    TypeSyntax typeSyntaxNode = (TypeSyntax)_syntaxGenerator.TypeExpression(symbol);
                    compilationUnit = _syntaxGenerator.AddAttributes(compilationUnit,
                        _syntaxGenerator.Attribute("System.Runtime.CompilerServices.TypeForwardedToAttribute",
                            SyntaxFactory.TypeOfExpression(typeSyntaxNode)).WithTrailingTrivia(SyntaxFactory.LineFeed));
                }
                else
                {
                    _logger.LogWarning(string.Format(
                        "Could not resolve type '{0}' in containing assembly '{1}' via type forward. Make sure that the assembly is provided as a reference and contains the type.",
                        symbol.ToDisplayString(),
                        $"{symbol.ContainingAssembly.Name}.dll"));
                }
            }

            return compilationUnit;
        }

        private static IEnumerable<INamespaceSymbol> EnumerateNamespaces(IAssemblySymbol assemblySymbol)
        {
            Stack<INamespaceSymbol> stack = new();
            stack.Push(assemblySymbol.GlobalNamespace);

            while (stack.Count > 0)
            {
                INamespaceSymbol current = stack.Pop();

                yield return current;

                foreach (INamespaceSymbol subNamespace in current.GetNamespaceMembers())
                {
                    stack.Push(subNamespace);
                }
            }
        }

        private OptionSet DefineFormattingOptions()
        {
            /// TODO: consider to move configuration into file.
            return _adhocWorkspace.Options
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInTypes, true)
                .WithChangedOption(CSharpFormattingOptions.WrappingKeepStatementsOnSingleLine, true)
                .WithChangedOption(CSharpFormattingOptions.WrappingPreserveSingleLine, true)
                .WithChangedOption(CSharpFormattingOptions.IndentBlock, false)
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInMethods, false)
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInProperties, false)
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAccessors, false)
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAnonymousMethods, false)
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInControlBlocks, false)
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAnonymousTypes, false)
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInObjectCollectionArrayInitializers, false)
                .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInLambdaExpressionBody, false)
                .WithChangedOption(CSharpFormattingOptions.NewLineForMembersInObjectInit, false)
                .WithChangedOption(CSharpFormattingOptions.NewLineForMembersInAnonymousTypes, false)
                .WithChangedOption(CSharpFormattingOptions.NewLineForClausesInQuery, false);
        }

        /// <inheritdoc />
        public void Dispose() => _textWriter.Dispose();
    }
}
