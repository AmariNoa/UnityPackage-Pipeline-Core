using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public static partial class AmariUnityPackageContentReader
    {
        public static bool TryRead(
            string packagePath,
            out IReadOnlyList<AmariUnityPackageContentEntry> entries,
            out string errorMessage,
            CancellationToken cancellationToken = default)
        {
            entries = Array.Empty<AmariUnityPackageContentEntry>();
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(packagePath))
            {
                errorMessage = "PackagePath is empty or invalid.";
                return false;
            }

            var resolvedPath = packagePath;
            try
            {
                resolvedPath = Path.GetFullPath(packagePath);
            }
            catch
            {
                // Fall back to the original path string when normalization fails.
            }

            if (!File.Exists(resolvedPath))
            {
                errorMessage = $"UnityPackage not found: {resolvedPath}";
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                errorMessage = "Operation cancelled.";
                return false;
            }

            try
            {
                using var stream = File.OpenRead(resolvedPath);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                var records = ReadEntryRecords(gzip, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    errorMessage = "Operation cancelled.";
                    return false;
                }

                entries = records
                    .Select(pair => new AmariUnityPackageContentEntry(
                        pair.Key,
                        pair.Value?.Pathname,
                        pair.Value?.HasAsset ?? false,
                        pair.Value?.AssetSize ?? 0L,
                        pair.Value?.AssetSha256,
                        pair.Value?.HasMeta ?? false,
                        pair.Value?.MetaSha256,
                        pair.Value?.MetaGuid))
                    .OrderBy(entry => entry.Guid ?? string.Empty, StringComparer.Ordinal)
                    .ToArray();
                return true;
            }
            catch (OperationCanceledException)
            {
                errorMessage = "Operation cancelled.";
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to read UnityPackage: {ex.Message}";
                return false;
            }
        }
    }
}
