using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CommandLine;
using Ionic.Zip;
using Newtonsoft.Json;

namespace ModPackager
{
    internal class ProgramArgs
    {
        [Option('i', "input", Required = true, HelpText = "Path to the build config")]
        public string BuildConfigPath { get; set; }

        [Option('o', "out", Required = true, HelpText = "Output path for the packages")]
        public string OutPath { get; set; }
    }

    internal static class Program
    {
        // key length in BYTES, not BITS
        private const int KeyLength = 32;

        private static void Main(string[] args)
        {
            Parser.Default.ParseArguments<ProgramArgs>(args).WithParsed(RunApplication);
        }

        private static void RunApplication(ProgramArgs args)
        {
            // Simple convention for files:
            //    - Source data is stored in <build path>/src/<package name>
            //    - Output data will always be in the same folder

            var configPath = args.BuildConfigPath;

            if (!File.Exists(configPath))
            {
                throw new Exception($"Could not find build config file. Looking for: {configPath}");
            }

            if (!Directory.Exists(args.OutPath))
            {
                Directory.CreateDirectory(args.OutPath);
            }

            var configDir = Path.GetDirectoryName(configPath) ?? "";
            var buildConfig = Serialization.Deserialize<BuildConfig>(File.ReadAllText(configPath));
            var pkgCache = new Dictionary<string, string>();
            var cachePath = Path.Combine(configDir, ".pkg-cache.json");

            if (File.Exists(cachePath))
            {
                pkgCache = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(cachePath));
            }

            var buildPackageList = new List<BuildConfigPackage>();

            foreach (var buildConfigPackage in buildConfig.Packages)
            {
                var packageBasePath = GetPackageBasePath(args, buildConfigPackage);
                var packageConfigPath = Path.Combine(packageBasePath, "config.json");
                if (!File.Exists(packageConfigPath))
                {
                    Console.WriteLine("ERROR: Could not find config for package '{0}'. Looked for: {1}",
                        buildConfigPackage.SourceName, packageConfigPath);
                    return;
                }

                var packageConfig =
                    Serialization.Deserialize<PackageConfig>(
                        File.ReadAllText(packageConfigPath));
                if (packageConfig.AutoSplitMode == PackageAutoSplitMode.None)
                {
                    Console.WriteLine("Compiling '{0}' in single-file mode...", buildConfigPackage.SourceName);
                    buildConfigPackage.Package = packageConfig;
                    buildPackageList.Add(buildConfigPackage);
                }
                else
                {
                    Console.WriteLine("Compiling '{0}' in auto-split mode...", buildConfigPackage.SourceName);
                    buildPackageList.AddRange(AutoGenerateSplitPackages(packageBasePath, packageConfig));
                }
            }

            Console.WriteLine("Packages to check ({0}):", buildPackageList.Count);
            for (var index = 0; index < buildPackageList.Count; index++)
            {
                var package = buildPackageList[index];
                Console.WriteLine("Package {0}: {1} / {2} ({3} entries)", index + 1, package.SourceName,
                    package.DistributionName, package.Package.Entries.Count);
            }

            foreach (var packageToBuild in buildPackageList)
            {
                CheckAndCompilePackage(args, packageToBuild, GetPackageBasePath(args, packageToBuild), pkgCache,
                    packageToBuild.Package);
            }

            if (buildConfig.GenerateIndex)
            {
                var buildIndex = new BuildIndex();
                buildIndex.BuiltAt = DateTime.Now;
                buildIndex.Entries = new List<BuildIndexEntry>();

                foreach (var buildConfigPackage in buildPackageList)
                {
                    var distributionFileName = buildConfigPackage.DistributionName + ".mods";
                    var outPath = Path.Combine(args.OutPath, distributionFileName);
                    var checksum = CryptUtil.SHA1File(outPath);
                    var size = new FileInfo(outPath).Length;

                    buildIndex.Entries.Add(new BuildIndexEntry
                    {
                        Checksum = checksum,
                        Name = distributionFileName,
                        Size = size
                    });
                }

                File.WriteAllText(Path.Combine(args.OutPath, "index.json"), Serialization.Serialize(buildIndex));
            }

            File.WriteAllText(cachePath, Serialization.Serialize(pkgCache));
        }

