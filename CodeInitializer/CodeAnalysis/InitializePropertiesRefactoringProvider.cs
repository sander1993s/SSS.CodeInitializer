using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Composition;
using System.Threading.Tasks;
using System.Linq;

namespace SSS.CodeInitializer.Analysis
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(InitializePropertiesRefactoringProvider)), Shared]
    public class InitializePropertiesRefactoringProvider : CodeRefactoringProvider
    {
        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var document = context.Document;
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var node = root.FindNode(context.Span);
            var objectCreation = node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
            if (objectCreation == null)
                return;
            if (objectCreation.Initializer == null || objectCreation.Initializer.Expressions.Any())
                return;

            var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            var typeInfo = semanticModel.GetTypeInfo(objectCreation.Type, context.CancellationToken);
            var typeSymbol = typeInfo.Type as INamedTypeSymbol;

            if (typeSymbol == null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(objectCreation.Type, context.CancellationToken);
                typeSymbol = symbolInfo.Symbol as INamedTypeSymbol;
            }

            if (typeSymbol == null)
            {
                var typeName = objectCreation.Type.ToString();
                typeSymbol = semanticModel.Compilation.GetTypeByMetadataName(typeName);
            }

            if (typeSymbol == null)
                return;

            var needsSystemUsing = false;
            var assignments = typeSymbol
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.SetMethod != null && p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
                .Select(p =>
                {
                    bool usedSystem;
                    var expr = GetDefaultValueForType(p.Type, out usedSystem);
                    if (usedSystem) needsSystemUsing = true;
                    return SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(p.Name),
                        expr
                    );
                })
                .ToArray();

            var newInitializer = SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList<ExpressionSyntax>(assignments)
            );

            var newObjectCreation = objectCreation.WithInitializer(newInitializer);

            var newRoot = root.ReplaceNode(objectCreation, newObjectCreation);

            if (needsSystemUsing)
            {
                if (newRoot is CompilationUnitSyntax cu && !cu.Usings.Any(u => u.Name.ToString() == "System"))
                {
                    var systemUsing = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName("System"))
                        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
                    newRoot = cu.WithUsings(cu.Usings.Insert(0, systemUsing));
                }
            }

            var action = CodeAction.Create(
                "Initialize all properties",
                cancellationToken => Task.FromResult(document.WithSyntaxRoot(newRoot)),
                nameof(InitializePropertiesRefactoringProvider)
            );

            context.RegisterRefactoring(action);
        }

        private ExpressionSyntax GetDefaultValueForType(ITypeSymbol typeSymbol, out bool usedSystemType)
        {
            usedSystemType = false;

            if (typeSymbol == null)
                return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

            if (typeSymbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

            if (typeSymbol.IsReferenceType || typeSymbol.TypeKind == TypeKind.Array)
            {
                if (typeSymbol.SpecialType == SpecialType.System_String)
                    return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(""));
                return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            }

            string typeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            switch (typeName)
            {
                case "string":
                    return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(""));
                case "DateTime":
                    usedSystemType = true;
                    return SyntaxFactory.ParseExpression("DateTime.MinValue");
                case "Guid":
                    usedSystemType = true;
                    return SyntaxFactory.ParseExpression("Guid.Empty");
                case "TimeSpan":
                    usedSystemType = true;
                    return SyntaxFactory.ParseExpression("TimeSpan.Zero");
                case "DateTimeOffset":
                    usedSystemType = true;
                    return SyntaxFactory.ParseExpression("DateTimeOffset.MinValue");
                case "decimal":
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0.0m));
                case "float":
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0.0f));
                case "double":
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0.0));
                case "byte":
                case "sbyte":
                case "short":
                case "ushort":
                case "int":
                case "uint":
                case "long":
                case "ulong":
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));
                case "bool":
                    return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression);
                case "char":
                    return SyntaxFactory.LiteralExpression(SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal('\0'));
            }

            if (typeSymbol.TypeKind == TypeKind.Enum)
            {
                var enumType = (INamedTypeSymbol)typeSymbol;
                var firstMember = enumType.GetMembers().OfType<IFieldSymbol>()
                                      .FirstOrDefault(f => f.IsStatic && f.HasConstantValue);
                if (firstMember != null)
                {
                    return SyntaxFactory.ParseExpression($"{typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{firstMember.Name}");
                }
                return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));
            }

            if (typeSymbol.IsValueType)
            {
                return SyntaxFactory.ParseExpression($"new {typeName}()");
            }

            return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        }
    }
}
