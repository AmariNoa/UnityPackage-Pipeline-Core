using System.Collections.Generic;
using System.Threading;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public static class AmariUnityPackageContentReaders
    {
        private static readonly object SyncRoot = new object();
        private static IAmariUnityPackageContentReadProvider _provider;

        public static void RegisterProvider(IAmariUnityPackageContentReadProvider provider)
        {
            if (provider == null)
            {
                return;
            }

            lock (SyncRoot)
            {
                _provider = provider;
            }
        }

        public static bool UnregisterProvider(IAmariUnityPackageContentReadProvider provider)
        {
            lock (SyncRoot)
            {
                if (!ReferenceEquals(_provider, provider))
                {
                    return false;
                }

                _provider = null;
                return true;
            }
        }

        public static bool TryRead(
            string packagePath,
            out IReadOnlyList<AmariUnityPackageContentEntry> entries,
            out string errorMessage,
            CancellationToken cancellationToken = default)
        {
            IAmariUnityPackageContentReadProvider provider;
            lock (SyncRoot)
            {
                provider = _provider;
            }

            if (provider != null)
            {
                return provider.TryRead(packagePath, out entries, out errorMessage, cancellationToken);
            }

            return AmariUnityPackageContentReader.TryRead(packagePath, out entries, out errorMessage, cancellationToken);
        }
    }
}
