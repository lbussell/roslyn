﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.Formatting
{
    internal partial class FormatCommandHandler
    {
        public CommandState GetCommandState(PasteCommandArgs args, Func<CommandState> nextHandler)
            => nextHandler();

        public void ExecuteCommand(PasteCommandArgs args, Action nextHandler, CommandExecutionContext context)
        {
            using var _ = context.OperationContext.AddScope(allowCancellation: true, EditorFeaturesResources.Formatting_pasted_text);
            var caretPosition = args.TextView.GetCaretPoint(args.SubjectBuffer);

            nextHandler();

            var cancellationToken = context.OperationContext.UserCancellationToken;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                ExecuteCommandWorker(args, caretPosition, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // According to Editor command handler API guidelines, it's best if we return early if cancellation
                // is requested instead of throwing. Otherwise, we could end up in an invalid state due to already
                // calling nextHandler().
            }
        }

        private void ExecuteCommandWorker(PasteCommandArgs args, SnapshotPoint? caretPosition, CancellationToken cancellationToken)
        {
            if (!caretPosition.HasValue)
            {
                return;
            }

            var document = args.SubjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                return;
            }

            if (!_globalOptions.GetOption(FormattingOptionsMetadata.FormatOnPaste, document.Project.Language))
            {
                return;
            }

            var solution = document.Project.Solution;
            if (!solution.Workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
            {
                return;
            }

            var formattingRuleService = solution.Workspace.Services.GetService<IHostDependentFormattingRuleFactoryService>();
            if (formattingRuleService != null && formattingRuleService.ShouldNotFormatOrCommitOnPaste(document))
            {
                return;
            }

            var formattingService = document.GetLanguageService<IFormattingInteractionService>();
            if (formattingService == null || !formattingService.SupportsFormatOnPaste)
            {
                return;
            }

            var trackingSpan = caretPosition.Value.Snapshot.CreateTrackingSpan(caretPosition.Value.Position, 0, SpanTrackingMode.EdgeInclusive);
            var span = trackingSpan.GetSpan(args.SubjectBuffer.CurrentSnapshot).Span.ToTextSpan();
            var changes = formattingService.GetFormattingChangesOnPasteAsync(
                document, span, documentOptions: null, cancellationToken).WaitAndGetResult(cancellationToken);
            if (changes.IsEmpty)
            {
                return;
            }

            solution.Workspace.ApplyTextChanges(document.Id, changes, cancellationToken);
        }
    }
}