        private static List<BuildConfigPackage> AutoGenerateSplitPackages(string basePath, PackageConfig packageConfig)
        {
            if (packageConfig.Entries.Any(e => !string.IsNullOrEmpty(Path.GetDirectoryName(e.GamePath))))
            {
                throw new Exception("Auto-split failed due to non-root entries in package configuration.");
            }

            var folderEntries = packageConfig.Entries.FindAll(e => e.Type == PackageEntryType.Directory);
            var fileEntries = packageConfig.Entries.FindAll(e => e.Type == PackageEntryType.File);
            var finalList = new List<BuildConfigPackage>();

            // Compile derived packages from directories
            foreach (var folderEntry in folderEntries)
            {
                if (packageConfig.AutoSplitMode == PackageAutoSplitMode.Aggressive)
                {
                    finalList.AddRange(Directory
                        .GetDirectories(Path.Combine(basePath, folderEntry.LocalPath), "*", SearchOption.AllDirectories)
                        .Select(p =>
                        {
                            var relPath = Path.GetRelativePath(basePath, p);
                            return new BuildConfigPackage
                            {
                                SourceName = Path.Combine(Path.GetFileName(basePath)!, relPath),
                                DistributionName = relPath.Replace(Path.DirectorySeparatorChar, '_').Replace('.', '_'),
                                Package = new PackageConfig
                                {
                                    EncryptFiles = packageConfig.EncryptFiles,
                                    Entries = new List<PackageEntry>
                                    {
                                        new PackageEntry
                                        {
                                            LocalPath = "",
                                            GamePath = relPath,
                                            Type = PackageEntryType.Directory
                                        }
                                    }
                                }
                            };
                        }));
                    var rootFiles = Directory.GetFiles(Path.Combine(basePath, folderEntry.LocalPath));
                    if (rootFiles.Length > 0)
                    {
                        finalList.Add(new BuildConfigPackage
                        {
                            SourceName = Path.Combine(Path.GetFileName(basePath)!, folderEntry.GamePath),
                            DistributionName = folderEntry.GamePath,
                            Package =
                                new PackageConfig
                                {
                                    Entries = rootFiles.Select(f => new PackageEntry
                                    {
                                        LocalPath = Path.GetFileName(f),
                                        GamePath = Path.GetRelativePath(basePath, f),
                                        Type = PackageEntryType.File
                                    }).ToList(),
                                    AutoSplitMode = PackageAutoSplitMode.None,
                                    EncryptFiles = packageConfig.EncryptFiles
                                }
                        });
                    }
                }
                else
                {
                    var tmpBuildPkg = new BuildConfigPackage
                    {
                        SourceName = folderEntry.GamePath,
                        DistributionName = folderEntry.GamePath,
                        Package =
                            new PackageConfig
                            {
                                Entries = new List<PackageEntry>
                                {
                                    new PackageEntry
                                    {
                                        LocalPath = "", GamePath = folderEntry.GamePath,
                                        Type = PackageEntryType.Directory
                                    }
                                },
                                AutoSplitMode = PackageAutoSplitMode.None,
                                EncryptFiles = packageConfig.EncryptFiles
                            }
                    };

                    finalList.Add(tmpBuildPkg);
                }
            }

            if (fileEntries.Count > 0)
            {
                var tmpRootPackage = new BuildConfigPackage
                {
                    SourceName = "root",
                    DistributionName = "root",
                    Package = new PackageConfig
                    {
                        Entries = fileEntries,
                        AutoSplitMode = PackageAutoSplitMode.None,
                        EncryptFiles = packageConfig.EncryptFiles
                    }
                };
                finalList.Add(tmpRootPackage);
            }

            return finalList;
        }

        private static void CheckAndCompilePackage(ProgramArgs args, BuildConfigPackage buildConfigPackage,
            string packageBasePath, Dictionary<string, string> pkgCache, PackageConfig packageConfig)
        {
            var packageDataChecksum = DirectoryContentsChecksum(packageBasePath,
                packageConfig.Entries.Any(e => e.Type == PackageEntryType.Directory));
            if (pkgCache.TryGetValue(buildConfigPackage.SourceName, out var currentHash) &&
                string.Equals(currentHash, packageDataChecksum)) return;
            // if (currentHash == null)
            // {
            //     Console.WriteLine("New package '{0}' added to cache", buildConfigPackage.SourceName);
            // }
            // else
            // {
            //     Console.WriteLine("Package '{0}' changed: old SHA1 checksum was {1}, now it is {2}",
            //         buildConfigPackage.SourceName, currentHash, packageDataChecksum);
            // }
            Console.WriteLine("Building package '{0}'", buildConfigPackage.SourceName);
            pkgCache[buildConfigPackage.SourceName] = packageDataChecksum;
            BuildPackage(args, buildConfigPackage, packageConfig, packageBasePath);
        }

