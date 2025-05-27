using CodeInitializer.DialogBoxes.IncludeGenerics;
using CodeInitializer.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeInitializer.CodeAnalysis
{
    public class GenerateInterfaceWithOptionsAction : CodeActionWithOptions
    {
        private readonly Func<InterfaceGenerationOptions, CancellationToken, Task<Document>> _createChangedDocument;
        private readonly InterfaceGenerationOptions _options;
        private readonly INamedTypeSymbol _className;
        public GenerateInterfaceWithOptionsAction(
            string title,
            Func<InterfaceGenerationOptions, CancellationToken, Task<Document>> createChangedDocument, INamedTypeSymbol classSymbol)
        {
            Title = title;
            _createChangedDocument = createChangedDocument;
            _options = new InterfaceGenerationOptions();
            _className = classSymbol;
        }

        public override string Title { get; }

        public override object GetOptions(CancellationToken token)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dialog = new GenericOptionDialog(_className);
            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                _options.IncludeGenerics = dialog.IncludeGenerics;
                return _options;
            }

            return null; 
        }

        protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(object options, CancellationToken cancellationToken)
        {
            if (options is InterfaceGenerationOptions opts)
            {
                var newDoc = await _createChangedDocument(opts, cancellationToken);
                return new[] { new ApplyChangesOperation(newDoc.Project.Solution) };
            }

            return Enumerable.Empty<CodeActionOperation>();
        }
    }

}
