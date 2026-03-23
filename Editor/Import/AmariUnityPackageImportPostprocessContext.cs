using System;
using System.Collections.Generic;
using System.Linq;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportPostprocessContext
    {
        public AmariUnityPackageImportContext ImportContext { get; }
        public IReadOnlyList<string> ImportedAssets { get; }

        public AmariUnityPackageImportPostprocessContext(
            AmariUnityPackageImportContext importContext,
            IEnumerable<string> importedAssets)
        {
            ImportContext = importContext ?? throw new ArgumentNullException(nameof(importContext));
            ImportedAssets = importedAssets?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(AmariUnityPackagePipelinePathUtil.NormalizeAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
        }
    }
}
