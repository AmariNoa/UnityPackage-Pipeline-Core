using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    internal sealed class AmariUnityPackagePreImportAnalyzer
    {
        public bool TryAnalyze(
            string packagePath,
            out AmariUnityPackagePreImportAnalysisResult result,
            out string errorMessage)
        {
            result = AmariUnityPackagePreImportAnalysisResult.RequiresImport();
            errorMessage = string.Empty;

            var normalizedPackagePath = AmariUnityPackagePipelinePathUtil.NormalizePath(packagePath);
            if (string.IsNullOrWhiteSpace(normalizedPackagePath))
            {
                errorMessage = "PackagePath is empty or invalid.";
                return false;
            }

            if (!File.Exists(normalizedPackagePath))
            {
                errorMessage = $"UnityPackage not found: {normalizedPackagePath}";
                return false;
            }

            if (!AmariUnityPackageContentReaders.TryRead(
                    normalizedPackagePath,
                    out var entries,
                    out var readError))
            {
                errorMessage = string.IsNullOrWhiteSpace(readError)
                    ? "Failed to read UnityPackage contents."
                    : readError;
                return false;
            }

            if (entries == null || entries.Count == 0)
            {
                errorMessage = "No analyzable pathname/asset entries were found in package.";
                return true;
            }

            try
            {
                var existingAssetPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Pathname) ||
                        (!entry.HasAsset && !entry.HasMeta))
                    {
                        errorMessage = "Entry record is incomplete (missing pathname/asset/meta).";
                        return true;
                    }

                    var assetPath = NormalizeAssetPath(entry.Pathname);
                    if (!TryResolveAssetPath(assetPath, out var absoluteAssetPath))
                    {
                        errorMessage = $"Pathname is not resolvable in project: {assetPath}";
                        return true;
                    }

                    var fileExists = File.Exists(absoluteAssetPath);
                    var directoryExists = Directory.Exists(absoluteAssetPath);
                    if (!fileExists && !directoryExists)
                    {
                        errorMessage = $"Path does not exist in project: {assetPath}";
                        return true;
                    }

                    if (entry.HasAsset)
                    {
                        if (fileExists)
                        {
                            if (!TryComputeFileHash(absoluteAssetPath, out var projectAssetHash))
                            {
                                errorMessage = $"Failed to hash existing asset file: {assetPath}";
                                return true;
                            }

                            if (!string.Equals(projectAssetHash, entry.AssetSha256, StringComparison.Ordinal))
                            {
                                errorMessage = $"Asset file hash differs: {assetPath}";
                                return true;
                            }
                        }
                        else if (!(directoryExists && entry.AssetSize == 0))
                        {
                            errorMessage = $"Asset entry expects file data but target is missing/incompatible: {assetPath}";
                            return true;
                        }
                    }

                    if (entry.HasMeta)
                    {
                        var absoluteMetaPath = $"{absoluteAssetPath}.meta";
                        if (!File.Exists(absoluteMetaPath))
                        {
                            errorMessage = $"Meta file is missing: {assetPath}.meta";
                            return true;
                        }

                        var packageMetaGuid = entry.MetaGuid;
                        if (TryReadMetaGuidFromFile(absoluteMetaPath, out var projectMetaGuid) &&
                            !string.IsNullOrWhiteSpace(packageMetaGuid))
                        {
                            if (!string.Equals(projectMetaGuid, packageMetaGuid, StringComparison.OrdinalIgnoreCase))
                            {
                                errorMessage = $"Meta GUID differs: {assetPath}.meta";
                                return true;
                            }
                        }
                        else
                        {
                            if (!TryComputeFileHash(absoluteMetaPath, out var projectMetaHash))
                            {
                                errorMessage = $"Failed to hash existing meta file: {assetPath}.meta";
                                return true;
                            }

                            if (!string.Equals(projectMetaHash, entry.MetaSha256, StringComparison.Ordinal))
                            {
                                errorMessage = $"Meta file hash differs: {assetPath}.meta";
                                return true;
                            }
                        }
                    }

                    existingAssetPaths.Add(assetPath);
                }

                result = AmariUnityPackagePreImportAnalysisResult.NoChange(existingAssetPaths);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Pre-import analysis failed: {ex.Message}";
                return false;
            }
        }

        private static string NormalizeAssetPath(string pathname)
        {
            var normalized = AmariUnityPackagePipelinePathUtil.NormalizeAssetPath(pathname);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            if (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            return normalized;
        }

        private static bool TryResolveAssetPath(string assetPath, out string absoluteAssetPath)
        {
            absoluteAssetPath = string.Empty;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            if (assetPath.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            var isAssetsPath = string.Equals(assetPath, "Assets", StringComparison.Ordinal) ||
                               assetPath.StartsWith("Assets/", StringComparison.Ordinal);
            var isPackagesPath = string.Equals(assetPath, "Packages", StringComparison.Ordinal) ||
                                 assetPath.StartsWith("Packages/", StringComparison.Ordinal);
            if (!isAssetsPath && !isPackagesPath)
            {
                return false;
            }

            var projectRoot = AmariUnityPackagePipelinePathUtil.NormalizePath(Path.Combine(Application.dataPath, ".."));
            if (string.IsNullOrEmpty(projectRoot))
            {
                return false;
            }

            var combined = Path.Combine(projectRoot, assetPath);
            var normalizedCombined = AmariUnityPackagePipelinePathUtil.NormalizePath(combined);
            if (string.IsNullOrEmpty(normalizedCombined))
            {
                return false;
            }

            if (!normalizedCombined.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedCombined, projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            absoluteAssetPath = normalizedCombined;
            return true;
        }

        private static bool TryComputeFileHash(string filePath, out string hash)
        {
            hash = string.Empty;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using var stream = File.OpenRead(filePath);
                using var sha256 = SHA256.Create();
                var bytes = sha256.ComputeHash(stream);
                hash = BytesToLowerHex(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadMetaGuidFromFile(string metaPath, out string guid)
        {
            guid = string.Empty;
            if (string.IsNullOrWhiteSpace(metaPath) || !File.Exists(metaPath))
            {
                return false;
            }

            try
            {
                foreach (var line in File.ReadLines(metaPath))
                {
                    if (!TryExtractGuidLine(line, out var parsedGuid))
                    {
                        continue;
                    }

                    guid = parsedGuid;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryExtractGuidLine(string line, out string guid)
        {
            guid = string.Empty;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var value = trimmed["guid:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            guid = value;
            return true;
        }

        private static string BytesToLowerHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }

    internal readonly struct AmariUnityPackagePreImportAnalysisResult
    {
        public bool IsNoChange { get; }
        public IReadOnlyList<string> ExistingAssetPaths { get; }

        private AmariUnityPackagePreImportAnalysisResult(
            bool isNoChange,
            IReadOnlyList<string> existingAssetPaths)
        {
            IsNoChange = isNoChange;
            ExistingAssetPaths = existingAssetPaths ?? Array.Empty<string>();
        }

        public static AmariUnityPackagePreImportAnalysisResult RequiresImport()
        {
            return new AmariUnityPackagePreImportAnalysisResult(false, Array.Empty<string>());
        }

        public static AmariUnityPackagePreImportAnalysisResult NoChange(IEnumerable<string> existingAssetPaths)
        {
            var normalized = existingAssetPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(AmariUnityPackagePipelinePathUtil.NormalizeAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            return new AmariUnityPackagePreImportAnalysisResult(true, normalized);
        }
    }
}