        private static void BuildPackage(ProgramArgs args, BuildConfigPackage buildConfigPackage,
            PackageConfig packageConfig, string packageBasePath)
        {
            var masterKey = new byte[0];
            // Console.WriteLine($"Building: {buildConfigPackage.SourceName} ({buildConfigPackage.DistributionName})");

            var outPath = Path.Combine(args.OutPath, buildConfigPackage.DistributionName + ".mods");
            using var fs = File.Open(outPath, FileMode.Create, FileAccess.Write);
            var packageHeader = new PackageHeader
            {
                Magic = 0x4459495A,
                CompilationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                EncryptionEnabled = packageConfig.EncryptFiles
            };

            if (packageConfig.EncryptFiles)
            {
                packageHeader.KeyLength = KeyLength;
                masterKey = CryptUtil.GenerateKey(KeyLength);
            }

            BinaryUtils.MarshalStruct(fs, packageHeader);

            if (packageConfig.EncryptFiles)
            {
                byte[] xorTable =
                {
                    0x94, 0xce, 0xc3, 0xae, 0x73, 0xf9, 0xf1, 0xb9
                };

                for (var i = 0; i < masterKey.Length; i++)
                {
                    fs.WriteByte((byte) (masterKey[i] ^ xorTable[i % xorTable.Length]));
                }
            }

            using var zipFile = new ZipFile();
            if (packageConfig.EncryptFiles)
            {
                zipFile.Encryption = EncryptionAlgorithm.WinZipAes256;
                zipFile.Password = Encoding.ASCII.GetString(masterKey);
            }

            foreach (var packageConfigEntry in packageConfig.Entries)
            {
                ProcessPackageEntry(packageConfigEntry, buildConfigPackage, zipFile, packageBasePath);
            }

            // Console.WriteLine($"Saving: {buildConfigPackage.SourceName} ({buildConfigPackage.DistributionName})");

            zipFile.Save(fs);
        }

        private static string GetPackageBasePath(ProgramArgs args, BuildConfigPackage buildConfigPackage)
        {
            return Path.Combine(Path.GetDirectoryName(args.BuildConfigPath) ?? "", "src",
                buildConfigPackage.SourceName);
        }

        private static void ProcessPackageEntry(PackageEntry packageConfigEntry, BuildConfigPackage buildConfigPackage,
            ZipFile zipFile, string packageBasePath)
        {
            switch (packageConfigEntry.Type)
            {
                // check entry type. if we're working with a file, just write the data - otherwise, create a hierarchy
                case PackageEntryType.File:
                    ProcessFileEntry(packageConfigEntry, buildConfigPackage, zipFile, packageBasePath);
                    break;
                case PackageEntryType.Directory:
                    ProcessDirectoryEntry(packageConfigEntry, buildConfigPackage, zipFile, packageBasePath);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void ProcessFileEntry(PackageEntry packageConfigEntry,
            BuildConfigPackage buildConfigPackage,
            ZipFile zipFile, string packageBasePath)
        {
            var dirName = Path.GetDirectoryName(packageConfigEntry.GamePath);

            if (!string.IsNullOrWhiteSpace(dirName))
            {
                var newDirName = dirName;
                if (!newDirName.EndsWith("/"))
                    newDirName += "/";

                if (zipFile[newDirName] == null)
                {
                    zipFile.AddDirectoryByName(newDirName);
                }
            }

            var path = Path.Combine(packageBasePath, packageConfigEntry.LocalPath);

            if (!File.Exists(path))
            {
                throw new Exception($"Package {buildConfigPackage.SourceName}: Directory {path} does not exist");
            }

            var data = File.ReadAllBytes(path);

            zipFile.AddEntry(packageConfigEntry.GamePath, data);
        }

        private static void ProcessDirectoryEntry(PackageEntry packageConfigEntry,
            BuildConfigPackage buildConfigPackage,
            ZipFile zipFile, string packageBasePath)
        {
            var directoryPath = Path.Combine(packageBasePath, packageConfigEntry.LocalPath);

            if (!Directory.Exists(directoryPath))
            {
                throw new Exception(
                    $"Package {buildConfigPackage.SourceName}: Directory {directoryPath} does not exist");
            }

            // Console.WriteLine($"Read files from {directoryPath}...");

            zipFile.AddDirectory(directoryPath, packageConfigEntry.GamePath);
        }

        private static string DirectoryContentsChecksum(string directory, bool recurse = true)
        {
            // assuming you want to include nested folders
            var files = Directory.GetFiles(directory, "*.*",
                    recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .OrderBy(p => p).ToList();

            if (files.Count <= 0) return string.Empty;
            var md5 = MD5.Create();

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];

                // hash path
                var relativePath = file[(directory.Length + 1)..];
                var pathBytes = Encoding.UTF8.GetBytes(relativePath.ToLower());
                md5.TransformBlock(pathBytes, 0, pathBytes.Length, pathBytes, 0);

                // hash contents
                var contentBytes = File.ReadAllBytes(file);
                if (i == files.Count - 1)
                    md5.TransformFinalBlock(contentBytes, 0, contentBytes.Length);
                else
                    md5.TransformBlock(contentBytes, 0, contentBytes.Length, contentBytes, 0);
            }

            return BitConverter.ToString(md5.Hash).Replace("-", "").ToLower();
        }
    }
}