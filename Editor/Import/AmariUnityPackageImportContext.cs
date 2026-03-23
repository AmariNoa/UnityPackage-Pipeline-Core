using System;
using System.Collections.Generic;
using System.Linq;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportContext
    {
        public AmariUnityPackageImportRequest Request { get; }
        public string PackagePath => Request.PackagePath;
        public IReadOnlyList<string> Tags { get; }
        public bool InteractiveMode { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public string NormalizedPackagePath { get; }

        public AmariUnityPackageImportContext(
            AmariUnityPackageImportRequest request,
            IEnumerable<string> normalizedTags,
            bool interactiveMode)
        {
            Request = request?.Clone() ?? throw new ArgumentNullException(nameof(request));
            Tags = normalizedTags?.ToArray() ?? Array.Empty<string>();
            InteractiveMode = interactiveMode;
            StartedAtUtc = DateTimeOffset.UtcNow;
            NormalizedPackagePath = AmariUnityPackagePipelinePathUtil.NormalizePath(PackagePath);
        }
    }
}
