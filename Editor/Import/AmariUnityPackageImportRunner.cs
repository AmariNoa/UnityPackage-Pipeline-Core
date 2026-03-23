using System;
using System.IO;
using UnityEditor;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportRunner
    {
        public bool IsRunning { get; private set; }

        public bool TryStartImport(string packagePath, bool interactive, out string error)
        {
            error = string.Empty;
            if (IsRunning)
            {
                error = "Import runner is already running.";
                return false;
            }

            var normalizedPath = AmariUnityPackagePipelinePathUtil.NormalizePath(packagePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                error = "PackagePath is empty or invalid.";
                return false;
            }

            if (!File.Exists(normalizedPath))
            {
                error = $"UnityPackage not found: {normalizedPath}";
                return false;
            }

            try
            {
                IsRunning = true;
                AssetDatabase.ImportPackage(normalizedPath, interactive);
                return true;
            }
            catch (Exception ex)
            {
                IsRunning = false;
                error = $"AssetDatabase.ImportPackage failed: {ex.Message}";
                return false;
            }
        }

        public void CompleteCurrent()
        {
            IsRunning = false;
        }
    }
}
