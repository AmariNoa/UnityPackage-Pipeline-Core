using System;
using System.Collections.Generic;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public interface IAmariUnityPackageImportPipelineService
    {
        void Enqueue(AmariUnityPackageImportRequest request);
        void EnqueueMultiple(IEnumerable<AmariUnityPackageImportRequest> requests);
        void StartImport();
        void ClearQueue();
        void ResetPipelineAndClearQueue();

        bool IsImporting { get; }
        int RemainingCount { get; }
        string[] CurrentTags { get; }
        bool InteractiveMode { get; set; }
        AmariUnityPackagePreImportAnalysisMode PreImportAnalysisMode { get; set; }
        bool QuietMode { get; set; }

        event Action<AmariUnityPackageImportResultContext> ImportRequestFinalized;
        event Action QueueChanged;

        void RegisterHook(IAmariUnityPackageImportPipelineHook hook);
        void UnregisterHook(IAmariUnityPackageImportPipelineHook hook);
        IReadOnlyList<AmariUnityPackageImportRequest> GetQueueSnapshot();
    }
}
