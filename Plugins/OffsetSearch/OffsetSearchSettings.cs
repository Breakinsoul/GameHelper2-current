// <copyright file="OffsetSearchSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace OffsetSearch
{
    using GameHelper.Plugin;

    /// <summary>
    ///     Settings for the offset-search plugin.
    /// </summary>
    public sealed class OffsetSearchSettings : IPSettings
    {
        /// <summary>
        ///     Path to a PathOfExile executable or binary dump.
        /// </summary>
        public string TargetFilePath = string.Empty;

        /// <summary>
        ///     Automatically run search when plugin is enabled and TargetFilePath exists.
        /// </summary>
        public bool AutoSearchOnEnable = false;

        /// <summary>
        ///     Load previously saved search results from the GameHelper folder on plugin enable.
        /// </summary>
        public bool LoadCachedResultsOnEnable = true;
    }
}
