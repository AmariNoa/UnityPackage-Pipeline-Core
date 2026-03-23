using System;
using System.Collections.Generic;
using System.Linq;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportQueue
    {
        private readonly List<AmariUnityPackageImportRequest> _items = new();
        private readonly HashSet<string> _pathKeys = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _items.Count;

        public bool Enqueue(AmariUnityPackageImportRequest request, out string error)
        {
            error = string.Empty;
            if (request == null)
            {
                error = "Request is null.";
                return false;
            }

            var pathKey = AmariUnityPackagePipelinePathUtil.NormalizePath(request.PackagePath);
            if (string.IsNullOrWhiteSpace(pathKey))
            {
                error = "PackagePath is empty or invalid.";
                return false;
            }

            if (_pathKeys.Contains(pathKey))
            {
                error = $"Duplicate packagePath skipped: {request.PackagePath}";
                return false;
            }

            var cloned = request.Clone();
            _items.Add(cloned);
            _pathKeys.Add(pathKey);
            return true;
        }

        public int EnqueueMultiple(IEnumerable<AmariUnityPackageImportRequest> requests)
        {
            if (requests == null)
            {
                return 0;
            }

            var addedCount = 0;
            foreach (var request in requests)
            {
                if (Enqueue(request, out _))
                {
                    addedCount++;
                }
            }

            return addedCount;
        }

        public bool TryPeek(out AmariUnityPackageImportRequest request)
        {
            if (_items.Count == 0)
            {
                request = null;
                return false;
            }

            request = _items[0].Clone();
            return true;
        }

        public bool TryDequeue(out AmariUnityPackageImportRequest request)
        {
            if (_items.Count == 0)
            {
                request = null;
                return false;
            }

            request = _items[0].Clone();
            _items.RemoveAt(0);

            var key = AmariUnityPackagePipelinePathUtil.NormalizePath(request.PackagePath);
            if (!string.IsNullOrWhiteSpace(key))
            {
                _pathKeys.Remove(key);
            }

            return true;
        }

        public void Clear()
        {
            _items.Clear();
            _pathKeys.Clear();
        }

        public IReadOnlyList<AmariUnityPackageImportRequest> Snapshot()
        {
            return _items.Select(item => item.Clone()).ToArray();
        }

        public void ReplaceWith(IEnumerable<AmariUnityPackageImportRequest> requests)
        {
            Clear();
            if (requests == null)
            {
                return;
            }

            foreach (var request in requests)
            {
                Enqueue(request, out _);
            }
        }
    }
}
