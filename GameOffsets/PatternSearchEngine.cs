namespace GameOffsets
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    ///     Shared pattern scanner for live memory chunks and offline executable dumps.
    /// </summary>
    public static class PatternSearchEngine
    {
        private const int FileChunkSize = 1024 * 1024;

        public readonly record struct Match(string PatternName, int Offset, int ResolvedOffset);

        /// <summary>
        ///     Finds pattern matches in a memory block.
        /// </summary>
        /// <param name="data">Data to scan.</param>
        /// <param name="patterns">Patterns to find.</param>
        /// <param name="baseOffset">Offset added to returned match positions.</param>
        /// <param name="scanLength">
        ///     Number of bytes that are allowed to own a match. This is useful for overlapped chunks:
        ///     scan the full buffer, but only count matches that start before the non-overlap boundary.
        /// </param>
        public static List<Match> FindInBlock(
            ReadOnlySpan<byte> data,
            IReadOnlyList<Pattern> patterns,
            int baseOffset = 0,
            int? scanLength = null)
        {
            var ownedLength = Math.Min(scanLength ?? data.Length, data.Length);
            var matches = new List<Match>();
            for (var offset = 0; offset < ownedLength; offset++)
            {
                for (var patternIndex = 0; patternIndex < patterns.Count; patternIndex++)
                {
                    var pattern = patterns[patternIndex];
                    if (IsMatchAt(data, offset, pattern))
                    {
                        var absoluteOffset = baseOffset + offset;
                        matches.Add(new Match(
                            pattern.Name,
                            absoluteOffset,
                            absoluteOffset + pattern.BytesToSkip));
                    }
                }
            }

            return matches;
        }

        /// <summary>
        ///     Finds static-offset patterns in an executable or binary dump.
        ///     This is intended for the Ghidra update workflow where the executable is already available on disk.
        /// </summary>
        public static Dictionary<string, int> FindStaticOffsetsInFile(
            string filePath,
            IReadOnlyList<Pattern>? patterns = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path is required.", nameof(filePath));
            }

            patterns ??= StaticOffsetsPatterns.Patterns;
            var patternMaxLength = BiggestPatternLength(patterns);
            var results = new Dictionary<string, int>(StringComparer.Ordinal);
            var matchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var carry = Array.Empty<byte>();
            var fileOffset = 0;

            using var stream = File.OpenRead(filePath);
            var readBuffer = new byte[FileChunkSize];
            while (true)
            {
                var bytesRead = stream.Read(readBuffer, 0, readBuffer.Length);
                if (bytesRead <= 0)
                {
                    break;
                }

                var block = new byte[carry.Length + bytesRead];
                carry.CopyTo(block, 0);
                Array.Copy(readBuffer, 0, block, carry.Length, bytesRead);

                var ownedLength = carry.Length + bytesRead;
                if (stream.Position < stream.Length)
                {
                    ownedLength = Math.Max(carry.Length, block.Length - patternMaxLength);
                }

                foreach (var match in FindInBlock(block, patterns, fileOffset - carry.Length, ownedLength))
                {
                    matchCounts.TryGetValue(match.PatternName, out var count);
                    matchCounts[match.PatternName] = count + 1;
                    results[match.PatternName] = match.ResolvedOffset;
                }

                var nextCarryLength = Math.Min(patternMaxLength, block.Length);
                carry = new byte[nextCarryLength];
                Array.Copy(block, block.Length - nextCarryLength, carry, 0, nextCarryLength);
                fileOffset += bytesRead;
            }

            ValidateResults(patterns, results, matchCounts);
            return results;
        }

        public static void ValidateResults(
            IReadOnlyList<Pattern> patterns,
            IReadOnlyDictionary<string, int> results,
            IReadOnlyDictionary<string, int> matchCounts)
        {
            var missing = new List<string>();
            var duplicates = new List<string>();
            foreach (var pattern in patterns)
            {
                if (!results.ContainsKey(pattern.Name))
                {
                    missing.Add(pattern.Name);
                    continue;
                }

                if (matchCounts.TryGetValue(pattern.Name, out var count) && count > 1)
                {
                    duplicates.Add($"{pattern.Name} ({count})");
                }
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Couldn't find some patterns: {string.Join(", ", missing)}.");
            }

            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Found non-unique patterns: {string.Join(", ", duplicates)}.");
            }
        }

        public static int BiggestPatternLength(IReadOnlyList<Pattern> patterns)
        {
            var maxLength = 0;
            foreach (var pattern in patterns)
            {
                maxLength = Math.Max(maxLength, pattern.Data.Length);
            }

            return maxLength;
        }

        private static bool IsMatchAt(ReadOnlySpan<byte> data, int offset, Pattern pattern)
        {
            var patternLength = pattern.Data.Length;
            if (data.Length - offset < patternLength)
            {
                return false;
            }

            for (var i = 0; i < patternLength; i++)
            {
                if (pattern.Mask[i] && data[offset + i] != pattern.Data[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
