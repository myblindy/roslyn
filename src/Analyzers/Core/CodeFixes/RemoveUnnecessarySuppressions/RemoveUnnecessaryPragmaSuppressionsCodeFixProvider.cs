﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.RemoveUnnecessarySuppressions
{
    [ExportCodeFixProvider(LanguageNames.CSharp, LanguageNames.VisualBasic, Name = PredefinedCodeFixProviderNames.RemoveUnnecessaryPragmaSuppressions), Shared]
    internal sealed class RemoveUnnecessaryPragmaSuppressionsCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        [ImportingConstructor]
        [SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
        public RemoveUnnecessaryPragmaSuppressionsCodeFixProvider()
        {
        }

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(IDEDiagnosticIds.RemoveUnnecessarySuppressionDiagnosticId);

        internal override CodeFixCategory CodeFixCategory
            => CodeFixCategory.CodeQuality;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetRequiredSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            foreach (var diagnostic in context.Diagnostics)
            {
                // Defensive check that we are operating on the diagnostic on a pragma.
                if (root.FindTrivia(diagnostic.Location.SourceSpan.Start).HasStructure)
                {
                    context.RegisterCodeFix(
                        new MyCodeAction(c => FixAsync(context.Document, diagnostic, c)),
                        diagnostic);
                }
            }
        }

        protected override Task FixAllAsync(Document document, ImmutableArray<Diagnostic> diagnostics, SyntaxEditor editor, CancellationToken cancellationToken)
        {
            // We need to track unique set of processed nodes when removing the nodes.
            // This is because we generate an unnecessary pragma suppression diagnostic at both the pragma disable and matching pragma restore location
            // with the corresponding restore/disable location as an additional location to be removed.
            // Our code fix ensures that we remove both the disable and restore directives with a single code fix application.
            // So, we need to ensure that we do not attempt to remove the same node multiple times when performing a FixAll in document operation.
            using var _ = PooledHashSet<SyntaxNode>.GetInstance(out var processedNodes);

            foreach (var diagnostic in diagnostics)
            {
                RemoveNode(diagnostic.Location, editor, processedNodes);

                foreach (var location in diagnostic.AdditionalLocations)
                {
                    RemoveNode(location, editor, processedNodes);
                }
            }

            return Task.CompletedTask;

            static void RemoveNode(Location location, SyntaxEditor editor, HashSet<SyntaxNode> processedNodes)
            {
                var node = editor.OriginalRoot.FindTrivia(location.SourceSpan.Start).GetStructure()!;
                if (processedNodes.Add(node))
                {
                    editor.RemoveNode(node);
                }
            }
        }

        private class MyCodeAction : CustomCodeActions.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(AnalyzersResources.Remove_unnecessary_suppression, createChangedDocument, nameof(RemoveUnnecessaryPragmaSuppressionsCodeFixProvider))
            {
            }
        }
    }
}
