﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Notification;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Remote
{
    internal sealed class SolutionChecksumUpdater : GlobalOperationAwareIdleProcessor
    {
        private readonly Workspace _workspace;
        private readonly TaskQueue _textChangeQueue;
        private readonly SemaphoreSlim _event;
        private readonly object _gate;

        private CancellationTokenSource _globalOperationCancellationSource;

        // hold last async token
        private IAsyncToken _lastToken;

        public SolutionChecksumUpdater(Workspace workspace, IAsynchronousOperationListenerProvider listenerProvider, CancellationToken shutdownToken)
            : base(listenerProvider.GetListener(FeatureAttribute.SolutionChecksumUpdater),
                   workspace.Services.GetService<IGlobalOperationNotificationService>(),
                   workspace.Options.GetOption(RemoteHostOptions.SolutionChecksumMonitorBackOffTimeSpanInMS), shutdownToken)
        {
            _workspace = workspace;
            _textChangeQueue = new TaskQueue(Listener, TaskScheduler.Default);

            _event = new SemaphoreSlim(initialCount: 0);
            _gate = new object();

            // start listening workspace change event
            _workspace.WorkspaceChanged += OnWorkspaceChanged;

            // create its own cancellation token source
            _globalOperationCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);

            Start();
        }

        private CancellationToken ShutdownCancellationToken => CancellationToken;

        protected override async Task ExecuteAsync()
        {
            lock (_gate)
            {
                _lastToken?.Dispose();
                _lastToken = null;
            }

            // wait for global operation to finish
            await GlobalOperationTask.ConfigureAwait(false);

            // update primary solution in remote host
            await SynchronizePrimaryWorkspaceAsync(_globalOperationCancellationSource.Token).ConfigureAwait(false);
        }

        protected override void PauseOnGlobalOperation()
        {
            var previousCancellationSource = _globalOperationCancellationSource;

            // create new cancellation token source linked with given shutdown cancellation token
            _globalOperationCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(ShutdownCancellationToken);

            CancelAndDispose(previousCancellationSource);
        }

        protected override Task WaitAsync(CancellationToken cancellationToken)
            => _event.WaitAsync(cancellationToken);

        public override void Shutdown()
        {
            base.Shutdown();

            // stop listening workspace change event
            _workspace.WorkspaceChanged -= OnWorkspaceChanged;

            CancelAndDispose(_globalOperationCancellationSource);
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            if (e.Kind == WorkspaceChangeKind.DocumentChanged)
            {
                PushTextChanges(e.OldSolution.GetDocument(e.DocumentId), e.NewSolution.GetDocument(e.DocumentId));
            }

            // record that we are busy
            UpdateLastAccessTime();

            EnqueueChecksumUpdate();
        }

        private void EnqueueChecksumUpdate()
        {
            // event will raised sequencially. no concurrency on this handler
            if (_event.CurrentCount > 0)
            {
                return;
            }

            lock (_gate)
            {
                _lastToken ??= Listener.BeginAsyncOperation(nameof(SolutionChecksumUpdater));
            }

            _event.Release();
        }

        private async Task SynchronizePrimaryWorkspaceAsync(CancellationToken cancellationToken)
        {
            var solution = _workspace.CurrentSolution;
            if (solution.BranchId != _workspace.PrimaryBranchId)
            {
                return;
            }

            var client = await RemoteHostClient.TryGetClientAsync(_workspace, cancellationToken).ConfigureAwait(false);
            if (client == null)
            {
                return;
            }

            using (Logger.LogBlock(FunctionId.SolutionChecksumUpdater_SynchronizePrimaryWorkspace, cancellationToken))
            {
                var checksum = await solution.State.GetChecksumAsync(cancellationToken).ConfigureAwait(false);

                await client.RunRemoteAsync(
                    WellKnownServiceHubService.RemoteHost,
                    nameof(IRemoteHostService.SynchronizePrimaryWorkspaceAsync),
                    solution,
                    new object[] { checksum, solution.WorkspaceVersion },
                    callbackTarget: null,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static void CancelAndDispose(CancellationTokenSource cancellationSource)
        {
            // cancel running tasks
            cancellationSource.Cancel();

            // dispose cancellation token source
            cancellationSource.Dispose();
        }

        private void PushTextChanges(Document oldDocument, Document newDocument)
        {
            // this pushes text changes to the remote side if it can.
            // this is purely perf optimization. whether this pushing text change
            // worked or not doesn't affect feature's functionality.
            //
            // this basically see whether it can cheaply find out text changes
            // between 2 snapshots, if it can, it will send out that text changes to
            // remote side.
            //
            // the remote side, once got the text change, will again see whether
            // it can use that text change information without any high cost and
            // create new snapshot from it.
            //
            // otherwise, it will do the normal behavior of getting full text from
            // VS side. this optimization saves times we need to do full text
            // synchronization for typing scenario.

            if ((oldDocument.TryGetText(out var oldText) == false) ||
                (newDocument.TryGetText(out var newText) == false))
            {
                // we only support case where text already exist
                return;
            }

            // get text changes
            var textChanges = newText.GetTextChanges(oldText);
            if (textChanges.Count == 0)
            {
                // no changes
                return;
            }

            // whole document case
            if (textChanges.Count == 1 && textChanges[0].Span.Length == oldText.Length)
            {
                // no benefit here. pulling from remote host is more efficient
                return;
            }

            // only cancelled when remote host gets shutdown
            _textChangeQueue.ScheduleTask(nameof(PushTextChanges), async () =>
            {
                var client = await RemoteHostClient.TryGetClientAsync(_workspace, CancellationToken).ConfigureAwait(false);
                if (client == null)
                {
                    return;
                }

                var state = await oldDocument.State.GetStateChecksumsAsync(CancellationToken).ConfigureAwait(false);

                await client.RunRemoteAsync(
                    WellKnownServiceHubService.RemoteHost,
                    nameof(IRemoteHostService.SynchronizeTextAsync),
                    solution: null,
                    new object[] { oldDocument.Id, state.Text, textChanges },
                    callbackTarget: null,
                    CancellationToken).ConfigureAwait(false);

            }, CancellationToken);
        }
    }
}
