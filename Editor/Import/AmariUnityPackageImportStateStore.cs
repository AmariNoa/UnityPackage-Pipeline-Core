using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportStateStore
    {
        public void Save(AmariUnityPackageImportPersistedState state)
        {
            if (state == null)
            {
                return;
            }

            try
            {
                var json = JsonConvert.SerializeObject(state, Formatting.None);
                SessionState.SetString(AmariUnityPackagePipelineLabels.ImportStateSessionKey, json);
            }
            catch (JsonException ex)
            {
                Debug.LogError($"{AmariUnityPackagePipelineLabels.LogPrefix} Failed to serialize import state: {ex.Message}");
            }
        }

        public bool TryLoad(out AmariUnityPackageImportPersistedState state)
        {
            state = null;
            var json = SessionState.GetString(AmariUnityPackagePipelineLabels.ImportStateSessionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                state = JsonConvert.DeserializeObject<AmariUnityPackageImportPersistedState>(json);
                if (state == null)
                {
                    return false;
                }

                state.Queue ??= new System.Collections.Generic.List<AmariUnityPackageImportRequest>();
                return true;
            }
            catch (JsonException ex)
            {
                Debug.LogError($"{AmariUnityPackagePipelineLabels.LogPrefix} Failed to deserialize import state: {ex.Message}");
                Clear();
                return false;
            }
        }

        public void Clear()
        {
            SessionState.EraseString(AmariUnityPackagePipelineLabels.ImportStateSessionKey);
        }
    }
}
