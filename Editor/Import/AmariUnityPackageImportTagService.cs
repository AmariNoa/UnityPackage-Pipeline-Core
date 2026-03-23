using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportTagService
    {
        public bool TryApplyTags(
            IReadOnlyList<string> importedAssets,
            IReadOnlyList<string> normalizedTags,
            out string error)
        {
            error = string.Empty;
            if (normalizedTags == null || normalizedTags.Count == 0)
            {
                return true;
            }

            var targetPaths = CollectTopLevelTargets(importedAssets);
            var labelsChanged = false;
            foreach (var targetPath in targetPaths)
            {
                var targetObject = AssetDatabase.LoadMainAssetAtPath(targetPath);
                if (targetObject == null)
                {
                    continue;
                }

                var currentLabels = AssetDatabase.GetLabels(targetObject);
                var mergedLabels = new HashSet<string>(currentLabels, StringComparer.Ordinal);
                var beforeCount = mergedLabels.Count;
                foreach (var tag in normalizedTags)
                {
                    mergedLabels.Add(tag);
                }

                if (mergedLabels.Count == beforeCount)
                {
                    continue;
                }

                try
                {
                    AssetDatabase.SetLabels(targetObject, mergedLabels.ToArray());
                    labelsChanged = true;
                }
                catch (Exception ex)
                {
                    error = $"Failed to apply tags to \"{targetPath}\": {ex.Message}";
                    return false;
                }
            }

            if (labelsChanged)
            {
                AssetDatabase.SaveAssets();
            }

            return true;
        }

        private static IReadOnlyList<string> CollectTopLevelTargets(IReadOnlyList<string> importedAssets)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            if (importedAssets == null)
            {
                return Array.Empty<string>();
            }

            foreach (var assetPath in importedAssets)
            {
                if (!AmariUnityPackagePipelinePathUtil.TryGetTopLevelAssetPath(assetPath, out var topLevelAssetPath))
                {
                    continue;
                }

                targets.Add(topLevelAssetPath);
            }

            return targets.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
    }
}
