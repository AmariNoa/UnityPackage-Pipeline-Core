using System;
using System.Collections.Generic;
using System.Linq;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportQueuePresenter
    {
        private readonly IAmariUnityPackageImportPipelineService _service;

        public AmariUnityPackageImportQueuePresenter(IAmariUnityPackageImportPipelineService service = null)
        {
            _service = service ?? AmariUnityPackageImportPipeline.Service;
        }

        public bool IsImporting => _service.IsImporting;
        public int RemainingCount => _service.RemainingCount;
        public IReadOnlyList<string> CurrentTags => _service.CurrentTags ?? Array.Empty<string>();
        public string CurrentTagsText => string.Join(", ", CurrentTags.Where(tag => !string.IsNullOrWhiteSpace(tag)));

        public void Enqueue(AmariUnityPackageImportRequest request)
        {
            _service.Enqueue(request);
        }

        public void EnqueueMultiple(IEnumerable<AmariUnityPackageImportRequest> requests)
        {
            _service.EnqueueMultiple(requests);
        }

        public void StartImport()
        {
            _service.StartImport();
        }

        public void ClearQueue()
        {
            _service.ClearQueue();
        }

        public IReadOnlyList<AmariUnityPackageImportRequest> GetQueueSnapshot()
        {
            return _service.GetQueueSnapshot();
        }
    }
}
