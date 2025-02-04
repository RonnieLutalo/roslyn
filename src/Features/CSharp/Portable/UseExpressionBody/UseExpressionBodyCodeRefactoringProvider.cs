﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeGeneration;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.CodeGeneration;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.UseExpressionBody
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp,
        Name = PredefinedCodeRefactoringProviderNames.UseExpressionBody), Shared]
    internal class UseExpressionBodyCodeRefactoringProvider : SyntaxEditorBasedCodeRefactoringProvider
    {
        private static readonly ImmutableArray<UseExpressionBodyHelper> _helpers = UseExpressionBodyHelper.Helpers;

        private static readonly BidirectionalMap<(UseExpressionBodyHelper helper, bool useExpressionBody), string> s_helperToTitleMap
            = CreateHelperToTitleMap(UseExpressionBodyHelper.Helpers);

        [ImportingConstructor]
        [SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
        public UseExpressionBodyCodeRefactoringProvider()
        {
        }

        private static BidirectionalMap<(UseExpressionBodyHelper helper, bool useExpressionBody), string> CreateHelperToTitleMap(
            ImmutableArray<UseExpressionBodyHelper> helpers)
        {
            return new BidirectionalMap<(UseExpressionBodyHelper helper, bool useExpressionBody), string>(GetKeyValuePairs(helpers));

            static IEnumerable<KeyValuePair<(UseExpressionBodyHelper helper, bool useExpressionBody), string>> GetKeyValuePairs(
                ImmutableArray<UseExpressionBodyHelper> helpers)
            {
                foreach (var helper in helpers)
                {
                    yield return KeyValuePairUtil.Create((helper, useExpressionBody: true), helper.UseExpressionBodyTitle.ToString());
                    yield return KeyValuePairUtil.Create((helper, useExpressionBody: false), helper.UseBlockBodyTitle.ToString());
                }
            }
        }

        protected override ImmutableArray<FixAllScope> SupportedFixAllScopes => AllFixAllScopes;

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var (document, textSpan, cancellationToken) = context;
            if (textSpan.Length > 0)
                return;

            var position = textSpan.Start;
            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var node = root.FindToken(position).Parent!;

            var containingLambda = node.FirstAncestorOrSelf<LambdaExpressionSyntax>();
            if (containingLambda != null &&
                node.AncestorsAndSelf().Contains(containingLambda.Body))
            {
                // don't offer inside a lambda.  Lambdas can be quite large, and it will be very noisy
                // inside the body of one to be offering to use a block/expression body for the containing
                // class member.
                return;
            }

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var options = (CSharpCodeGenerationOptions)await document.GetCodeGenerationOptionsAsync(context.Options, cancellationToken).ConfigureAwait(false);

            foreach (var helper in _helpers)
            {
                var declaration = TryGetDeclaration(helper, text, node, position);
                if (declaration == null)
                    continue;

                var succeeded = TryComputeRefactoring(context, root, declaration, options, helper);
                if (succeeded)
                    return;
            }
        }

        private static SyntaxNode? TryGetDeclaration(
            UseExpressionBodyHelper helper, SourceText text, SyntaxNode node, int position)
        {
            var declaration = GetDeclaration(node, helper);
            if (declaration == null)
                return null;

            if (position < declaration.SpanStart)
            {
                // The user is allowed to be before the starting point of this node, as long as
                // they're only between the start of the node and the start of the same line the
                // node starts on.  This prevents unnecessarily showing this feature in areas like
                // the comment of a method.
                if (!text.AreOnSameLine(position, declaration.SpanStart))
                    return null;
            }

            return declaration;
        }

        private static bool TryComputeRefactoring(
            CodeRefactoringContext context, SyntaxNode root, SyntaxNode declaration,
            CSharpCodeGenerationOptions options, UseExpressionBodyHelper helper)
        {
            var document = context.Document;
            var preference = helper.GetExpressionBodyPreference(options);

            var succeeded = false;
            if (helper.CanOfferUseExpressionBody(preference, declaration, forAnalyzer: false))
            {
                var title = s_helperToTitleMap[(helper, useExpressionBody: true)];
                context.RegisterRefactoring(
                    CodeAction.Create(
                        title,
                        c => UpdateDocumentAsync(
                            document, root, declaration, helper,
                            useExpressionBody: true, cancellationToken: c),
                        title),
                    declaration.Span);
                succeeded = true;
            }

            if (helper.CanOfferUseBlockBody(preference, declaration, forAnalyzer: false, out _, out _))
            {
                var title = s_helperToTitleMap[(helper, useExpressionBody: false)];
                context.RegisterRefactoring(
                    CodeAction.Create(
                        title,
                        c => UpdateDocumentAsync(
                            document, root, declaration, helper,
                            useExpressionBody: false, cancellationToken: c),
                        title),
                    declaration.Span);
                succeeded = true;
            }

            return succeeded;
        }

        private static SyntaxNode? GetDeclaration(SyntaxNode node, UseExpressionBodyHelper helper)
        {
            for (var current = node; current != null; current = current.Parent)
            {
                if (helper.SyntaxKinds.Contains(current.Kind()))
                    return current;
            }

            return null;
        }

        private static async Task<Document> UpdateDocumentAsync(
            Document document, SyntaxNode root, SyntaxNode declaration,
            UseExpressionBodyHelper helper, bool useExpressionBody,
            CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var newRoot = GetUpdatedRoot(semanticModel, root, declaration, helper, useExpressionBody);
            return document.WithSyntaxRoot(newRoot);
        }

        private static SyntaxNode GetUpdatedRoot(
            SemanticModel semanticModel, SyntaxNode root, SyntaxNode declaration,
            UseExpressionBodyHelper helper, bool useExpressionBody)
        {
            var updatedDeclaration = helper.Update(semanticModel, declaration, useExpressionBody);

            var parent = declaration is AccessorDeclarationSyntax
                ? declaration.Parent
                : declaration;
            RoslynDebug.Assert(parent is object);
            var updatedParent = parent.ReplaceNode(declaration, updatedDeclaration)
                                      .WithAdditionalAnnotations(Formatter.Annotation);

            return root.ReplaceNode(parent, updatedParent);
        }

        protected override async Task FixAllAsync(
            Document document,
            ImmutableArray<TextSpan> fixAllSpans,
            SyntaxEditor editor,
            CodeActionOptionsProvider optionsProvider,
            string? equivalenceKey,
            CancellationToken cancellationToken)
        {
            Debug.Assert(equivalenceKey != null);
            var (helper, useExpressionBody) = s_helperToTitleMap[equivalenceKey];

            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var options = (CSharpCodeGenerationOptions)await document.GetCodeGenerationOptionsAsync(optionsProvider, cancellationToken).ConfigureAwait(false);
            var declarationsToFix = GetDeclarationsToFix(fixAllSpans, root, helper, useExpressionBody, options);
            await FixDeclarationsAsync(document, editor, root, declarationsToFix, helper, useExpressionBody, cancellationToken).ConfigureAwait(false);
            return;

            // Local functions.
            static IEnumerable<SyntaxNode> GetDeclarationsToFix(
                ImmutableArray<TextSpan> fixAllSpans,
                SyntaxNode root,
                UseExpressionBodyHelper helper,
                bool useExpressionBody,
                CSharpCodeGenerationOptions options)
            {
                var preference = helper.GetExpressionBodyPreference(options);
                foreach (var span in fixAllSpans)
                {
                    var spanNode = root.FindNode(span);

                    foreach (var node in spanNode.DescendantNodesAndSelf())
                    {
                        if (!helper.IsRelevantDeclarationNode(node) || !helper.SyntaxKinds.Contains(node.Kind()))
                            continue;

                        if (useExpressionBody && helper.CanOfferUseExpressionBody(preference, node, forAnalyzer: false))
                        {
                            yield return node;
                        }
                        else if (!useExpressionBody && helper.CanOfferUseBlockBody(preference, node, forAnalyzer: false, out _, out _))
                        {
                            yield return node;
                        }
                    }
                }
            }

            static async Task FixDeclarationsAsync(
                Document document,
                SyntaxEditor editor,
                SyntaxNode root,
                IEnumerable<SyntaxNode> declarationsToFix,
                UseExpressionBodyHelper helper,
                bool useExpressionBody,
                CancellationToken cancellationToken)
            {
                // Process all declaration nodes in reverse to handle nested declaration updates properly.
                declarationsToFix = declarationsToFix.Reverse();

                // Track all the declaration nodes to be fixed so we can get the latest declaration node in the current root during updates.
                var currentRoot = root.TrackNodes(declarationsToFix);

                foreach (var declaration in declarationsToFix)
                {
                    // Get the current document, root, semanticModel and declaration.
                    document = document.WithSyntaxRoot(currentRoot);
                    currentRoot = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    var currentDeclaration = currentRoot.GetCurrentNodes(declaration).Single();

                    // Fix the current declaration and get updated current root
                    currentRoot = GetUpdatedRoot(semanticModel, currentRoot, currentDeclaration, helper, useExpressionBody);
                }

                // Finally apply the latest current root to the editor.
                editor.ReplaceNode(editor.OriginalRoot, currentRoot);
            }
        }
    }
}
