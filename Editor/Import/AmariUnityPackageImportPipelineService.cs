using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

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
            _eventBridge.AssetsPostprocessed += OnAssetsPostprocessed;
            _eventBridge.Subscribe();

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

            _runner.CompleteCurrent();
            _assetTracker.EndTracking();
            _currentContext = null;
            _currentTags = Array.Empty<string>();
            _isImporting = false;
            _queue.Clear();

            PersistState();
            RaiseQueueChanged();

            Debug.LogWarning(
                $"{AmariUnityPackagePipelineLabels.LogPrefix} Pipeline reset requested. " +
                $"remainingBefore={remainingBefore}, wasImporting={wasImporting.ToString().ToLowerInvariant()}, " +
                $"wasRunnerRunning={wasRunnerRunning.ToString().ToLowerInvariant()}");
        }

        private void RecoverStaleImportStateIfNeeded(string caller)
        {
            if (!_isImporting || _runner.IsRunning)
            {
                return;
            }

            Debug.LogWarning($"{AmariUnityPackagePipelineLabels.LogPrefix} Stale importing state detected in {caller}. Recovering internal state.");
            _assetTracker.EndTracking();
            _currentContext = null;
            _currentTags = Array.Empty<string>();
            _isImporting = false;
            PersistState();
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

            _isDisposed = true;
            _eventBridge.ImportCompleted -= OnImportCompleted;
            _eventBridge.ImportFailed -= OnImportFailed;
            _eventBridge.ImportCancelled -= OnImportCancelled;
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
                PersistState();
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
                PersistState();
                RaiseQueueChanged();
                return;
            }

            if (!_tagValidator.TryNormalizeAndValidate(request.Tags, out var normalizedTags, out var validationError))
            {
                _currentContext = new AmariUnityPackageImportContext(request, Array.Empty<string>(), _interactiveMode);
                _currentTags = Array.Empty<string>();
                FinalizeCurrentRequest(AmariUnityPackagePipelineOperationStatus.Failed, validationError);
                return;
            }

            if (TryFinalizeNoChangeRequest(request, normalizedTags))
            {
                return;
            }

            _currentContext = new AmariUnityPackageImportContext(request, normalizedTags, _interactiveMode);
            _currentTags = normalizedTags;
            _assetTracker.BeginTracking(_currentContext);
            _hookDispatcher.DispatchBefore(_currentContext);

            _isImporting = true;
            PersistState();
            RaiseQueueChanged();

            if (_runner.TryStartImport(request.PackagePath, _interactiveMode, out var startError))
            {
                return;
            }

            FinalizeCurrentRequest(AmariUnityPackagePipelineOperationStatus.Failed, startError);
        }

        private void OnImportCompleted(string packageName)
        {
            if (!_isImporting || _currentContext == null)
            {
                return;
            }

            _hookDispatcher.DispatchCompleted(_currentContext);

            var importedAssets = _assetTracker.GetImportedAssetsSnapshot();
            if (!_tagService.TryApplyTags(importedAssets, _currentTags, out var tagError))
            {
                FinalizeCurrentRequest(AmariUnityPackagePipelineOperationStatus.Failed, tagError);
                return;
            }

            FinalizeCurrentRequest(AmariUnityPackagePipelineOperationStatus.Completed, string.Empty);
        }

        private void OnImportFailed(string packageName, string error)
        {
            if (!_isImporting || _currentContext == null)
            {
                return;
            }

            var message = string.IsNullOrWhiteSpace(error)
                ? $"Import failed: {packageName}"
                : error;
            FinalizeCurrentRequest(AmariUnityPackagePipelineOperationStatus.Failed, message);
        }

        private void OnImportCancelled(string packageName)
        {
            if (!_isImporting || _currentContext == null)
            {
                return;
            }

            var message = string.IsNullOrWhiteSpace(packageName)
                ? "Import cancelled."
                : $"Import cancelled: {packageName}";
            FinalizeCurrentRequest(
                AmariUnityPackagePipelineOperationStatus.Failed,
                message,
                clearRemainingQueue: true);
        }

        private void OnAssetsPostprocessed(string[] importedAssets)
        {
            if (!_isImporting || _currentContext == null)
            {
                return;
            }

            _assetTracker.RecordImportedAssets(importedAssets);
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
                Debug.LogWarning($"{AmariUnityPackagePipelineLabels.LogPrefix} Queue cleared due to cancelled package import.");
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
                Debug.LogWarning($"{AmariUnityPackagePipelineLabels.LogPrefix} Queue stopped due to import error. Remaining requests: {_queue.Count}");
            }
        }

        private void CleanupCurrentRequestState()
        {
            _runner.CompleteCurrent();
            _assetTracker.EndTracking();
            _currentContext = null;
            _currentTags = Array.Empty<string>();
            _isImporting = false;
            PersistState();
            RaiseQueueChanged();
        }

        private bool TryFinalizeNoChangeRequest(AmariUnityPackageImportRequest request, string[] normalizedTags)
        {
            if (!_preImportAnalyzer.TryAnalyze(
                    request.PackagePath,
                    out var analysisResult,
                    out var analysisError))
            {
                Debug.LogWarning(
                    $"{AmariUnityPackagePipelineLabels.LogPrefix} Pre-import analysis failed. " +
                    $"Falling back to normal import. packagePath={request.PackagePath}, reason={analysisError}");
                return false;
            }

            if (!analysisResult.IsNoChange)
            {
                if (!string.IsNullOrWhiteSpace(analysisError))
                {
                    Debug.Log(
                        $"{AmariUnityPackagePipelineLabels.LogPrefix} Pre-import analysis requires package import. " +
                        $"packagePath={request.PackagePath}, reason={analysisError}");
                }
                return false;
            }

            _currentContext = new AmariUnityPackageImportContext(request, normalizedTags, _interactiveMode);
            _currentTags = normalizedTags ?? Array.Empty<string>();
            _assetTracker.BeginTracking(_currentContext);
            _hookDispatcher.DispatchBefore(_currentContext);
            _assetTracker.RecordImportedAssets(analysisResult.ExistingAssetPaths);
            _hookDispatcher.DispatchCompleted(_currentContext);

            if (!_tagService.TryApplyTags(analysisResult.ExistingAssetPaths, _currentTags, out var tagError))
            {
                FinalizeCurrentRequest(AmariUnityPackagePipelineOperationStatus.Failed, tagError);
                return true;
            }

            Debug.Log(
                $"{AmariUnityPackagePipelineLabels.LogPrefix} Pre-import analysis detected no changes. " +
                $"Skipping package import: {request.PackagePath}");
            FinalizeCurrentRequest(AmariUnityPackagePipelineOperationStatus.Completed, string.Empty);
            return true;
        }

        private void PersistState()
        {
            var state = new AmariUnityPackageImportPersistedState
            {
                IsImporting = _isImporting,
                InteractiveMode = _interactiveMode,
                Queue = new List<AmariUnityPackageImportRequest>(_queue.Snapshot())
            };

            _stateStore.Save(state);
        }

        private void RaiseQueueChanged()
        {
            try
            {
                QueueChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{AmariUnityPackagePipelineLabels.LogPrefix} QueueChanged callback failed: {ex.Message}");
            }
        }
    }
}
