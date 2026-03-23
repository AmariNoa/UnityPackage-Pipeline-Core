using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    internal sealed class AmariUnityPackagePreImportAnalyzer
    {
        private const int TarBlockSize = 512;
        private const int HashReadBufferSize = 8192;
        private const long MaxPathnameBytes = 16 * 1024;
        private const long MaxMetaBytes = 2 * 1024 * 1024;

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

            try
            {
                using var packageStream = File.OpenRead(normalizedPackagePath);
                using var gzipStream = new GZipStream(packageStream, CompressionMode.Decompress);

                var entryRecords = ReadEntryRecords(gzipStream);
                if (entryRecords.Count == 0)
                {
                    errorMessage = "No analyzable pathname/asset entries were found in package.";
                    return true;
                }

                var existingAssetPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var record in entryRecords.Values)
                {
                    if (string.IsNullOrWhiteSpace(record.Pathname) ||
                        (!record.HasAsset && !record.HasMeta))
                    {
                        errorMessage = "Entry record is incomplete (missing pathname/asset/meta).";
                        return true;
                    }

                    var assetPath = NormalizeAssetPath(record.Pathname);
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

                    if (record.HasAsset)
                    {
                        if (fileExists)
                        {
                            if (!TryComputeFileHash(absoluteAssetPath, out var projectAssetHash))
                            {
                                errorMessage = $"Failed to hash existing asset file: {assetPath}";
                                return true;
                            }

                            if (!string.Equals(projectAssetHash, record.AssetHash, StringComparison.Ordinal))
                            {
                                errorMessage = $"Asset file hash differs: {assetPath}";
                                return true;
                            }
                        }
                        else if (!(directoryExists && record.AssetSize == 0))
                        {
                            errorMessage = $"Asset entry expects file data but target is missing/incompatible: {assetPath}";
                            return true;
                        }
                    }

                    if (record.HasMeta)
                    {
                        var absoluteMetaPath = $"{absoluteAssetPath}.meta";
                        if (!File.Exists(absoluteMetaPath))
                        {
                            errorMessage = $"Meta file is missing: {assetPath}.meta";
                            return true;
                        }

                        var packageMetaGuid = record.MetaGuid;
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

                            if (!string.Equals(projectMetaHash, record.MetaHash, StringComparison.Ordinal))
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

        private static Dictionary<string, UnityPackageEntryRecord> ReadEntryRecords(Stream tarStream)
        {
            var records = new Dictionary<string, UnityPackageEntryRecord>(StringComparer.Ordinal);
            var header = new byte[TarBlockSize];
            var readBuffer = new byte[HashReadBufferSize];

            while (true)
            {
                var headerRead = ReadExactly(tarStream, header, 0, TarBlockSize);
                if (headerRead == 0)
                {
                    break;
                }

                if (headerRead < TarBlockSize)
                {
                    throw new EndOfStreamException("Unexpected end of tar header.");
                }

                if (IsAllZeroBlock(header))
                {
                    break;
                }

                var entryName = ReadNullTerminatedAscii(header, 0, 100);
                var entrySize = ParseTarOctal(header, 124, 12);
                if (entrySize < 0)
                {
                    throw new InvalidDataException($"Invalid tar entry size: {entryName}");
                }

                if (!TryExtractRecordKey(entryName, out var guid, out var entryKey))
                {
                    ConsumeBytes(tarStream, entrySize, readBuffer);
                    ConsumePadding(tarStream, entrySize, readBuffer);
                    continue;
                }

                if (!records.TryGetValue(guid, out var record))
                {
                    record = new UnityPackageEntryRecord();
                    records[guid] = record;
                }

                if (string.Equals(entryKey, "pathname", StringComparison.Ordinal))
                {
                    record.Pathname = ReadPathnameEntry(tarStream, entrySize);
                }
                else if (string.Equals(entryKey, "asset", StringComparison.Ordinal))
                {
                    record.AssetHash = ComputeStreamHash(tarStream, entrySize, readBuffer);
                    record.AssetSize = entrySize;
                }
                else if (string.Equals(entryKey, "asset.meta", StringComparison.Ordinal))
                {
                    var metaBytes = ReadEntryBytes(tarStream, entrySize, MaxMetaBytes);
                    record.MetaHash = ComputeBytesHash(metaBytes);
                    record.MetaGuid = TryExtractMetaGuidFromBytes(metaBytes);
                }
                else
                {
                    ConsumeBytes(tarStream, entrySize, readBuffer);
                }

                ConsumePadding(tarStream, entrySize, readBuffer);
            }

            return records;
        }

        private static string ReadPathnameEntry(Stream stream, long entrySize)
        {
            if (entrySize < 0 || entrySize > MaxPathnameBytes)
            {
                throw new InvalidDataException($"Pathname entry size is out of range: {entrySize}");
            }

            if (entrySize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[(int)entrySize];
            var read = ReadExactly(stream, bytes, 0, bytes.Length);
            if (read != bytes.Length)
            {
                throw new EndOfStreamException("Unexpected end of pathname entry.");
            }

            var rawPath = Encoding.UTF8.GetString(bytes);
            var line = rawPath
                .Replace('\0', '\n')
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return line.Trim();
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

        private static string ComputeStreamHash(Stream stream, long byteCount, byte[] readBuffer)
        {
            if (byteCount < 0)
            {
                throw new InvalidDataException($"Invalid entry size: {byteCount}");
            }

            using var sha256 = SHA256.Create();
            var remaining = byteCount;
            while (remaining > 0)
            {
                var readSize = (int)Math.Min(readBuffer.Length, remaining);
                var read = stream.Read(readBuffer, 0, readSize);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected end while reading tar entry.");
                }

                sha256.TransformBlock(readBuffer, 0, read, null, 0);
                remaining -= read;
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BytesToLowerHex(sha256.Hash ?? Array.Empty<byte>());
        }

        private static byte[] ReadEntryBytes(Stream stream, long byteCount, long maxBytes)
        {
            if (byteCount < 0)
            {
                throw new InvalidDataException($"Invalid entry size: {byteCount}");
            }

            if (byteCount > maxBytes)
            {
                throw new InvalidDataException($"Entry size exceeds limit: {byteCount}");
            }

            var bytes = new byte[(int)byteCount];
            var read = ReadExactly(stream, bytes, 0, bytes.Length);
            if (read != bytes.Length)
            {
                throw new EndOfStreamException("Unexpected end while reading tar entry bytes.");
            }

            return bytes;
        }

        private static string ComputeBytesHash(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            return BytesToLowerHex(sha256.ComputeHash(bytes ?? Array.Empty<byte>()));
        }

        private static void ConsumeBytes(Stream stream, long byteCount, byte[] readBuffer)
        {
            if (byteCount < 0)
            {
                throw new InvalidDataException($"Invalid byte count: {byteCount}");
            }

            var remaining = byteCount;
            while (remaining > 0)
            {
                var readSize = (int)Math.Min(readBuffer.Length, remaining);
                var read = stream.Read(readBuffer, 0, readSize);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected end while consuming stream bytes.");
                }

                remaining -= read;
            }
        }

        private static void ConsumePadding(Stream stream, long entrySize, byte[] readBuffer)
        {
            var padding = (TarBlockSize - (entrySize % TarBlockSize)) % TarBlockSize;
            if (padding <= 0)
            {
                return;
            }

            ConsumeBytes(stream, padding, readBuffer);
        }

        private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }

        private static bool IsAllZeroBlock(byte[] block)
        {
            for (var i = 0; i < block.Length; i++)
            {
                if (block[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ReadNullTerminatedAscii(byte[] buffer, int offset, int count)
        {
            var end = offset;
            var max = offset + count;
            while (end < max && buffer[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
        }

        private static string TryExtractMetaGuidFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (TryExtractGuidLine(line, out var guid))
                {
                    return guid;
                }
            }

            return string.Empty;
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

        private static long ParseTarOctal(byte[] buffer, int offset, int count)
        {
            var raw = ReadNullTerminatedAscii(buffer, offset, count).Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return 0;
            }

            try
            {
                return Convert.ToInt64(raw, 8);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static bool TryExtractRecordKey(string tarEntryName, out string guid, out string entryKey)
        {
            guid = string.Empty;
            entryKey = string.Empty;
            if (string.IsNullOrWhiteSpace(tarEntryName))
            {
                return false;
            }

            var normalized = tarEntryName.Replace('\\', '/').Trim();
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            var firstSlash = normalized.IndexOf('/');
            if (firstSlash <= 0 || firstSlash >= normalized.Length - 1)
            {
                return false;
            }

            guid = normalized[..firstSlash];
            entryKey = normalized[(firstSlash + 1)..];

            if (entryKey.IndexOf('/') >= 0)
            {
                return false;
            }

            return string.Equals(entryKey, "pathname", StringComparison.Ordinal) ||
                   string.Equals(entryKey, "asset", StringComparison.Ordinal) ||
                   string.Equals(entryKey, "asset.meta", StringComparison.Ordinal);
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

        private sealed class UnityPackageEntryRecord
        {
            public string Pathname = string.Empty;
            public string AssetHash = string.Empty;
            public long AssetSize;
            public string MetaHash = string.Empty;
            public string MetaGuid = string.Empty;

            public bool HasAsset => !string.IsNullOrEmpty(AssetHash);
            public bool HasMeta => !string.IsNullOrEmpty(MetaHash);
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
