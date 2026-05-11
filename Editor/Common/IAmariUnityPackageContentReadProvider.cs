using System.Collections.Generic;
using System.Threading;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public interface IAmariUnityPackageContentReadProvider
    {
        bool TryRead(
            string packagePath,
            out IReadOnlyList<AmariUnityPackageContentEntry> entries,
            out string errorMessage,
            CancellationToken cancellationToken = default);
    }
}
