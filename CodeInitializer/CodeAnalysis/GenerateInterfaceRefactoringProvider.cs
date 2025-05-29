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
            // Class type parameters
            var classTypeParams = classDecl.TypeParameterList?.Parameters.Select(tp => tp.Identifier.Text).ToHashSet()
                                  ?? new HashSet<string>();
            // Method type parameters
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
                .Where(m => !m.Modifiers.Any(SyntaxKind.PrivateKeyword) && !m.Modifiers.Any(SyntaxKind.StaticKeyword))
                .Where(m =>
                    includeGenericMembers
                    ||
                    (
                        // For methods
                        (m is MethodDeclarationSyntax md
                            // If not generic, always allow
                            ? (md.TypeParameterList == null || md.TypeParameterList.Parameters.Count == 0)
                                // If generic, allow only if return type is not a type parameter
                                || !IsReturnTypeAnyTypeParameter(md, classDecl)
                            : true)
                        // For properties
                        && (m is PropertyDeclarationSyntax pd
                            ? !(pd.Type is IdentifierNameSyntax idProp
                                && classDecl.TypeParameterList?.Parameters.Any(tp => tp.Identifier.Text == idProp.Identifier.Text) == true)
                            : true)
                        // For event declarations
                        && (m is EventDeclarationSyntax ed
                            ? !(ed.Type is IdentifierNameSyntax idEvt
                                && classDecl.TypeParameterList?.Parameters.Any(tp => tp.Identifier.Text == idEvt.Identifier.Text) == true)
                            : true)
                        // For event field declarations
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

            // Update the class declaration to implement the interface
            var classWithInterface = classDecl;
            var implemented = classDecl.BaseList?.Types
                .Any(bt => bt.Type.ToString() == interfaceName) ?? false;
            if (!implemented)
            {
                var newBaseList = classDecl.BaseList ?? SyntaxFactory.BaseList();
                var newType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));
                var updatedBaseList = newBaseList.AddTypes(newType);
                classWithInterface = classDecl.WithBaseList(updatedBaseList);
            }

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

            var updatedDocument = await ImplementInterfaceOnClassAsync(document, interfaceName, classDecl, cancellationToken);
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
    }
}
