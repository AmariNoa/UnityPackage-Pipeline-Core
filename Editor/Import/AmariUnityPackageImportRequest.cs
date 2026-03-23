using System;
using System.Collections.Generic;
using System.Linq;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    [Serializable]
    public sealed class AmariUnityPackageImportRequest
    {
        public string PackagePath;
        public string[] Tags = Array.Empty<string>();

        public AmariUnityPackageImportRequest()
        {
        }

        public AmariUnityPackageImportRequest(string packagePath, IEnumerable<string> tags = null)
        {
            PackagePath = packagePath;
            Tags = tags?.ToArray() ?? Array.Empty<string>();
        }

        public AmariUnityPackageImportRequest Clone()
        {
            return new AmariUnityPackageImportRequest(PackagePath, Tags ?? Array.Empty<string>());
        }
    }
}
