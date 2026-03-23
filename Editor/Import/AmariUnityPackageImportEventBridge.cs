using System;
using UnityEditor;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportEventBridge : IDisposable
    {
        public event Action<string> ImportCompleted;
        public event Action<string, string> ImportFailed;
        public event Action<string> ImportCancelled;
        public event Action<string[]> AssetsPostprocessed;

        public bool IsSubscribed { get; private set; }

        public void Subscribe()
        {
            if (IsSubscribed)
            {
                return;
            }

            AssetDatabase.importPackageCompleted += OnImportCompleted;
            AssetDatabase.importPackageFailed += OnImportFailed;
            AssetDatabase.importPackageCancelled += OnImportCancelled;
            AmariUnityPackageImportPostprocessRelay.ImportedAssetsReceived += OnImportedAssetsReceived;
            IsSubscribed = true;
        }

        public void Unsubscribe()
        {
            if (!IsSubscribed)
            {
                return;
            }

            AssetDatabase.importPackageCompleted -= OnImportCompleted;
            AssetDatabase.importPackageFailed -= OnImportFailed;
            AssetDatabase.importPackageCancelled -= OnImportCancelled;
            AmariUnityPackageImportPostprocessRelay.ImportedAssetsReceived -= OnImportedAssetsReceived;
            IsSubscribed = false;
        }

        public void Dispose()
        {
            Unsubscribe();
        }

        private void OnImportCompleted(string packageName)
        {
            ImportCompleted?.Invoke(packageName);
        }

        private void OnImportFailed(string packageName, string error)
        {
            ImportFailed?.Invoke(packageName, error);
        }

        private void OnImportCancelled(string packageName)
        {
            ImportCancelled?.Invoke(packageName);
        }

        private void OnImportedAssetsReceived(string[] importedAssets)
        {
            AssetsPostprocessed?.Invoke(importedAssets);
        }
    }

    internal static class AmariUnityPackageImportPostprocessRelay
    {
        public static event Action<string[]> ImportedAssetsReceived;

        public static void Raise(string[] importedAssets)
        {
            ImportedAssetsReceived?.Invoke(importedAssets);
        }
    }

    internal sealed class AmariUnityPackageImportAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (importedAssets == null || importedAssets.Length == 0)
            {
                return;
            }

            AmariUnityPackageImportPostprocessRelay.Raise(importedAssets);
        }
    }
}
