using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ModPackager
{
    /// <summary>
    /// Package entry types
    /// </summary>
    internal enum PackageEntryType
    {
        /// <summary>
        /// A standard file. No special treatment.
        /// </summary>
        File,

        /// <summary>
        /// Signals to the packager that multiple entries should be created.
        /// </summary>
        Directory
    }

    /// <summary>
    /// A package entry. Entries contain metadata that is
    /// used by the packager when generating a ZIP file.
    /// </summary>
    [JsonObject]
    internal class PackageEntry
    {
        /// <summary>
        /// The entry type.
        /// </summary>
        [JsonProperty("type", Required = Required.Always)]
        public PackageEntryType Type { get; set; }

        /// <summary>
        /// The local (source) path for the file(s).
        /// </summary>
        [JsonProperty("local_path", Required = Required.Always)]
        public string LocalPath { get; set; }

        /// <summary>
        /// The game (packaged) path for the file(s).
        /// </summary>
        [JsonProperty("game_path", Required = Required.Always)]
        public string GamePath { get; set; }
    }

    /// <summary>
    /// The configuration for a mod package.
    /// </summary>
    [JsonObject]
    internal class PackageConfig
    {
        /// <summary>
        /// Whether files should be encrypted or not.
        /// </summary>
        [JsonProperty(Required = Required.Always, PropertyName = "encrypt_files")]
        public bool EncryptFiles { get; set; }
        
        /// <summary>
        /// The package entries.
        /// </summary>
        [JsonProperty(Required = Required.Always, PropertyName = "entries")]
        public List<PackageEntry> Entries { get; set; }
    }

    /// <summary>
    /// A package entry in a build config.
    /// </summary>
    [JsonObject]
    internal class BuildConfigPackage
    {
        /// <summary>
        /// The name of the source directory.
        /// </summary>
        [JsonProperty(Required = Required.Always, PropertyName = "source_name")]
        public string SourceName { get; set; }
        
        /// <summary>
        /// The name of the final package file.
        /// </summary>
        [JsonProperty(Required = Required.Always, PropertyName = "distribution_name")]
        public string DistributionName { get; set; }
    }

    /// <summary>
    /// The configuration for the build process.
    /// </summary>
    [JsonObject]
    internal class BuildConfig
    {
        /// <summary>
        /// The packages to build.
        /// </summary>
        [JsonProperty(Required = Required.Always, PropertyName = "packages")]
        public List<BuildConfigPackage> Packages { get; set; }

        /// <summary>
        /// Whether or not an index file should be generated.
        /// </summary>
        [JsonProperty(Required = Required.Always, PropertyName = "generate_index")]
        public bool GenerateIndex { get; set; }
    }

    /// <summary>
    /// An entry in a build index.
    /// </summary>
    [JsonObject]
    internal class BuildIndexEntry
    {
        /// <summary>
        /// The full file name of the package to be downloaded.
        /// </summary>
        [JsonProperty]
        public string Name { get; set; }

        /// <summary>
        /// The SHA-1 checksum of the package.
        /// </summary>
        [JsonProperty]
        public string Checksum { get; set; }
    }

    /// <summary>
    /// The index information for a package build.
    /// </summary>
    [JsonObject]
    internal class BuildIndex
    {
        [JsonProperty(Required = Required.Always, PropertyName = "built_at")]
        public DateTime BuiltAt { get; set; }

        [JsonProperty(Required = Required.Always, PropertyName = "entries")]
        public List<BuildIndexEntry> Entries { get; set; }
    }
}