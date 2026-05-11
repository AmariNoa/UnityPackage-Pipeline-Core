using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public static partial class AmariUnityPackageContentReader
    {
        private const int TarBlockSize = 512;
        private const int ReadBufferSize = 8192;
        private const long MaxPathnameBytes = 16 * 1024;
        private const long MaxMetaBytes = 2 * 1024 * 1024;

        private static Dictionary<string, PackageEntryRecord> ReadEntryRecords(
            Stream tarStream,
            CancellationToken cancellationToken)
        {
            var records = new Dictionary<string, PackageEntryRecord>(StringComparer.Ordinal);
            var header = new byte[TarBlockSize];
            var readBuffer = new byte[ReadBufferSize];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var headerRead = ReadExactly(tarStream, header, 0, TarBlockSize, cancellationToken);
                if (headerRead == 0)
                {
                    break;
                }

                if (headerRead < TarBlockSize || IsAllZeroBlock(header))
                {
                    break;
                }

                var entryName = ReadNullTerminatedAscii(header, 0, 100);
                var entrySize = ParseTarOctal(header, 124, 12);
                if (entrySize < 0)
                {
                    break;
                }

                if (!TryExtractRecordKey(entryName, out var guid, out var entryKey))
                {
                    ConsumeBytes(tarStream, entrySize, readBuffer, cancellationToken);
                    ConsumePadding(tarStream, entrySize, readBuffer, cancellationToken);
                    continue;
                }

                if (!records.TryGetValue(guid, out var record))
                {
                    record = new PackageEntryRecord();
                    records[guid] = record;
                }

                switch (entryKey)
                {
                    case "pathname":
                        record.Pathname = ReadPathnameEntry(tarStream, entrySize, readBuffer, cancellationToken);
                        break;
                    case "asset":
                        record.HasAsset = true;
                        record.AssetSize = entrySize;
                        record.AssetSha256 = ConsumeBytesAndComputeSha256(tarStream, entrySize, readBuffer, cancellationToken);
                        break;
                    case "asset.meta":
                        record.HasMeta = true;
                        var metaBytes = ReadEntryBytes(tarStream, entrySize, MaxMetaBytes, cancellationToken);
                        record.MetaSha256 = ComputeBytesSha256(metaBytes);
                        record.MetaGuid = TryExtractMetaGuidFromBytes(metaBytes);
                        break;
                    default:
                        ConsumeBytes(tarStream, entrySize, readBuffer, cancellationToken);
                        break;
                }

                ConsumePadding(tarStream, entrySize, readBuffer, cancellationToken);
            }

            return records;
        }

        private static string ReadPathnameEntry(
            Stream stream,
            long entrySize,
            byte[] readBuffer,
            CancellationToken cancellationToken)
        {
            if (entrySize <= 0 || entrySize > MaxPathnameBytes)
            {
                ConsumeBytes(stream, entrySize, readBuffer, cancellationToken);
                return string.Empty;
            }

            var bytes = new byte[(int)entrySize];
            var read = ReadExactly(stream, bytes, 0, bytes.Length, cancellationToken);
            if (read != bytes.Length)
            {
                return string.Empty;
            }

            var rawPath = Encoding.UTF8.GetString(bytes);
            foreach (var line in rawPath
                         .Replace('\0', '\n')
                         .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        private static byte[] ReadEntryBytes(
            Stream stream,
            long entrySize,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            if (entrySize <= 0)
            {
                return Array.Empty<byte>();
            }

            if (entrySize > maxBytes)
            {
                throw new InvalidDataException($"Entry size exceeds limit: {entrySize}");
            }

            var bytes = new byte[(int)entrySize];
            var read = ReadExactly(stream, bytes, 0, bytes.Length, cancellationToken);
            if (read != bytes.Length)
            {
                throw new EndOfStreamException("Unexpected end while reading tar entry bytes.");
            }

            return bytes;
        }

        private static int ReadExactly(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }

        private static string ConsumeBytesAndComputeSha256(
            Stream stream,
            long byteCount,
            byte[] readBuffer,
            CancellationToken cancellationToken)
        {
            if (stream == null || readBuffer == null || readBuffer.Length == 0 || byteCount <= 0)
            {
                return string.Empty;
            }

            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var remaining = byteCount;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readSize = (int)Math.Min(readBuffer.Length, remaining);
                var read = stream.Read(readBuffer, 0, readSize);
                if (read <= 0)
                {
                    return string.Empty;
                }

                incrementalHash.AppendData(readBuffer, 0, read);
                remaining -= read;
            }

            return ToLowerHex(incrementalHash.GetHashAndReset());
        }

        private static string ComputeBytesSha256(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            return ToLowerHex(sha256.ComputeHash(bytes ?? Array.Empty<byte>()));
        }

        private static void ConsumeBytes(
            Stream stream,
            long byteCount,
            byte[] readBuffer,
            CancellationToken cancellationToken)
        {
            var remaining = byteCount;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readSize = (int)Math.Min(readBuffer.Length, remaining);
                var read = stream.Read(readBuffer, 0, readSize);
                if (read <= 0)
                {
                    break;
                }

                remaining -= read;
            }
        }

        private static void ConsumePadding(
            Stream stream,
            long entrySize,
            byte[] readBuffer,
            CancellationToken cancellationToken)
        {
            var padding = (TarBlockSize - (entrySize % TarBlockSize)) % TarBlockSize;
            if (padding <= 0)
            {
                return;
            }

            ConsumeBytes(stream, padding, readBuffer, cancellationToken);
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

        private static long ParseTarOctal(byte[] buffer, int offset, int count)
        {
            var value = 0L;
            var hasDigit = false;
            var end = offset + count;
            for (var i = offset; i < end; i++)
            {
                var c = buffer[i];
                if (c == 0 || c == 32)
                {
                    continue;
                }

                if (c < '0' || c > '7')
                {
                    return -1;
                }

                hasDigit = true;
                value = (value * 8) + (c - '0');
            }

            return hasDigit ? value : 0;
        }

        private static bool TryExtractRecordKey(string tarEntryName, out string guid, out string entryKey)
        {
            guid = string.Empty;
            entryKey = string.Empty;
            if (string.IsNullOrWhiteSpace(tarEntryName))
            {
                return false;
            }

            var normalized = tarEntryName.Replace('\\', '/').Trim('/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            var firstSlash = normalized.IndexOf('/');
            if (firstSlash <= 0 || firstSlash >= normalized.Length - 1)
            {
                return false;
            }

            var candidateGuid = normalized[..firstSlash];
            var candidateKey = normalized[(firstSlash + 1)..];
            if (candidateKey.IndexOf('/') >= 0)
            {
                return false;
            }

            if (!IsHex32(candidateGuid))
            {
                return false;
            }

            if (!string.Equals(candidateKey, "pathname", StringComparison.Ordinal) &&
                !string.Equals(candidateKey, "asset", StringComparison.Ordinal) &&
                !string.Equals(candidateKey, "asset.meta", StringComparison.Ordinal))
            {
                return false;
            }

            guid = candidateGuid.ToLowerInvariant();
            entryKey = candidateKey;
            return true;
        }

        private static bool IsHex32(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var isDigit = c >= '0' && c <= '9';
                var isLower = c >= 'a' && c <= 'f';
                var isUpper = c >= 'A' && c <= 'F';
                if (!isDigit && !isLower && !isUpper)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ToLowerHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static string TryExtractMetaGuidFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var text = Encoding.UTF8.GetString(bytes);
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed["guid:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private sealed class PackageEntryRecord
        {
            public string Pathname = string.Empty;
            public bool HasAsset;
            public long AssetSize;
            public string AssetSha256 = string.Empty;
            public bool HasMeta;
            public string MetaSha256 = string.Empty;
            public string MetaGuid = string.Empty;
        }
    }
}
