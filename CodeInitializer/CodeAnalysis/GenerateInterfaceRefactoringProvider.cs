using CodeInitializer.CodeAnalysis;
using CodeInitializer.Models;
using Microsoft.CodeAnalysis;
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

namespace SSS.CodeInitializer.Analysis
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(GenerateInterfaceRefactoringProvider)), Shared]
    public class GenerateInterfaceRefactoringProvider : CodeRefactoringProvider
    {
        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var document = context.Document;
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var node = root.FindNode(context.Span);
            var classDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (classDecl == null || classDecl.Modifiers.Any(SyntaxKind.StaticKeyword))
                return;

            var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            var classSymbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (classSymbol == null)
                return;

            var interfaceName = "I" + classSymbol.Name;
            if (classSymbol.ContainingType != null)
                return;

            var ns = classSymbol.ContainingNamespace.IsGlobalNamespace ? null : classSymbol.ContainingNamespace.ToDisplayString();

            context.RegisterRefactoring(new GenerateInterfaceWithOptionsAction(
                "Generate interface",
                async (options, cancellationToken) =>
                {
                    return await GenerateAsync(options, document, root, classDecl, interfaceName, cancellationToken, ns);
                }, classSymbol));
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
            string ns,
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

        private async Task<Document> GenerateAsync(
            InterfaceGenerationOptions options,
            Document document,
            SyntaxNode root,
            ClassDeclarationSyntax classDecl,
            string interfaceName,
            CancellationToken cancellationToken,
            string nameSpace)
        {

            var interfaceDecl = GenerateInterfaceSyntax(interfaceName, classDecl, nameSpace, options.IncludeGenerics);
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


        private async Task<Solution> AddInterfaceToNewFileAsync(
    Document document,
    ClassDeclarationSyntax classDecl,
    InterfaceDeclarationSyntax interfaceDecl,
    string interfaceName,
    string ns,
    CancellationToken cancellationToken)
        {
            var docFolder = Path.GetDirectoryName(document.FilePath);
            var interfaceFileName = interfaceName + ".cs";
            var interfaceFilePath = Path.Combine(docFolder, interfaceFileName);

            var cu = SyntaxFactory.CompilationUnit();
            if (!string.IsNullOrEmpty(ns))
            {
                cu = cu.AddMembers(
                    SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(ns))
                        .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceDecl)));
            }
            else
            {
                cu = cu.AddMembers(interfaceDecl);
            }
            var sourceText = cu.NormalizeWhitespace().ToFullString();

            var updatedDocument = await
                ImplementInterfaceOnClassAsync(document, interfaceName, classDecl, cancellationToken);
            var updatedSolution = updatedDocument.Project.Solution;

            var newDocId = DocumentId.CreateNewId(document.Project.Id);
            updatedSolution = updatedSolution.AddDocument(newDocId, interfaceFileName, SourceText.From(sourceText), filePath: interfaceFilePath);

            return updatedSolution;
        }

        private async Task<Document> ImplementInterfaceOnClassAsync(
            Document document,
            string interfaceName,
            ClassDeclarationSyntax classDecl,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var newClassDecl = classDecl;

            var implemented = classDecl.BaseList?.Types
                .Any(bt => bt.Type.ToString() == interfaceName) ?? false;

            if (!implemented)
            {
                var newBaseList = classDecl.BaseList ?? SyntaxFactory.BaseList();
                var newType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));
                var updatedBaseList = newBaseList.AddTypes(newType);

                newClassDecl = classDecl.WithBaseList(updatedBaseList);
                root = root.ReplaceNode(classDecl, newClassDecl);
            }

            return document.WithSyntaxRoot(root);
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

    }
}
