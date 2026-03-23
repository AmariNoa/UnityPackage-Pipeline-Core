using System;
using System.Collections.Generic;
using System.Linq;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportResultContext
    {
        public AmariUnityPackageImportRequest Request { get; }
        public string PackagePath { get; }
        public IReadOnlyList<string> Tags { get; }
        public IReadOnlyList<string> ImportedAssets { get; }
        public AmariUnityPackagePipelineOperationStatus ImportStatus { get; }
        public string ErrorMessage { get; }
        public bool IsCompleted => ImportStatus == AmariUnityPackagePipelineOperationStatus.Completed;

        public AmariUnityPackageImportResultContext(
            AmariUnityPackageImportRequest request,
            IEnumerable<string> normalizedTags,
            IEnumerable<string> importedAssets,
            AmariUnityPackagePipelineOperationStatus importStatus,
            string errorMessage)
        {
            Request = request?.Clone() ?? throw new ArgumentNullException(nameof(request));
            PackagePath = Request.PackagePath;
            Tags = normalizedTags?
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            ImportedAssets = importedAssets?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(AmariUnityPackagePipelinePathUtil.NormalizeAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            ImportStatus = importStatus;
            ErrorMessage = errorMessage ?? string.Empty;
        }
    }
}
