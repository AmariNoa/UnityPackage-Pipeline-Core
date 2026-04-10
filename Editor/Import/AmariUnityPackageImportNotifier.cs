using System;
using System.Collections.Generic;

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

            List<string> errors = null;
            foreach (Action<AmariUnityPackageImportResultContext> handler in _importRequestFinalized.GetInvocationList())
            {
                try
                {
                    handler(context);
                }
                catch (Exception ex)
                {
                    if (errors == null)
                    {
                        errors = new List<string>();
                    }

                    if (!string.IsNullOrWhiteSpace(ex.Message))
                    {
                        errors.Add(ex.Message);
                    }
                }
            }

            if (errors == null || errors.Count == 0)
            {
                return true;
            }

            error = string.Join(" | ", errors);
            return false;
        }
    }
}
