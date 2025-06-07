using CodeInitializer.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeInitializer
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(GenerateInterfaceRefactoringProvider)), Shared]
    public class GenerateInterfaceRefactoringProvider : CodeRefactoringProvider
    {
        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var document = context.Document;
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var node = root.FindNode(context.Span);

            if (!(node is ClassDeclarationSyntax classDecl))
                return;

            if (classDecl.Modifiers.Any(SyntaxKind.StaticKeyword))
                return;

            var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            var classSymbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (classSymbol == null)
                return;

            var interfaceName = "I" + classSymbol.Name;
            if (classSymbol.ContainingType != null)
                return;

            var ns = classSymbol.ContainingNamespace.IsGlobalNamespace ? null : classSymbol.ContainingNamespace.ToDisplayString();

            var compilation = await document.Project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
            var interfaceSymbol = compilation.GetTypeByMetadataName($"{ns}.{interfaceName}");
            if(interfaceSymbol != null)
            {
                return;
            }

            if (IsGenericClass(classDecl))
            {
                context.RegisterRefactoring(new GenerateInterfaceWithOptionsAction<Document>(
                $"Generate interface '{interfaceName}' in this file",
                async (options, cancellationToken) =>
                {
                    return await GenerateInterfaceSameFileAsync(options, document, root, classDecl, interfaceName, cancellationToken);
                }, classSymbol));

                context.RegisterRefactoring(new GenerateInterfaceWithOptionsAction<Solution>(
                $"Generate interface '{interfaceName}' in new file",
                async (options, cancellationToken) =>
                {
                    return await GenerateInterfaceNewFileAsync(options, document, classDecl, interfaceName, cancellationToken, ns);
                }, classSymbol));
            }
            else
            {
                context.RegisterRefactoring(CodeAction.Create(
                    $"Generate interface '{interfaceName}' in this file",
                    cancellationToken => GenerateInterfaceSameFileAsync(new InterfaceGenerationOptions(), document, root, classDecl, interfaceName, cancellationToken),
                    "GenerateInterface_SameFile"
                ));

                context.RegisterRefactoring(CodeAction.Create(
                    $"Generate interface '{interfaceName}' in new file",
                    cancellationToken => GenerateInterfaceNewFileAsync(new InterfaceGenerationOptions(), document, classDecl, interfaceName, cancellationToken, ns),
                    "GenerateInterface_NewFile"
                ));
            }
        }

        private bool IsReturnTypeAnyTypeParameter(MethodDeclarationSyntax method, ClassDeclarationSyntax classDecl)
        {
            var classTypeParams = classDecl.TypeParameterList?.Parameters.Select(tp => tp.Identifier.Text).ToHashSet()
                                  ?? new HashSet<string>();

            var methodTypeParams = method.TypeParameterList?.Parameters.Select(tp => tp.Identifier.Text).ToHashSet()
                                   ?? new HashSet<string>();

            if (method.ReturnType is IdentifierNameSyntax id)
            {
                return classTypeParams.Contains(id.Identifier.Text)
                    || methodTypeParams.Contains(id.Identifier.Text);
            }
            return false;
        }

        private InterfaceDeclarationSyntax GenerateInterfaceSyntax(
            string interfaceName,
            ClassDeclarationSyntax classDecl,
            bool includeGenericMembers)
        {
            var members = classDecl.Members
                .Where(m =>
                    m is MethodDeclarationSyntax ||
                    m is PropertyDeclarationSyntax ||
                    m is EventDeclarationSyntax ||
                    m is EventFieldDeclarationSyntax
                )
                .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) && !m.Modifiers.Any(SyntaxKind.StaticKeyword))
                .Where(m =>
                    includeGenericMembers
                    ||
                    (
                        (m is MethodDeclarationSyntax md
                            ? (md.TypeParameterList == null || md.TypeParameterList.Parameters.Count == 0)
                                || !IsReturnTypeAnyTypeParameter(md, classDecl)
                            : true)
                        && (m is PropertyDeclarationSyntax pd
                            ? !(pd.Type is IdentifierNameSyntax idProp
                                && classDecl.TypeParameterList?.Parameters.Any(tp => tp.Identifier.Text == idProp.Identifier.Text) == true)
                            : true)
                        && (m is EventDeclarationSyntax ed
                            ? !(ed.Type is IdentifierNameSyntax idEvt
                                && classDecl.TypeParameterList?.Parameters.Any(tp => tp.Identifier.Text == idEvt.Identifier.Text) == true)
                            : true)
                        && (m is EventFieldDeclarationSyntax efd
                            ? !(efd.Declaration.Type is IdentifierNameSyntax idEfd
                                && classDecl.TypeParameterList?.Parameters.Any(tp => tp.Identifier.Text == idEfd.Identifier.Text) == true)
                            : true)
                    )
                )
                .Select(m =>
                {
                    if (m is MethodDeclarationSyntax method)
                    {
                        return (MemberDeclarationSyntax)SyntaxFactory.MethodDeclaration(method.ReturnType, method.Identifier)
                            .WithParameterList(method.ParameterList)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                            .WithModifiers(new SyntaxTokenList())
                            .WithTypeParameterList(method.TypeParameterList)
                            .WithConstraintClauses(method.ConstraintClauses);
                    }
                    else if (m is PropertyDeclarationSyntax prop)
                    {
                        var accessorList = SyntaxFactory.AccessorList(
                            SyntaxFactory.List(
                                prop.AccessorList?.Accessors
                                    .Where(a => a.Kind() == SyntaxKind.GetAccessorDeclaration || a.Kind() == SyntaxKind.SetAccessorDeclaration)
                                    .Select(a => SyntaxFactory.AccessorDeclaration(a.Kind())
                                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))
                                    ?? Enumerable.Empty<AccessorDeclarationSyntax>()
                            )
                        );
                        return (MemberDeclarationSyntax)SyntaxFactory.PropertyDeclaration(prop.Type, prop.Identifier)
                            .WithAccessorList(accessorList)
                            .WithModifiers(new SyntaxTokenList());
                    }
                    else if (m is EventDeclarationSyntax evt)
                    {
                        return (MemberDeclarationSyntax)SyntaxFactory.EventDeclaration(evt.Type, evt.Identifier)
                            .WithAccessorList(evt.AccessorList)
                            .WithModifiers(new SyntaxTokenList());
                    }
                    else if (m is EventFieldDeclarationSyntax evf)
                    {
                        var declarator = evf.Declaration.Variables.FirstOrDefault();
                        if (declarator != null)
                        {
                            return (MemberDeclarationSyntax)SyntaxFactory.EventFieldDeclaration(
                                SyntaxFactory.VariableDeclaration(evf.Declaration.Type)
                                    .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator))
                            ).WithModifiers(new SyntaxTokenList());
                        }
                    }
                    return null;
                })
                .Where(x => x != null)
                .ToList();

            var interfaceDecl = SyntaxFactory.InterfaceDeclaration(interfaceName)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithMembers(SyntaxFactory.List(members));

            if (includeGenericMembers && classDecl.TypeParameterList != null)
            {
                interfaceDecl = interfaceDecl
                    .WithTypeParameterList(classDecl.TypeParameterList)
                    .WithConstraintClauses(classDecl.ConstraintClauses);
            }

            return interfaceDecl;
        }

        private async Task<Document> GenerateInterfaceSameFileAsync(
            InterfaceGenerationOptions options,
            Document document,
            SyntaxNode root,
            ClassDeclarationSyntax classDecl,
            string interfaceName,
            CancellationToken cancellationToken)
        {

            var interfaceDecl = GenerateInterfaceSyntax(interfaceName, classDecl, options.IncludeGenerics);
            var classWithInterface = AddInterfaceImplementation(classDecl, interfaceName, options.IncludeGenerics);

            SyntaxNode newRoot = null;

            // Check if parent is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax
            if (classDecl.Parent is NamespaceDeclarationSyntax ns)
            {
                var members = ns.Members.ToList();
                var index = members.IndexOf(classDecl);
                if (index >= 0)
                {
                    members[index] = classWithInterface;
                    members.Insert(index, interfaceDecl);
                    var newNs = ns.WithMembers(SyntaxFactory.List(members));
                    newRoot = root.ReplaceNode(ns, newNs);
                }
            }
            else if (classDecl.Parent is CompilationUnitSyntax cu)
            {
                var members = cu.Members.ToList();
                var index = members.IndexOf(classDecl);
                if (index >= 0)
                {
                    members[index] = classWithInterface;
                    members.Insert(index, interfaceDecl);
                    newRoot = cu.WithMembers(SyntaxFactory.List(members));
                }
            }

            if (newRoot == null)
            {
                newRoot = root.InsertNodesBefore(classDecl, new[] { interfaceDecl })
                    .ReplaceNode(classDecl, classWithInterface);
            }

            return document.WithSyntaxRoot(newRoot);
        }


        private async Task<Solution> GenerateInterfaceNewFileAsync(
     InterfaceGenerationOptions options,
     Document document,
     ClassDeclarationSyntax classDecl,
     string interfaceName,
     CancellationToken cancellationToken,
     string nameSpace)
        {
            var docFolder = Path.GetDirectoryName(document.FilePath);
            var interfaceFileName = interfaceName + ".cs";
            var interfaceFilePath = Path.Combine(docFolder, interfaceFileName);

            var interfaceDecl = GenerateInterfaceSyntax(interfaceName, classDecl, options.IncludeGenerics);

            var originalRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var compilationUnit = originalRoot as CompilationUnitSyntax;

            var requiredUsings = compilationUnit?.Usings ?? SyntaxFactory.List<UsingDirectiveSyntax>();

            var cu = SyntaxFactory.CompilationUnit().WithUsings(requiredUsings);

            if (!string.IsNullOrEmpty(nameSpace))
            {
                cu = cu.AddMembers(
                    SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(nameSpace))
                        .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceDecl)));
            }
            else
            {
                cu = cu.AddMembers(interfaceDecl);
            }

            var sourceText = cu.NormalizeWhitespace().ToFullString();

            var classWithInterface = AddInterfaceImplementation(classDecl, interfaceName, options.IncludeGenerics);

            var newRoot = originalRoot.ReplaceNode(classDecl, classWithInterface);
            var updatedDocument = document.WithSyntaxRoot(newRoot);
            var updatedSolution = updatedDocument.Project.Solution;

            var newDocId = DocumentId.CreateNewId(document.Project.Id);
            updatedSolution = updatedSolution.AddDocument(newDocId, interfaceFileName, SourceText.From(sourceText), filePath: interfaceFilePath);

            return updatedSolution;
        }

        public static ClassDeclarationSyntax AddInterfaceImplementation(
            ClassDeclarationSyntax classDecl,
            string interfaceName,
            bool includeGenericMembers)
        {
            // Compose the correct interface type name
            BaseTypeSyntax interfaceType;

            if (includeGenericMembers && classDecl.TypeParameterList != null)
            {
                // Interface is generic, mirror the type parameters in order
                var typeArgs = classDecl.TypeParameterList.Parameters
                    .Select(tp => SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SeparatedList<TypeSyntax>(
                            new[] { SyntaxFactory.IdentifierName(tp.Identifier.Text) }
                        ))).ToList();

                var interfaceGenericName = SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier(interfaceName),
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SeparatedList<TypeSyntax>(
                            classDecl.TypeParameterList.Parameters
                                .Select(tp => SyntaxFactory.IdentifierName(tp.Identifier.Text))
                        )
                    )
                );

                interfaceType = SyntaxFactory.SimpleBaseType(interfaceGenericName);
            }
            else
            {
                // Interface is not generic
                interfaceType = SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(interfaceName));
            }

            // Check if already implemented
            bool alreadyImplemented = classDecl.BaseList != null &&
                classDecl.BaseList.Types.Any(bt =>
                {
                    var name = bt.Type.ToString();
                    // Match both generic and non-generic versions
                    return name.StartsWith(interfaceName);
                });

            if (alreadyImplemented)
                return classDecl; // Do nothing if already present

            // Build new base list
            BaseListSyntax newBaseList;

            if (classDecl.BaseList == null)
            {
                // No base list yet
                newBaseList = SyntaxFactory.BaseList(
                    SyntaxFactory.SeparatedList(new[] { interfaceType })
                );
            }
            else
            {
                // Add to existing base list
                newBaseList = classDecl.BaseList.AddTypes(interfaceType);
            }

            // Return class with updated base list
            return classDecl.WithBaseList(newBaseList);
        }

        bool IsGenericClass(ClassDeclarationSyntax classDecl)
        {
            return classDecl.TypeParameterList != null &&
                   classDecl.TypeParameterList.Parameters.Count > 0;
        }
    }
}
