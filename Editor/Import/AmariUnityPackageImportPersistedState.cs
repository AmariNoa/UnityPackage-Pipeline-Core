using System;
using System.Collections.Generic;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    [Serializable]
    public sealed class AmariUnityPackageImportPersistedState
    {
        public bool IsImporting;
        public bool InteractiveMode = true;
        public List<AmariUnityPackageImportRequest> Queue = new();
    }
}
