// <copyright file="PatternFinder.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using GameOffsets;

    /// <summary>
    ///     This class contains helper functions to find the
    ///     patterns (array of bytes in HEX) in the process memory.
    ///     To improve the perforamnce and memory footprint, it parallelizes
    ///     the search and ensures that the whole executable is not loaded in
    ///     the memory at once.
    ///     NOTE: According to microsoft docs (linked below) anything bigger
    ///     than 85,000 bytes will go into the large-object-heap and will
    ///     remain in the memory for a long time.
    ///     https://docs.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap.
    /// </summary>
    internal static class PatternFinder
    {
        /// <summary>
        ///     This is 1000 less than the maximum number of bytes that can be created
        ///     with non-large-object-heap. Benefit of doing that is to ensure
        ///     that GC cleans up the memory ASAP.
        ///     NOTE: 1000 less than the maximum number allows us to read a bit more than
        ///     this number when require.
        /// </summary>
        private const int MaxBytesObject = 84000;

        /// <summary>
        ///     Gets the HEX (byte array) patterns for finding static offsets in the Process.
        ///     All patterns are read from the GameOffsets library so that users just have to update
        ///     GameOffsets lib once there is a new patch.
        /// </summary>
        private static Pattern[] Patterns => StaticOffsetsPatterns.Patterns;

        /// <summary>
        ///     Tries to find all the patterns given in the GameOffsets StaticOffsetsPatterns class.
        /// </summary>
        /// <param name="handle">Handle to the process.</param>
        /// <param name="baseAddress">BaseAddress of the process main module.</param>
        /// <param name="processSize">Total Size of the process main module.</param>
        /// <returns> Static offsets name and location in the processs.</returns>
        internal static Dictionary<string, int> Find(
            SafeMemoryHandle handle,
            IntPtr baseAddress,
            int processSize
        )
        {
            // This allows the algorithm to read X bytes more than MaxBytesObject.
            // Algorithm does this to find patterns between the chunks.
            // e.g.      1 5 {4
            //           6 9 }2
            //           3 5 2
            //           each line shows a chunks
            //           {} shows the pattern we want to find.
            var patternMaxLength = PatternSearchEngine.BiggestPatternLength(Patterns);

            // Underlying library silently crashes if the algorithm reads more than the processSize.
            // So lets find the total number of reads required and modify the number of byes to read
            // in the last read operation.
            var totalReadOperations = CalculateTotalReadOperations(processSize);

            Dictionary<string, int> result = new(StringComparer.Ordinal);

            var matchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sync = new object();

            var pOptions = new ParallelOptions { MaxDegreeOfParallelism = 4 };
            Parallel.For(0, totalReadOperations, pOptions, i =>
            {
                var currentOffset = i * MaxBytesObject;
                var isLastIteration = i == totalReadOperations - 1;
                // F-037: chunks overlap by patternMaxLength so a pattern that straddles
                // the boundary between chunk N and chunk N+1 is matched in chunk N.
                // The last chunk reads to end-of-process; intermediate chunks read
                // MaxBytesObject + patternMaxLength but capped at end-of-process.
                var actualReadSize = isLastIteration
                    ? processSize - currentOffset
                    : Math.Min(MaxBytesObject + patternMaxLength, processSize - currentOffset);

                var processData = handle.ReadMemoryArray<byte>(baseAddress + currentOffset, actualReadSize);
                if (processData.Length == 0)
                {
                    return;
                }

                var ownedLength = isLastIteration
                    ? processData.Length
                    : Math.Min(MaxBytesObject, processData.Length);
                var matches = PatternSearchEngine.FindInBlock(processData, Patterns, currentOffset, ownedLength);
                if (matches.Count == 0)
                {
                    return;
                }

                lock (sync)
                {
                    foreach (var match in matches)
                    {
                        matchCounts.TryGetValue(match.PatternName, out var count);
                        matchCounts[match.PatternName] = count + 1;
                        result[match.PatternName] = match.ResolvedOffset;
                    }
                }
            });

            PatternSearchEngine.ValidateResults(Patterns, result, matchCounts);
            return result;
        }

        /// <summary>
        ///     Calculates the total number of read operations required for a given
        ///     process size based on the MaxBytesObject constant.
        /// </summary>
        /// <param name="processSize">Size of the process main module.</param>
        /// <returns>total number of read operations requried.</returns>
        private static int CalculateTotalReadOperations(int processSize)
        {
            var ret = processSize / MaxBytesObject;
            return processSize % MaxBytesObject == 0 ? ret : ret + 1;
        }
    }
}
