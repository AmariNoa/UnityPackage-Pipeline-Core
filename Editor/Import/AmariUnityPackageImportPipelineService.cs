using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    [InitializeOnLoad]
    public static class AmariUnityPackageImportPipeline
    {
        public static IAmariUnityPackageImportPipelineService Service { get; }

        static AmariUnityPackageImportPipeline()
        {
            Service = new AmariUnityPackageImportPipelineService();
        }
    }

    public sealed class AmariUnityPackageImportPipelineService : IAmariUnityPackageImportPipelineService, IDisposable
    {
        private const int PersistStateDebounceMilliseconds = 500;
        private const int CloseFallbackRequiredNoWindowFrames = 10;
        private const int CloseFallbackRequiredNoWindowMilliseconds = 200;
        private const int AbsoluteHangTimeoutMilliseconds = 60000;
        private static bool EnablePrefLog = false;
        private static readonly string[] PackageImportWindowTypeNames =
        {
            "UnityEditor.PackageImport",
            "UnityEditor.PackageImportWindow"
        };

        private readonly AmariUnityPackageImportQueue _queue;
        private readonly AmariUnityPackageImportStateStore _stateStore;
        private readonly AmariUnityPackageImportRunner _runner;
        private readonly AmariUnityPackageImportEventBridge _eventBridge;
        private readonly AmariUnityPackageImportedAssetTracker _assetTracker;
        private readonly AmariUnityPackageImportHookDispatcher _hookDispatcher;
        private readonly AmariUnityPackageImportNotifier _notifier;
        private readonly AmariUnityPackageImportTagValidator _tagValidator;
        private readonly AmariUnityPackageImportTagService _tagService;
        private readonly AmariUnityPackagePreImportAnalyzer _preImportAnalyzer;

        private AmariUnityPackageImportContext _currentContext;
        private string[] _currentTags = Array.Empty<string>();
        private bool _isDisposed;
        private bool _isImporting;
        private bool _interactiveMode = true;
        private AmariUnityPackagePreImportAnalysisMode _preImportAnalysisMode = AmariUnityPackagePreImportAnalysisMode.Full;
        private bool _quietMode;
        private bool _queueChangedPendingInQuietMode;
        private bool _persistStateScheduled;
        private long _persistStateDeadlineUtcTicks;
        private Type[] _packageImportWindowTypes = Array.Empty<Type>();
        private bool _interactiveImportMonitorSubscribed;
        private bool _currentRequestFinalized;
        private bool _currentRequestImportExecutionObserved;
        private bool _currentRequestTerminalEventReceived;
        private bool _currentRequestObservedPackageImportWindowOpen;
        private bool _currentRequestObservedPackageImportWindowClose;
        private bool _currentRequestCloseCandidateActive;
        private int _currentRequestCloseCandidateNoWindowFrames;
        private long _currentRequestCloseCandidateStartUtcTicks;
        private PackageImportWindowKeyboardHint _currentRequestKeyboardHint;
        private bool _currentRequestImportConfirmedByKeyboard;
        private long _currentRequestStartUtcTicks;
        private readonly HashSet<int> _trackedPackageImportWindowIds = new HashSet<int>();
        private readonly Dictionary<int, VisualElement> _trackedPackageImportWindowRoots = new Dictionary<int, VisualElement>();
        private readonly Dictionary<int, EventCallback<DetachFromPanelEvent>> _trackedPackageImportWindowDetachCallbacks =
            new Dictionary<int, EventCallback<DetachFromPanelEvent>>();
        private readonly Dictionary<int, EventCallback<KeyDownEvent>> _trackedPackageImportWindowKeyDownCallbacks =
            new Dictionary<int, EventCallback<KeyDownEvent>>();

        public bool IsImporting => _isImporting;
        public int RemainingCount => _queue.Count;
        public string[] CurrentTags => (string[])_currentTags.Clone();

        public bool InteractiveMode
        {
            get => _interactiveMode;
            set
            {
                _interactiveMode = value;
                PersistState();
            }
        }

        public AmariUnityPackagePreImportAnalysisMode PreImportAnalysisMode
        {
            get => _preImportAnalysisMode;
            set => _preImportAnalysisMode = value;
        }

        public bool QuietMode
        {
            get => _quietMode;
            set
            {
                if (_quietMode == value)
                {
                    return;
                }

                _quietMode = value;
                if (!_quietMode && _queueChangedPendingInQuietMode)
                {
                    RaiseQueueChanged(force: true);
                }
            }
        }

        public event Action<AmariUnityPackageImportResultContext> ImportRequestFinalized
        {
            add => _notifier.ImportRequestFinalized += value;
            remove => _notifier.ImportRequestFinalized -= value;
        }

        public event Action QueueChanged;

        public AmariUnityPackageImportPipelineService()
            : this(
                new AmariUnityPackageImportQueue(),
                new AmariUnityPackageImportStateStore(),
                new AmariUnityPackageImportRunner(),
                new AmariUnityPackageImportEventBridge(),
                new AmariUnityPackageImportedAssetTracker(),
                new AmariUnityPackageImportHookDispatcher(),
                new AmariUnityPackageImportNotifier(),
                new AmariUnityPackageImportTagValidator(),
                new AmariUnityPackageImportTagService(),
                new AmariUnityPackagePreImportAnalyzer())
        {
        }

        internal AmariUnityPackageImportPipelineService(
            AmariUnityPackageImportQueue queue,
            AmariUnityPackageImportStateStore stateStore,
            AmariUnityPackageImportRunner runner,
            AmariUnityPackageImportEventBridge eventBridge,
            AmariUnityPackageImportedAssetTracker assetTracker,
            AmariUnityPackageImportHookDispatcher hookDispatcher,
            AmariUnityPackageImportNotifier notifier,
            AmariUnityPackageImportTagValidator tagValidator,
            AmariUnityPackageImportTagService tagService,
            AmariUnityPackagePreImportAnalyzer preImportAnalyzer)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _eventBridge = eventBridge ?? throw new ArgumentNullException(nameof(eventBridge));
            _assetTracker = assetTracker ?? throw new ArgumentNullException(nameof(assetTracker));
            _hookDispatcher = hookDispatcher ?? throw new ArgumentNullException(nameof(hookDispatcher));
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
            _tagValidator = tagValidator ?? throw new ArgumentNullException(nameof(tagValidator));
            _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
            _preImportAnalyzer = preImportAnalyzer ?? throw new ArgumentNullException(nameof(preImportAnalyzer));

            _eventBridge.ImportCompleted += OnImportCompleted;
            _eventBridge.ImportFailed += OnImportFailed;
            _eventBridge.ImportCancelled += OnImportCancelled;
            _eventBridge.ImportStarted += OnImportStarted;
            _eventBridge.AssetsPostprocessed += OnAssetsPostprocessed;
            _eventBridge.Subscribe();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;

            LoadPersistedState();
        }

        public void Enqueue(AmariUnityPackageImportRequest request)
        {
            if (_isDisposed)
            {
                return;
            }

            if (!_queue.Enqueue(request, out var error) && !string.IsNullOrWhiteSpace(error))
            {
                Debug.LogWarning($"{AmariUnityPackagePipelineLabels.LogPrefix} {error}");
            }

            PersistState();
            RaiseQueueChanged();
        }

        public void EnqueueMultiple(IEnumerable<AmariUnityPackageImportRequest> requests)
        {
            if (_isDisposed)
            {
                return;
            }

            _queue.EnqueueMultiple(requests);
            PersistState();
            RaiseQueueChanged();
        }

        public void StartImport()
        {
            if (_isDisposed)
            {
                return;
            }

            PrefLog($"StartImport called. isImporting={_isImporting}, runnerIsRunning={_runner.IsRunning}, queueCount={_queue.Count}");
            RecoverStaleImportStateIfNeeded("StartImport");

            if (_isImporting || _runner.IsRunning)
            {
                return;
            }

            TryStartCurrentRequest();
        }

        public void ClearQueue()
        {
            if (_isDisposed)
            {
                return;
            }

            RecoverStaleImportStateIfNeeded("ClearQueue");

            if (_isImporting && _queue.TryPeek(out var currentRequest))
            {
                _queue.ReplaceWith(new[] { currentRequest });
            }
            else
            {
                _queue.Clear();
            }

            PersistState();
            RaiseQueueChanged();
        }

        public void ResetPipelineAndClearQueue()
        {
            if (_isDisposed)
            {
                return;
            }

            var remainingBefore = _queue.Count;
            var wasImporting = _isImporting;
            var wasRunnerRunning = _runner.IsRunning;

            if (TryFinalizeCurrentRequest(
                    AmariUnityPackagePipelineOperationStatus.Cancelled,
                    string.Empty,
                    clearRemainingQueue: true,
                    reason: nameof(ResetPipelineAndClearQueue),
                    cancellationReason: AmariUnityPackageImportCancellationReason.PipelineReset))
            {
                LogWarning(
                    $"Pipeline reset finalized active request. remainingBefore={remainingBefore}, " +
                    $"wasImporting={wasImporting.ToString().ToLowerInvariant()}, " +
                    $"wasRunnerRunning={wasRunnerRunning.ToString().ToLowerInvariant()}");
                return;
            }

            StopInteractiveImportWindowMonitor();
            _runner.CompleteCurrent();
            _assetTracker.EndTracking();
            _currentContext = null;
            _currentTags = Array.Empty<string>();
            _isImporting = false;
            _queue.Clear();

            PersistStateNow();
            RaiseQueueChanged();

            LogWarning(
                $"Pipeline reset requested. remainingBefore={remainingBefore}, " +
                $"wasImporting={wasImporting.ToString().ToLowerInvariant()}, " +
                $"wasRunnerRunning={wasRunnerRunning.ToString().ToLowerInvariant()}");
        }

        private void RecoverStaleImportStateIfNeeded(string caller)
        {
            if (!_isImporting || _runner.IsRunning)
            {
                return;
            }

            if (_currentContext == null && _queue.TryPeek(out var staleRequest))
            {
                _currentContext = new AmariUnityPackageImportContext(staleRequest, _currentTags, _interactiveMode);
            }

            if (TryFinalizeCurrentRequest(
                    AmariUnityPackagePipelineOperationStatus.Cancelled,
                    string.Empty,
                    clearRemainingQueue: true,
                    reason: $"StaleRecovery({caller})",
                    cancellationReason: AmariUnityPackageImportCancellationReason.StaleRecovery))
            {
                return;
            }

            LogWarning($"Stale importing state detected in {caller}. Recovering internal state.");
            StopInteractiveImportWindowMonitor();
            _assetTracker.EndTracking();
            _currentContext = null;
            _currentTags = Array.Empty<string>();
            _isImporting = false;
            PersistStateNow();
            RaiseQueueChanged();
        }

        public void RegisterHook(IAmariUnityPackageImportPipelineHook hook)
        {
            _hookDispatcher.Register(hook);
        }

        public void UnregisterHook(IAmariUnityPackageImportPipelineHook hook)
        {
            _hookDispatcher.Unregister(hook);
        }

        public IReadOnlyList<AmariUnityPackageImportRequest> GetQueueSnapshot()
        {
            return _queue.Snapshot();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            PersistStateNow();
            _isDisposed = true;
            StopInteractiveImportWindowMonitor();
            EditorApplication.delayCall -= FlushScheduledPersistState;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
            _eventBridge.ImportCompleted -= OnImportCompleted;
            _eventBridge.ImportFailed -= OnImportFailed;
            _eventBridge.ImportCancelled -= OnImportCancelled;
            _eventBridge.ImportStarted -= OnImportStarted;
            _eventBridge.AssetsPostprocessed -= OnAssetsPostprocessed;
            _eventBridge.Dispose();
        }

        private void LoadPersistedState()
        {
            if (!_stateStore.TryLoad(out var persistedState))
            {
                PersistState();
                return;
            }

            _interactiveMode = persistedState.InteractiveMode;
            _queue.ReplaceWith(persistedState.Queue);
            _isImporting = persistedState.IsImporting;

            if (_isImporting && _queue.Count > 0)
            {
                EditorApplication.delayCall += ResumeAfterDomainReload;
            }
            else if (_isImporting && _queue.Count == 0)
            {
                _isImporting = false;
                PersistStateNow();
            }

            RaiseQueueChanged();
        }

        private void ResumeAfterDomainReload()
        {
            if (_isDisposed)
            {
                return;
            }

            if (!_isImporting || _queue.Count == 0)
            {
                return;
            }

            _runner.CompleteCurrent();
            TryStartCurrentRequest();
        }

        private void TryStartCurrentRequest()
        {
            PrefLog($"TryStartCurrentRequest entered. queueCount={_queue.Count}, interactiveMode={_interactiveMode}, preImportAnalysisMode={_preImportAnalysisMode}");
            if (!_queue.TryPeek(out var request))
            {
                _isImporting = false;
                PersistStateNow();
                RaiseQueueChanged();
                PrefLog("TryStartCurrentRequest aborted: queue empty.");
                return;
            }

            if (!_tagValidator.TryNormalizeAndValidate(request.Tags, out var normalizedTags, out var validationError))
            {
                PrefLog($"TryStartCurrentRequest tag validation failed. packagePath={request.PackagePath}, error={validationError}");
                _currentContext = new AmariUnityPackageImportContext(request, Array.Empty<string>(), _interactiveMode);
                _currentTags = Array.Empty<string>();
                BeginCurrentRequestLifecycleState();
                TryFinalizeCurrentRequest(
                    AmariUnityPackagePipelineOperationStatus.Failed,
                    validationError,
                    clearRemainingQueue: false,
                    reason: "TagValidationFailed");
                return;
            }

            if (_preImportAnalysisMode != AmariUnityPackagePreImportAnalysisMode.Skip &&
                TryFinalizeNoChangeRequest(request, normalizedTags))
            {
                PrefLog($"TryStartCurrentRequest finalized by pre-import no-change. packagePath={request.PackagePath}");
                return;
            }

            _currentContext = new AmariUnityPackageImportContext(request, normalizedTags, _interactiveMode);
            _currentTags = normalizedTags;
            BeginCurrentRequestLifecycleState();
            _assetTracker.BeginTracking(_currentContext);
            _hookDispatcher.DispatchBefore(_currentContext);

            _isImporting = true;
            PersistState();
            RaiseQueueChanged();
            var interactiveMonitorReady = BeginInteractiveImportWindowMonitor();
            if (_interactiveMode && !interactiveMonitorReady)
            {
                PrefLog(
                    $"TryStartCurrentRequest failed before import start. packagePath={request.PackagePath}, " +
                    $"failureReason={AmariUnityPackageImportFailureReason.PackageImportWindowTypesUnresolved}");
                TryFinalizeCurrentRequest(
                    AmariUnityPackagePipelineOperationStatus.Failed,
                    string.Empty,
                    clearRemainingQueue: false,
                    reason: "PackageImportWindowTypesUnresolved",
                    failureReason: AmariUnityPackageImportFailureReason.PackageImportWindowTypesUnresolved);
                return;
            }

            PrefLog($"TryStartCurrentRequest starting import. packagePath={request.PackagePath}, interactive={_interactiveMode}");

            if (_runner.TryStartImport(request.PackagePath, _interactiveMode, out var startError))
            {
                PrefLog($"TryStartCurrentRequest import started. packagePath={request.PackagePath}");
                return;
            }

            PrefLog($"TryStartCurrentRequest failed to start import. packagePath={request.PackagePath}, error={startError}");
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Failed,
                startError,
                clearRemainingQueue: false,
                reason: "ImportRunnerStartFailed");
        }

        private void OnImportCompleted(string packageName)
        {
            if (!_isImporting || _currentContext == null)
            {
                PrefLog(
                    $"OnImportCompleted ignored. packageName={packageName}, isImporting={_isImporting}, " +
                    $"hasContext={(_currentContext != null)}, currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                return;
            }

            PrefLog(
                $"OnImportCompleted received. packageName={packageName}, " +
                $"currentPackagePath={_currentContext.Request?.PackagePath ?? string.Empty}");
            _currentRequestTerminalEventReceived = true;
            ClearCurrentRequestKeyboardHint();
            _hookDispatcher.DispatchCompleted(_currentContext);

            var importedAssets = _assetTracker.GetImportedAssetsSnapshot();
            if (!_tagService.TryApplyTags(importedAssets, _currentTags, out var tagError))
            {
                PrefLog($"OnImportCompleted tag apply failed. packageName={packageName}, error={tagError}");
                TryFinalizeCurrentRequest(
                    AmariUnityPackagePipelineOperationStatus.Failed,
                    tagError,
                    clearRemainingQueue: false,
                    reason: "TagApplyFailedAfterImportCompleted");
                return;
            }

            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Completed,
                string.Empty,
                clearRemainingQueue: false,
                reason: "ImportCompleted");
        }

        private void OnImportFailed(string packageName, string error)
        {
            if (!_isImporting || _currentContext == null)
            {
                PrefLog(
                    $"OnImportFailed ignored. packageName={packageName}, error={error}, isImporting={_isImporting}, " +
                    $"hasContext={(_currentContext != null)}, currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                return;
            }

            PrefLog(
                $"OnImportFailed received. packageName={packageName}, error={error}, " +
                $"currentPackagePath={_currentContext.Request?.PackagePath ?? string.Empty}");
            _currentRequestTerminalEventReceived = true;
            ClearCurrentRequestKeyboardHint();
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Failed,
                error ?? string.Empty,
                clearRemainingQueue: false,
                reason: "ImportFailed");
        }

        private void OnImportStarted(string packageName)
        {
            if (!_isImporting || _currentContext == null)
            {
                PrefLog(
                    $"OnImportStarted ignored. packageName={packageName}, isImporting={_isImporting}, " +
                    $"hasContext={(_currentContext != null)}, currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                return;
            }

            PrefLog(
                $"OnImportStarted received. packageName={packageName}, " +
                $"currentPackagePath={_currentContext.Request?.PackagePath ?? string.Empty}");
            _ = packageName;
            // importPackageStarted can fire before the user confirms Import,
            // so it must not suppress the close-window fallback.
        }

        private void OnImportCancelled(string packageName)
        {
            if (!_isImporting || _currentContext == null)
            {
                PrefLog(
                    $"OnImportCancelled ignored. packageName={packageName}, isImporting={_isImporting}, " +
                    $"hasContext={(_currentContext != null)}, currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                return;
            }

            PrefLog(
                $"OnImportCancelled received. packageName={packageName}, " +
                $"currentPackagePath={_currentContext.Request?.PackagePath ?? string.Empty}");
            _currentRequestTerminalEventReceived = true;
            ClearCurrentRequestKeyboardHint();
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Cancelled,
                string.Empty,
                clearRemainingQueue: true,
                reason: "ImportCancelled",
                cancellationReason: AmariUnityPackageImportCancellationReason.UnityCancelledEvent);
        }

        private void OnAssetsPostprocessed(string[] importedAssets)
        {
            if (!_isImporting || _currentContext == null)
            {
                PrefLog(
                    $"OnAssetsPostprocessed ignored. importedAssetsCount={(importedAssets == null ? 0 : importedAssets.Length)}, " +
                    $"isImporting={_isImporting}, hasContext={(_currentContext != null)}, " +
                    $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                return;
            }

            PrefLog(
                $"OnAssetsPostprocessed received. importedAssetsCount={(importedAssets == null ? 0 : importedAssets.Length)}, " +
                $"currentPackagePath={_currentContext.Request?.PackagePath ?? string.Empty}");
            _currentRequestImportExecutionObserved = true;
            _assetTracker.RecordImportedAssets(importedAssets);
        }

        private void BeginCurrentRequestLifecycleState()
        {
            PrefLog(
                $"BeginCurrentRequestLifecycleState reset flags. " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}, queueCount={_queue.Count}");
            StopInteractiveImportWindowMonitor();
            _currentRequestFinalized = false;
            _currentRequestImportExecutionObserved = false;
            _currentRequestTerminalEventReceived = false;
            _currentRequestObservedPackageImportWindowOpen = false;
            _currentRequestObservedPackageImportWindowClose = false;
            ResetCloseFallbackCandidateState();
            ClearCurrentRequestKeyboardHint();
            _currentRequestImportConfirmedByKeyboard = false;
            _currentRequestStartUtcTicks = DateTime.UtcNow.Ticks;
        }

        private bool TryFinalizeCurrentRequest(
            AmariUnityPackagePipelineOperationStatus status,
            string errorMessage,
            bool clearRemainingQueue,
            string reason,
            AmariUnityPackageImportCancellationReason cancellationReason = AmariUnityPackageImportCancellationReason.None,
            AmariUnityPackageImportFailureReason failureReason = AmariUnityPackageImportFailureReason.None)
        {
            if (status != AmariUnityPackagePipelineOperationStatus.Cancelled)
            {
                cancellationReason = AmariUnityPackageImportCancellationReason.None;
            }

            if (status != AmariUnityPackagePipelineOperationStatus.Failed)
            {
                failureReason = AmariUnityPackageImportFailureReason.None;
            }

            PrefLog(
                $"TryFinalizeCurrentRequest requested. status={status}, clearRemainingQueue={clearRemainingQueue}, " +
                $"reason={reason}, error={errorMessage}, cancellationReason={cancellationReason}, failureReason={failureReason}, " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}, " +
                $"queueHeadPackagePath={(_queue.TryPeek(out var queueHeadForLog) ? queueHeadForLog.PackagePath : string.Empty)}, " +
                $"observedWindowOpen={_currentRequestObservedPackageImportWindowOpen}, observedWindowClose={_currentRequestObservedPackageImportWindowClose}, " +
                $"terminalEventReceived={_currentRequestTerminalEventReceived}, importExecutionObserved={_currentRequestImportExecutionObserved}");
            if (_currentRequestFinalized)
            {
                PrefLog("TryFinalizeCurrentRequest skipped: already finalized.");
                return false;
            }

            if (_currentContext == null && _queue.TryPeek(out var pendingRequest))
            {
                _currentContext = new AmariUnityPackageImportContext(pendingRequest, _currentTags, _interactiveMode);
            }

            if (_currentContext == null)
            {
                PrefLog("TryFinalizeCurrentRequest skipped: current context unavailable.");
                return false;
            }

            _currentRequestFinalized = true;
            FinalizeCurrentRequest(status, errorMessage, clearRemainingQueue, cancellationReason, failureReason);
            return true;
        }

        private void FinalizeCurrentRequest(
            AmariUnityPackagePipelineOperationStatus status,
            string errorMessage,
            bool clearRemainingQueue = false,
            AmariUnityPackageImportCancellationReason cancellationReason = AmariUnityPackageImportCancellationReason.None,
            AmariUnityPackageImportFailureReason failureReason = AmariUnityPackageImportFailureReason.None)
        {
            PrefLog(
                $"FinalizeCurrentRequest begin. status={status}, clearRemainingQueue={clearRemainingQueue}, " +
                $"queueCountBefore={_queue.Count}, currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
            if (!_queue.TryPeek(out var request))
            {
                CleanupCurrentRequestState();
                PrefLog("FinalizeCurrentRequest: queue empty during finalize.");
                return;
            }

            PrefLog($"FinalizeCurrentRequest queue head. requestPackagePath={request.PackagePath}");

            var importedAssets = _assetTracker.GetImportedAssetsSnapshot();
            var resultContext = new AmariUnityPackageImportResultContext(
                _currentContext?.Request ?? request,
                _currentTags,
                importedAssets,
                status,
                errorMessage,
                cancellationReason,
                failureReason);

            _hookDispatcher.DispatchFinalized(resultContext);
            var notifyOk = _notifier.TryNotifyFinalized(resultContext, out var notificationError);
            if (!notifyOk)
            {
                Debug.LogError($"{AmariUnityPackagePipelineLabels.LogPrefix} Integration notification failed: {notificationError}");
            }

            _queue.TryDequeue(out _);
            if (clearRemainingQueue && _queue.Count > 0)
            {
                _queue.Clear();
                LogWarning("Queue cleared due to cancelled package import.");
                PrefLog("FinalizeCurrentRequest: cleared remaining queue.");
            }

            CleanupCurrentRequestState();

            var shouldContinue =
                !clearRemainingQueue &&
                status == AmariUnityPackagePipelineOperationStatus.Completed &&
                notifyOk;
            if (shouldContinue && _queue.Count > 0)
            {
                PrefLog($"FinalizeCurrentRequest: continue to next request. remainingQueue={_queue.Count}");
                TryStartCurrentRequest();
                return;
            }

            if (!shouldContinue && _queue.Count > 0)
            {
                LogWarning($"Queue stopped due to import error. Remaining requests: {_queue.Count}");
                PrefLog($"FinalizeCurrentRequest: queue stopped. remainingQueue={_queue.Count}");
            }
        }

        private void CleanupCurrentRequestState()
        {
            var previousPackagePath = _currentContext?.Request?.PackagePath ?? string.Empty;
            PrefLog($"CleanupCurrentRequestState reset state. previousPackagePath={previousPackagePath}, queueCount={_queue.Count}");
            StopInteractiveImportWindowMonitor();
            _runner.CompleteCurrent();
            _assetTracker.EndTracking();
            _currentContext = null;
            _currentTags = Array.Empty<string>();
            _isImporting = false;
            _currentRequestFinalized = false;
            _currentRequestImportExecutionObserved = false;
            _currentRequestTerminalEventReceived = false;
            _currentRequestObservedPackageImportWindowOpen = false;
            _currentRequestObservedPackageImportWindowClose = false;
            ResetCloseFallbackCandidateState();
            ClearCurrentRequestKeyboardHint();
            _currentRequestImportConfirmedByKeyboard = false;
            _currentRequestStartUtcTicks = 0L;
            PersistStateNow();
            RaiseQueueChanged();
        }

        private bool BeginInteractiveImportWindowMonitor()
        {
            if (_isDisposed || !_interactiveMode)
            {
                StopInteractiveImportWindowMonitor();
                PrefLog(
                    $"BeginInteractiveImportWindowMonitor skipped. isDisposed={_isDisposed}, interactiveMode={_interactiveMode}, " +
                    $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                return true;
            }

            ResolvePackageImportWindowTypes();
            if (_packageImportWindowTypes.Length == 0)
            {
                StopInteractiveImportWindowMonitor();
                PrefLog(
                    $"BeginInteractiveImportWindowMonitor skipped. packageImportWindowTypes unresolved. " +
                    $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                return false;
            }

            if (_interactiveImportMonitorSubscribed)
            {
                PrefLog(
                    $"BeginInteractiveImportWindowMonitor skipped: already subscribed. " +
                    $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                return true;
            }

            EditorApplication.update += MonitorInteractiveImportWindow;
            _interactiveImportMonitorSubscribed = true;
            PrefLog(
                $"BeginInteractiveImportWindowMonitor subscribed. " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
            return true;
        }

        private void StopInteractiveImportWindowMonitor()
        {
            PrefLog(
                $"StopInteractiveImportWindowMonitor invoked. trackedWindowCount={_trackedPackageImportWindowIds.Count}, " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
            StopTrackingPackageImportWindows(markClosedObserved: false);
            _currentRequestObservedPackageImportWindowOpen = false;
            _currentRequestObservedPackageImportWindowClose = false;
            ResetCloseFallbackCandidateState();
            ClearCurrentRequestKeyboardHint();
            _currentRequestImportConfirmedByKeyboard = false;
            _currentRequestStartUtcTicks = 0L;
            if (!_interactiveImportMonitorSubscribed)
            {
                return;
            }

            EditorApplication.update -= MonitorInteractiveImportWindow;
            _interactiveImportMonitorSubscribed = false;
            PrefLog("StopInteractiveImportWindowMonitor unsubscribed.");
        }

        private void MonitorInteractiveImportWindow()
        {
            if (_isDisposed || !_isImporting || _currentContext == null || !_interactiveMode || _currentRequestFinalized)
            {
                StopInteractiveImportWindowMonitor();
                PrefLog(
                    $"MonitorInteractiveImportWindow stopped by guard. isDisposed={_isDisposed}, isImporting={_isImporting}, " +
                    $"hasContext={(_currentContext != null)}, interactiveMode={_interactiveMode}, currentRequestFinalized={_currentRequestFinalized}");
                return;
            }

            ResolvePackageImportWindowTypes();
            if (_packageImportWindowTypes.Length == 0)
            {
                StopInteractiveImportWindowMonitor();
                PrefLog("MonitorInteractiveImportWindow stopped: packageImportWindowTypes unresolved.");
                return;
            }

            if (EditorApplication.isUpdating)
            {
                _currentRequestImportExecutionObserved = true;
            }

            var openWindows = GetOpenPackageImportWindows();
            SyncTrackedPackageImportWindows(openWindows);
            if (_trackedPackageImportWindowIds.Count > 0)
            {
                ResetCloseFallbackCandidateState();
                return;
            }

            if (!_currentRequestCloseCandidateActive &&
                _currentRequestObservedPackageImportWindowOpen &&
                _currentRequestObservedPackageImportWindowClose &&
                !_currentRequestTerminalEventReceived &&
                !_currentRequestImportExecutionObserved)
            {
                PrefLog(
                    $"MonitorInteractiveImportWindow evaluating close fallback. openWindowCount={openWindows.Length}, " +
                    $"currentPackagePath={_currentContext.Request?.PackagePath ?? string.Empty}, " +
                    $"keyboardHint={_currentRequestKeyboardHint}");
            }

            if (_currentRequestTerminalEventReceived)
            {
                StopInteractiveImportWindowMonitor();
                PrefLog("MonitorInteractiveImportWindow stopped: terminal event already received.");
                return;
            }

            if (_currentRequestImportExecutionObserved)
            {
                StopInteractiveImportWindowMonitor();
                PrefLog("MonitorInteractiveImportWindow stopped: import execution observed.");
                return;
            }

            if (_currentRequestImportConfirmedByKeyboard)
            {
                TryHandleCurrentRequestHangTimeout();
                return;
            }

            TryHandleCurrentRequestWindowCloseFallback();
        }

        private void TryHandleCurrentRequestWindowCloseFallback()
        {
            if (!_currentRequestObservedPackageImportWindowOpen ||
                !_currentRequestObservedPackageImportWindowClose ||
                _currentRequestTerminalEventReceived ||
                _currentRequestImportExecutionObserved)
            {
                if (_currentRequestCloseCandidateActive)
                {
                    PrefLog(
                        $"TryHandleCurrentRequestWindowCloseFallback reset candidate by guard. " +
                        $"observedWindowOpen={_currentRequestObservedPackageImportWindowOpen}, " +
                        $"observedWindowClose={_currentRequestObservedPackageImportWindowClose}, " +
                        $"terminalEventReceived={_currentRequestTerminalEventReceived}, " +
                        $"importExecutionObserved={_currentRequestImportExecutionObserved}, " +
                        $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
                }

                ResetCloseFallbackCandidateState();
                return;
            }

            if (_currentRequestImportConfirmedByKeyboard)
            {
                PrefLog(
                    $"TryHandleCurrentRequestWindowCloseFallback suppressed: import already confirmed by keyboard. " +
                    $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}, " +
                    $"keyboardHint={_currentRequestKeyboardHint}");
                ResetCloseFallbackCandidateState();
                return;
            }

            var nowTicks = DateTime.UtcNow.Ticks;
            if (!_currentRequestCloseCandidateActive)
            {
                _currentRequestCloseCandidateActive = true;
                _currentRequestCloseCandidateNoWindowFrames = 1;
                _currentRequestCloseCandidateStartUtcTicks = nowTicks;
                PrefLog(
                    $"TryHandleCurrentRequestWindowCloseFallback started close candidate. " +
                    $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}, " +
                    $"keyboardHint={_currentRequestKeyboardHint}");
                return;
            }

            _currentRequestCloseCandidateNoWindowFrames++;
            var elapsedMilliseconds = 0d;
            if (_currentRequestCloseCandidateStartUtcTicks > 0L)
            {
                elapsedMilliseconds = TimeSpan.FromTicks(nowTicks - _currentRequestCloseCandidateStartUtcTicks).TotalMilliseconds;
            }

            if (_currentRequestCloseCandidateNoWindowFrames < CloseFallbackRequiredNoWindowFrames ||
                elapsedMilliseconds < CloseFallbackRequiredNoWindowMilliseconds)
            {
                return;
            }

            PrefLog(
                $"TryHandleCurrentRequestWindowCloseFallback finalized. " +
                $"frames={_currentRequestCloseCandidateNoWindowFrames}, elapsedMs={elapsedMilliseconds:0}, " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}, " +
                $"keyboardHint={_currentRequestKeyboardHint}");
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Cancelled,
                string.Empty,
                clearRemainingQueue: true,
                reason: "PackageImportWindowClosedFallback",
                cancellationReason: AmariUnityPackageImportCancellationReason.WindowClosedFallback);
        }

        private void TryHandleCurrentRequestHangTimeout()
        {
            if (!_currentRequestImportConfirmedByKeyboard ||
                _currentRequestTerminalEventReceived ||
                _currentRequestImportExecutionObserved ||
                _currentRequestStartUtcTicks <= 0L)
            {
                return;
            }

            var elapsedMilliseconds = TimeSpan
                .FromTicks(DateTime.UtcNow.Ticks - _currentRequestStartUtcTicks)
                .TotalMilliseconds;
            if (elapsedMilliseconds < AbsoluteHangTimeoutMilliseconds)
            {
                return;
            }

            PrefLog(
                $"TryHandleCurrentRequestHangTimeout finalized. elapsedMs={elapsedMilliseconds:0}, " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}, " +
                $"keyboardHint={_currentRequestKeyboardHint}");
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Cancelled,
                string.Empty,
                clearRemainingQueue: true,
                reason: "PackageImportHangTimeoutAfterImportConfirm",
                cancellationReason: AmariUnityPackageImportCancellationReason.HangTimeoutAfterImportConfirm);
        }

        private void ResetCloseFallbackCandidateState()
        {
            _currentRequestCloseCandidateActive = false;
            _currentRequestCloseCandidateNoWindowFrames = 0;
            _currentRequestCloseCandidateStartUtcTicks = 0L;
        }

        private void ClearCurrentRequestKeyboardHint()
        {
            _currentRequestKeyboardHint = PackageImportWindowKeyboardHint.None;
        }

        private static bool TryResolvePackageImportWindowKeyboardHint(
            KeyDownEvent evt,
            out PackageImportWindowKeyboardHint hint)
        {
            hint = PackageImportWindowKeyboardHint.None;
            if (evt == null || evt.ctrlKey || evt.altKey || evt.commandKey || evt.shiftKey)
            {
                return false;
            }

            if (IsTextInputTarget(evt.target))
            {
                return false;
            }

            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                hint = PackageImportWindowKeyboardHint.Import;
                return true;
            }

            if (evt.keyCode == KeyCode.Escape)
            {
                hint = PackageImportWindowKeyboardHint.Cancel;
                return true;
            }

            return false;
        }

        private static bool IsTextInputTarget(object eventTarget)
        {
            if (eventTarget is not VisualElement target)
            {
                return false;
            }

            var current = target;
            while (current != null)
            {
                if (current is TextField ||
                    current.ClassListContains("unity-base-text-field") ||
                    current.ClassListContains("unity-text-field"))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private UnityEngine.Object[] GetOpenPackageImportWindows()
        {
            if (_packageImportWindowTypes == null || _packageImportWindowTypes.Length == 0)
            {
                return Array.Empty<UnityEngine.Object>();
            }

            try
            {
                var windowsById = new Dictionary<int, UnityEngine.Object>();
                for (var i = 0; i < _packageImportWindowTypes.Length; i++)
                {
                    var windowType = _packageImportWindowTypes[i];
                    if (windowType == null)
                    {
                        continue;
                    }

                    var windows = Resources.FindObjectsOfTypeAll(windowType);
                    if (windows == null || windows.Length == 0)
                    {
                        continue;
                    }

                    for (var j = 0; j < windows.Length; j++)
                    {
                        var window = windows[j];
                        if (window == null)
                        {
                            continue;
                        }

                        windowsById[window.GetInstanceID()] = window;
                    }
                }

                if (windowsById.Count == 0)
                {
                    return Array.Empty<UnityEngine.Object>();
                }

                var dedupedWindows = new UnityEngine.Object[windowsById.Count];
                var index = 0;
                foreach (var pair in windowsById)
                {
                    dedupedWindows[index] = pair.Value;
                    index++;
                }

                return dedupedWindows;
            }
            catch
            {
                return Array.Empty<UnityEngine.Object>();
            }
        }

        private void ResolvePackageImportWindowTypes()
        {
            if (_packageImportWindowTypes.Length > 0)
            {
                return;
            }

            var types = new List<Type>(PackageImportWindowTypeNames.Length);
            for (var i = 0; i < PackageImportWindowTypeNames.Length; i++)
            {
                var typeName = PackageImportWindowTypeNames[i];
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }

                var type = typeof(Editor).Assembly.GetType(typeName);
                if (type != null)
                {
                    types.Add(type);
                }
            }

            _packageImportWindowTypes = types.ToArray();
        }

        private void SyncTrackedPackageImportWindows(IReadOnlyList<UnityEngine.Object> openWindows)
        {
            var currentOpenIds = new HashSet<int>();
            if (openWindows != null)
            {
                for (var i = 0; i < openWindows.Count; i++)
                {
                    var windowObj = openWindows[i];
                    if (windowObj == null)
                    {
                        continue;
                    }

                    var instanceId = windowObj.GetInstanceID();
                    currentOpenIds.Add(instanceId);
                    if (_trackedPackageImportWindowIds.Contains(instanceId))
                    {
                        continue;
                    }

                    TrackPackageImportWindow(windowObj, instanceId);
                }
            }

            if (_trackedPackageImportWindowIds.Count == 0)
            {
                return;
            }

            var closedWindowIds = new List<int>();
            foreach (var trackedId in _trackedPackageImportWindowIds)
            {
                if (!currentOpenIds.Contains(trackedId))
                {
                    closedWindowIds.Add(trackedId);
                }
            }

            for (var i = 0; i < closedWindowIds.Count; i++)
            {
                UntrackPackageImportWindow(closedWindowIds[i], markClosedObserved: true);
            }

            if (closedWindowIds.Count > 0)
            {
                PrefLog(
                    $"SyncTrackedPackageImportWindows observed closed windows. closedCount={closedWindowIds.Count}, " +
                    $"remainingTrackedCount={_trackedPackageImportWindowIds.Count}, currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
            }
        }

        private void TrackPackageImportWindow(UnityEngine.Object windowObj, int windowId)
        {
            if (!_trackedPackageImportWindowIds.Add(windowId))
            {
                return;
            }

            PrefLog(
                $"TrackPackageImportWindow tracked. windowId={windowId}, windowType={windowObj?.GetType().FullName ?? string.Empty}, " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
            _currentRequestObservedPackageImportWindowOpen = true;

            if (windowObj is not EditorWindow editorWindow)
            {
                return;
            }

            var root = editorWindow.rootVisualElement;
            if (root == null)
            {
                return;
            }

            EventCallback<DetachFromPanelEvent> detachCallback = _ => OnTrackedPackageImportWindowDetached(windowId);
            EventCallback<KeyDownEvent> keyDownCallback = evt => OnTrackedPackageImportWindowKeyDown(windowId, evt);
            root.RegisterCallback(detachCallback);
            root.RegisterCallback(keyDownCallback, TrickleDown.TrickleDown);
            _trackedPackageImportWindowRoots[windowId] = root;
            _trackedPackageImportWindowDetachCallbacks[windowId] = detachCallback;
            _trackedPackageImportWindowKeyDownCallbacks[windowId] = keyDownCallback;
        }

        private void OnTrackedPackageImportWindowDetached(int windowId)
        {
            PrefLog(
                $"OnTrackedPackageImportWindowDetached. windowId={windowId}, " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
            UntrackPackageImportWindow(windowId, markClosedObserved: true);
        }

        private void OnTrackedPackageImportWindowKeyDown(int windowId, KeyDownEvent evt)
        {
            if (evt == null ||
                !_trackedPackageImportWindowIds.Contains(windowId) ||
                _currentRequestFinalized ||
                _currentContext == null)
            {
                return;
            }

            if (!TryResolvePackageImportWindowKeyboardHint(evt, out var hint) ||
                hint == PackageImportWindowKeyboardHint.None)
            {
                return;
            }

            _currentRequestKeyboardHint = hint;
            if (hint == PackageImportWindowKeyboardHint.Import)
            {
                _currentRequestImportConfirmedByKeyboard = true;
            }

            PrefLog(
                $"OnTrackedPackageImportWindowKeyDown hint set. windowId={windowId}, hint={hint}, " +
                $"keyCode={evt.keyCode}, importConfirmedByKeyboard={_currentRequestImportConfirmedByKeyboard}, " +
                $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
        }

        private void UntrackPackageImportWindow(int windowId, bool markClosedObserved)
        {
            if (_trackedPackageImportWindowRoots.TryGetValue(windowId, out var root) &&
                _trackedPackageImportWindowDetachCallbacks.TryGetValue(windowId, out var callback) &&
                root != null &&
                callback != null)
            {
                try
                {
                    root.UnregisterCallback(callback);
                }
                catch
                {
                    // ignored
                }
            }

            if (_trackedPackageImportWindowRoots.TryGetValue(windowId, out var keyDownRoot) &&
                _trackedPackageImportWindowKeyDownCallbacks.TryGetValue(windowId, out var keyDownCallback) &&
                keyDownRoot != null &&
                keyDownCallback != null)
            {
                try
                {
                    keyDownRoot.UnregisterCallback(keyDownCallback, TrickleDown.TrickleDown);
                }
                catch
                {
                    // ignored
                }
            }

            _trackedPackageImportWindowRoots.Remove(windowId);
            _trackedPackageImportWindowDetachCallbacks.Remove(windowId);
            _trackedPackageImportWindowKeyDownCallbacks.Remove(windowId);
            var removed = _trackedPackageImportWindowIds.Remove(windowId);
            if (removed && markClosedObserved && _currentRequestObservedPackageImportWindowOpen)
            {
                _currentRequestObservedPackageImportWindowClose = true;
                PrefLog(
                    $"UntrackPackageImportWindow marked close observed. windowId={windowId}, " +
                    $"trackedWindowCount={_trackedPackageImportWindowIds.Count}, " +
                    $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
            }
            else if (removed)
            {
                PrefLog(
                    $"UntrackPackageImportWindow removed. windowId={windowId}, markClosedObserved={markClosedObserved}, " +
                    $"trackedWindowCount={_trackedPackageImportWindowIds.Count}, " +
                    $"currentPackagePath={_currentContext?.Request?.PackagePath ?? string.Empty}");
            }
        }

        private void StopTrackingPackageImportWindows(bool markClosedObserved)
        {
            if (_trackedPackageImportWindowIds.Count == 0)
            {
                return;
            }

            var trackedIds = new List<int>(_trackedPackageImportWindowIds);
            for (var i = 0; i < trackedIds.Count; i++)
            {
                UntrackPackageImportWindow(trackedIds[i], markClosedObserved);
            }
        }

        private bool TryFinalizeNoChangeRequest(AmariUnityPackageImportRequest request, string[] normalizedTags)
        {
            if (!_preImportAnalyzer.TryAnalyze(
                    request.PackagePath,
                    out var analysisResult,
                    out var analysisError))
            {
                LogWarning(
                    $"Pre-import analysis failed. Falling back to normal import. " +
                    $"packagePath={request.PackagePath}, reason={analysisError}");
                return false;
            }

            if (!analysisResult.IsNoChange)
            {
                if (!string.IsNullOrWhiteSpace(analysisError))
                {
                    Log($"Pre-import analysis requires package import. packagePath={request.PackagePath}, reason={analysisError}");
                }
                return false;
            }

            _currentContext = new AmariUnityPackageImportContext(request, normalizedTags, _interactiveMode);
            _currentTags = normalizedTags ?? Array.Empty<string>();
            BeginCurrentRequestLifecycleState();
            _assetTracker.BeginTracking(_currentContext);
            _hookDispatcher.DispatchBefore(_currentContext);
            _assetTracker.RecordImportedAssets(analysisResult.ExistingAssetPaths);
            _hookDispatcher.DispatchCompleted(_currentContext);

            if (!_tagService.TryApplyTags(analysisResult.ExistingAssetPaths, _currentTags, out var tagError))
            {
                TryFinalizeCurrentRequest(
                    AmariUnityPackagePipelineOperationStatus.Failed,
                    tagError,
                    clearRemainingQueue: false,
                    reason: "PreImportNoChangeTagApplyFailed");
                return true;
            }

            Log($"Pre-import analysis detected no changes. Skipping package import: {request.PackagePath}");
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Completed,
                string.Empty,
                clearRemainingQueue: false,
                reason: "PreImportNoChangeCompleted");
            return true;
        }

        private void PersistState()
        {
            if (_isDisposed)
            {
                return;
            }

            _persistStateDeadlineUtcTicks = DateTime.UtcNow.AddMilliseconds(PersistStateDebounceMilliseconds).Ticks;
            if (_persistStateScheduled)
            {
                return;
            }

            _persistStateScheduled = true;
            EditorApplication.delayCall += FlushScheduledPersistState;
        }

        private void PersistStateNow()
        {
            if (_isDisposed)
            {
                return;
            }

            _persistStateScheduled = false;
            _persistStateDeadlineUtcTicks = 0L;
            EditorApplication.delayCall -= FlushScheduledPersistState;
            PersistStateImmediate();
        }

        private void FlushScheduledPersistState()
        {
            if (_isDisposed || !_persistStateScheduled)
            {
                _persistStateScheduled = false;
                return;
            }

            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks < _persistStateDeadlineUtcTicks)
            {
                EditorApplication.delayCall += FlushScheduledPersistState;
                return;
            }

            _persistStateScheduled = false;
            _persistStateDeadlineUtcTicks = 0L;
            PersistStateImmediate();
        }

        private void PersistStateImmediate()
        {
            var state = new AmariUnityPackageImportPersistedState
            {
                IsImporting = _isImporting,
                InteractiveMode = _interactiveMode,
                Queue = new List<AmariUnityPackageImportRequest>(_queue.Snapshot())
            };

            _stateStore.Save(state);
        }

        private void RaiseQueueChanged(bool force = false)
        {
            if (!force && _quietMode && _queue.Count > 0)
            {
                _queueChangedPendingInQuietMode = true;
                return;
            }

            _queueChangedPendingInQuietMode = false;
            try
            {
                QueueChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{AmariUnityPackagePipelineLabels.LogPrefix} QueueChanged callback failed: {ex.Message}");
            }
        }

        private void OnBeforeAssemblyReload()
        {
            PersistStateNow();
        }

        private void OnEditorQuitting()
        {
            PersistStateNow();
        }

        private static void PrefLog(string message)
        {
            if (!EnablePrefLog)
            {
                return;
            }

            Debug.Log($"{AmariUnityPackagePipelineLabels.LogPrefix} [PrefLog] {message}");
        }

        private enum PackageImportWindowKeyboardHint
        {
            None = 0,
            Import = 1,
            Cancel = 2
        }

        private void Log(string message)
        {
            if (_quietMode)
            {
                return;
            }

            Debug.Log($"{AmariUnityPackagePipelineLabels.LogPrefix} {message}");
        }

        private void LogWarning(string message)
        {
            if (_quietMode)
            {
                return;
            }

            Debug.LogWarning($"{AmariUnityPackagePipelineLabels.LogPrefix} {message}");
        }
    }
}
