using System;
using System.Collections.Generic;
using System.Linq;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportedAssetTracker
    {
        private readonly HashSet<string> _importedAssets = new(StringComparer.Ordinal);
        private AmariUnityPackageImportContext _currentContext;

        public bool IsTracking => _currentContext != null;
        public AmariUnityPackageImportContext CurrentContext => _currentContext;

        public void BeginTracking(AmariUnityPackageImportContext context)
        {
            _currentContext = context ?? throw new ArgumentNullException(nameof(context));
            _importedAssets.Clear();
        }

        public void RecordImportedAssets(IEnumerable<string> importedAssets)
        {
            if (!IsTracking || importedAssets == null)
            {
                return;
            }

            foreach (var path in importedAssets)
            {
                var normalizedPath = AmariUnityPackagePipelinePathUtil.NormalizeAssetPath(path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                _importedAssets.Add(normalizedPath);
            }
        }

        public IReadOnlyList<string> GetImportedAssetsSnapshot()
        {
            return _importedAssets
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        public void EndTracking()
        {
            _currentContext = null;
            _importedAssets.Clear();
        }
    }
}
