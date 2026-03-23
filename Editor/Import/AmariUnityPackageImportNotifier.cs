using System;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportNotifier
    {
        private Action<AmariUnityPackageImportResultContext> _importRequestFinalized;

        public event Action<AmariUnityPackageImportResultContext> ImportRequestFinalized
        {
            add => _importRequestFinalized += value;
            remove => _importRequestFinalized -= value;
        }

        public bool TryNotifyFinalized(AmariUnityPackageImportResultContext context, out string error)
        {
            error = string.Empty;
            if (_importRequestFinalized == null)
            {
                return true;
            }

            foreach (Action<AmariUnityPackageImportResultContext> handler in _importRequestFinalized.GetInvocationList())
            {
                try
                {
                    handler(context);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            return true;
        }
    }
}
