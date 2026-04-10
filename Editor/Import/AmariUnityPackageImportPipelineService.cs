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
        private const int ClosedWindowFallbackDecisionFrames = 30;
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
        private int _currentRequestWindowClosedWithoutTerminalEventUpdateCount;
        private readonly HashSet<int> _trackedPackageImportWindowIds = new HashSet<int>();
        private readonly Dictionary<int, VisualElement> _trackedPackageImportWindowRoots = new Dictionary<int, VisualElement>();
        private readonly Dictionary<int, EventCallback<DetachFromPanelEvent>> _trackedPackageImportWindowDetachCallbacks =
            new Dictionary<int, EventCallback<DetachFromPanelEvent>>();

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
                    "Import queue reset by pipeline request.",
                    clearRemainingQueue: true,
                    reason: nameof(ResetPipelineAndClearQueue)))
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
                    $"Import was cancelled while recovering stale pipeline state: {caller}",
                    clearRemainingQueue: true,
                    reason: $"StaleRecovery({caller})"))
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
            if (!_queue.TryPeek(out var request))
            {
                _isImporting = false;
                PersistStateNow();
                RaiseQueueChanged();
                return;
            }

            if (!_tagValidator.TryNormalizeAndValidate(request.Tags, out var normalizedTags, out var validationError))
            {
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
            BeginInteractiveImportWindowMonitor();

            if (_runner.TryStartImport(request.PackagePath, _interactiveMode, out var startError))
            {
                return;
            }

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
                return;
            }

            _currentRequestTerminalEventReceived = true;
            _hookDispatcher.DispatchCompleted(_currentContext);

            var importedAssets = _assetTracker.GetImportedAssetsSnapshot();
            if (!_tagService.TryApplyTags(importedAssets, _currentTags, out var tagError))
            {
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
                return;
            }

            _currentRequestTerminalEventReceived = true;
            var message = string.IsNullOrWhiteSpace(error)
                ? $"Import failed: {packageName}"
                : error;
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Failed,
                message,
                clearRemainingQueue: false,
                reason: "ImportFailed");
        }

        private void OnImportStarted(string packageName)
        {
            if (!_isImporting || _currentContext == null)
            {
                return;
            }

            _ = packageName;
            // importPackageStarted can fire before the user confirms Import,
            // so it must not suppress the close-window fallback.
        }

        private void OnImportCancelled(string packageName)
        {
            if (!_isImporting || _currentContext == null)
            {
                return;
            }

            _currentRequestTerminalEventReceived = true;
            var message = string.IsNullOrWhiteSpace(packageName)
                ? "Import cancelled."
                : $"Import cancelled: {packageName}";
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Cancelled,
                message,
                clearRemainingQueue: true,
                reason: "ImportCancelled");
        }

        private void OnAssetsPostprocessed(string[] importedAssets)
        {
            if (!_isImporting || _currentContext == null)
            {
                return;
            }

            _currentRequestImportExecutionObserved = true;
            _assetTracker.RecordImportedAssets(importedAssets);
        }

        private void BeginCurrentRequestLifecycleState()
        {
            StopInteractiveImportWindowMonitor();
            _currentRequestFinalized = false;
            _currentRequestImportExecutionObserved = false;
            _currentRequestTerminalEventReceived = false;
            _currentRequestObservedPackageImportWindowOpen = false;
            _currentRequestObservedPackageImportWindowClose = false;
            _currentRequestWindowClosedWithoutTerminalEventUpdateCount = 0;
        }

        private bool TryFinalizeCurrentRequest(
            AmariUnityPackagePipelineOperationStatus status,
            string errorMessage,
            bool clearRemainingQueue,
            string reason)
        {
            _ = reason;
            if (_currentRequestFinalized)
            {
                return false;
            }

            if (_currentContext == null && _queue.TryPeek(out var pendingRequest))
            {
                _currentContext = new AmariUnityPackageImportContext(pendingRequest, _currentTags, _interactiveMode);
            }

            if (_currentContext == null)
            {
                return false;
            }

            _currentRequestFinalized = true;
            FinalizeCurrentRequest(status, errorMessage, clearRemainingQueue);
            return true;
        }

        private void FinalizeCurrentRequest(
            AmariUnityPackagePipelineOperationStatus status,
            string errorMessage,
            bool clearRemainingQueue = false)
        {
            if (!_queue.TryPeek(out var request))
            {
                CleanupCurrentRequestState();
                return;
            }

            var importedAssets = _assetTracker.GetImportedAssetsSnapshot();
            var resultContext = new AmariUnityPackageImportResultContext(
                _currentContext?.Request ?? request,
                _currentTags,
                importedAssets,
                status,
                errorMessage);

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
            }

            CleanupCurrentRequestState();

            var shouldContinue =
                !clearRemainingQueue &&
                status == AmariUnityPackagePipelineOperationStatus.Completed &&
                notifyOk;
            if (shouldContinue && _queue.Count > 0)
            {
                TryStartCurrentRequest();
                return;
            }

            if (!shouldContinue && _queue.Count > 0)
            {
                LogWarning($"Queue stopped due to import error. Remaining requests: {_queue.Count}");
            }
        }

        private void CleanupCurrentRequestState()
        {
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
            _currentRequestWindowClosedWithoutTerminalEventUpdateCount = 0;
            PersistStateNow();
            RaiseQueueChanged();
        }

        private void BeginInteractiveImportWindowMonitor()
        {
            if (_isDisposed || !_interactiveMode)
            {
                StopInteractiveImportWindowMonitor();
                return;
            }

            ResolvePackageImportWindowTypes();
            if (_packageImportWindowTypes.Length == 0)
            {
                StopInteractiveImportWindowMonitor();
                return;
            }

            if (_interactiveImportMonitorSubscribed)
            {
                return;
            }

            EditorApplication.update += MonitorInteractiveImportWindow;
            _interactiveImportMonitorSubscribed = true;
        }

        private void StopInteractiveImportWindowMonitor()
        {
            StopTrackingPackageImportWindows(markClosedObserved: false);
            _currentRequestObservedPackageImportWindowOpen = false;
            _currentRequestObservedPackageImportWindowClose = false;
            _currentRequestWindowClosedWithoutTerminalEventUpdateCount = 0;
            if (!_interactiveImportMonitorSubscribed)
            {
                return;
            }

            EditorApplication.update -= MonitorInteractiveImportWindow;
            _interactiveImportMonitorSubscribed = false;
        }

        private void MonitorInteractiveImportWindow()
        {
            if (_isDisposed || !_isImporting || _currentContext == null || !_interactiveMode || _currentRequestFinalized)
            {
                StopInteractiveImportWindowMonitor();
                return;
            }

            ResolvePackageImportWindowTypes();
            if (_packageImportWindowTypes.Length == 0)
            {
                StopInteractiveImportWindowMonitor();
                return;
            }

            var openWindows = GetOpenPackageImportWindows();
            SyncTrackedPackageImportWindows(openWindows);
            if (_trackedPackageImportWindowIds.Count > 0)
            {
                _currentRequestObservedPackageImportWindowClose = false;
                _currentRequestWindowClosedWithoutTerminalEventUpdateCount = 0;
                return;
            }

            if (_currentRequestTerminalEventReceived)
            {
                StopInteractiveImportWindowMonitor();
                return;
            }

            if (_currentRequestImportExecutionObserved)
            {
                StopInteractiveImportWindowMonitor();
                return;
            }

            // Keep waiting while Unity is actively importing/updating assets.
            // This prevents premature fallback-cancel after pressing Import.
            if (EditorApplication.isUpdating)
            {
                _currentRequestImportExecutionObserved = true;
                _currentRequestWindowClosedWithoutTerminalEventUpdateCount = 0;
                return;
            }

            if (!_currentRequestObservedPackageImportWindowOpen || !_currentRequestObservedPackageImportWindowClose)
            {
                _currentRequestWindowClosedWithoutTerminalEventUpdateCount = 0;
                return;
            }

            _currentRequestWindowClosedWithoutTerminalEventUpdateCount++;
            if (_currentRequestWindowClosedWithoutTerminalEventUpdateCount < ClosedWindowFallbackDecisionFrames)
            {
                return;
            }

            var message = string.IsNullOrWhiteSpace(_currentContext.Request?.PackagePath)
                ? "Import cancelled by closing the Package Import window."
                : $"Import cancelled by closing the Package Import window: {_currentContext.Request.PackagePath}";
            TryFinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Cancelled,
                message,
                clearRemainingQueue: true,
                reason: "PackageImportWindowClosedWithoutTerminalEvent");
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

            if (types.Count == 0)
            {
                try
                {
                    var editorAssemblyTypes = typeof(Editor).Assembly.GetTypes();
                    for (var i = 0; i < editorAssemblyTypes.Length; i++)
                    {
                        var type = editorAssemblyTypes[i];
                        if (type == null ||
                            !typeof(EditorWindow).IsAssignableFrom(type) ||
                            string.IsNullOrWhiteSpace(type.FullName) ||
                            type.FullName.IndexOf("PackageImport", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        types.Add(type);
                    }
                }
                catch
                {
                    // ignored
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
        }

        private void TrackPackageImportWindow(UnityEngine.Object windowObj, int windowId)
        {
            if (!_trackedPackageImportWindowIds.Add(windowId))
            {
                return;
            }

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
            root.RegisterCallback(detachCallback);
            _trackedPackageImportWindowRoots[windowId] = root;
            _trackedPackageImportWindowDetachCallbacks[windowId] = detachCallback;
        }

        private void OnTrackedPackageImportWindowDetached(int windowId)
        {
            UntrackPackageImportWindow(windowId, markClosedObserved: true);
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

            _trackedPackageImportWindowRoots.Remove(windowId);
            _trackedPackageImportWindowDetachCallbacks.Remove(windowId);
            var removed = _trackedPackageImportWindowIds.Remove(windowId);
            if (removed && markClosedObserved && _currentRequestObservedPackageImportWindowOpen)
            {
                _currentRequestObservedPackageImportWindowClose = true;
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
