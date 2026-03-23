using System;
using System.IO;
using UnityEngine;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public static class AmariUnityPackagePipelinePathUtil
    {
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim()).Replace('\\', '/');
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static bool IsSamePath(string left, string right)
        {
            var normalizedLeft = NormalizePath(left);
            var normalizedRight = NormalizePath(right);

            return !string.IsNullOrEmpty(normalizedLeft) &&
                   string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeAssetPath(string assetPath)
        {
            return string.IsNullOrWhiteSpace(assetPath) ? string.Empty : assetPath.Trim().Replace('\\', '/');
        }

        public static bool TryGetTopLevelAssetPath(string assetPath, out string topLevelAssetPath)
        {
            topLevelAssetPath = string.Empty;
            var normalized = NormalizeAssetPath(assetPath);
            if (string.IsNullOrEmpty(normalized) ||
                (!string.Equals(normalized, "Assets", StringComparison.Ordinal) &&
                 !normalized.StartsWith("Assets/", StringComparison.Ordinal)))
            {
                return false;
            }

            var segments = normalized.Split('/');
            if (segments.Length == 1)
            {
                topLevelAssetPath = "Assets";
                return true;
            }

            if (!string.Equals(segments[0], "Assets", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(segments[1]))
            {
                return false;
            }

            topLevelAssetPath = $"Assets/{segments[1]}";
            return true;
        }

        public static string ToAssetPathFromAbsolute(string absolutePath)
        {
            var normalizedAbsolute = NormalizePath(absolutePath);
            if (string.IsNullOrEmpty(normalizedAbsolute))
            {
                return string.Empty;
            }

            var projectRoot = NormalizePath(Path.Combine(Application.dataPath, ".."));
            if (string.IsNullOrEmpty(projectRoot))
            {
                return string.Empty;
            }

            if (!normalizedAbsolute.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return normalizedAbsolute[(projectRoot.Length + 1)..];
        }
    }
}
