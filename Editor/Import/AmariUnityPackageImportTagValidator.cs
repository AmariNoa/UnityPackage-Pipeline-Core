using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportTagValidator
    {
        private static readonly Regex AllowedTagRegex = new(
            "^[A-Za-z0-9_\\-./]+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public bool TryNormalizeAndValidate(IEnumerable<string> tags, out string[] normalizedTags, out string error)
        {
            normalizedTags = Array.Empty<string>();
            error = string.Empty;

            if (tags == null)
            {
                return true;
            }

            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var orderedTags = new List<string>();

            foreach (var rawTag in tags)
            {
                if (string.IsNullOrWhiteSpace(rawTag))
                {
                    continue;
                }

                var trimmed = rawTag.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.Length > AmariUnityPackagePipelineLabels.MaxTagLength)
                {
                    error = $"Tag exceeds max length ({AmariUnityPackagePipelineLabels.MaxTagLength}): {trimmed}";
                    return false;
                }

                if (!AllowedTagRegex.IsMatch(trimmed))
                {
                    error = $"Tag contains unsupported characters: {trimmed}";
                    return false;
                }

                if (distinct.Add(trimmed))
                {
                    orderedTags.Add(trimmed);
                }
            }

            normalizedTags = orderedTags.ToArray();
            return true;
        }
    }
}
