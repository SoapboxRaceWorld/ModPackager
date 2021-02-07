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

            var configDir = Path.GetDirectoryName(configPath) ?? "";
            var buildConfig = Serialization.Deserialize<BuildConfig>(File.ReadAllText(configPath));
            var pkgCache = new Dictionary<string, string>();
            var cachePath = Path.Combine(configDir, ".pkg-cache.json");

            if (File.Exists(cachePath))
            {
                pkgCache = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(cachePath));
            }

            foreach (var buildConfigPackage in buildConfig.Packages)
            {
                var packageBasePath = GetPackageBasePath(args, buildConfigPackage);
                Console.WriteLine("Checking package '{0}' to see if we need to update it...", buildConfigPackage.SourceName);
                var packageDataChecksum = DirectoryContentsChecksum(packageBasePath);
                if (!pkgCache.TryGetValue(buildConfigPackage.SourceName, out var currentHash) ||
                    !string.Equals(currentHash, packageDataChecksum))
                {
                    pkgCache[buildConfigPackage.SourceName] = packageDataChecksum;
                    if (currentHash == null)
                    {
                        Console.WriteLine("New package '{0}' added to cache", buildConfigPackage.SourceName);
                    }
                    else
                    {
                        Console.WriteLine("Package '{0}' changed: old SHA1 checksum was {1}, now it is {2}", buildConfigPackage.SourceName, currentHash, packageDataChecksum);
                    }

                    BuildPackage(args, buildConfigPackage);
                }
                else
                {
                    Console.WriteLine("Package '{0}' was not changed, so we're not recompiling it", buildConfigPackage.SourceName);
                }
            }

            if (buildConfig.GenerateIndex)
            {
                var buildIndex = new BuildIndex();
                buildIndex.BuiltAt = DateTime.Now;
                buildIndex.Entries = new List<BuildIndexEntry>();

                foreach (var buildConfigPackage in buildConfig.Packages)
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

        private static void BuildPackage(ProgramArgs args, BuildConfigPackage buildConfigPackage)
        {
            var packageBasePath = GetPackageBasePath(args, buildConfigPackage);
            var packageConfigPath = Path.Combine(packageBasePath, "config.json");

            if (!File.Exists(packageConfigPath))
            {
                throw new Exception($"Could not find config file for package {buildConfigPackage.SourceName} ({buildConfigPackage.DistributionName}): looking for {packageConfigPath}");
            }

            var packageConfig = Serialization.Deserialize<PackageConfig>(File.ReadAllText(packageConfigPath));
            var masterKey = new byte[0];
            Console.WriteLine($"Building: {buildConfigPackage.SourceName} ({buildConfigPackage.DistributionName})");


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
                    fs.WriteByte((byte)(masterKey[i] ^ xorTable[i % xorTable.Length]));
                }
            }

            using (var zipFile = new ZipFile())
            {
                if (packageConfig.EncryptFiles)
                {
                    zipFile.Encryption = EncryptionAlgorithm.WinZipAes256;
                    zipFile.Password = Encoding.ASCII.GetString(masterKey);
                }

                foreach (var packageConfigEntry in packageConfig.Entries)
                {
                    ProcessPackageEntry(args, packageConfigEntry, buildConfigPackage, zipFile);
                }

                Console.WriteLine($"Saving: {buildConfigPackage.SourceName} ({buildConfigPackage.DistributionName})");

                zipFile.Save(fs);
            }
        }

        private static string GetPackageBasePath(ProgramArgs args, BuildConfigPackage buildConfigPackage)
        {
            return Path.Combine(Path.GetDirectoryName(args.BuildConfigPath) ?? "", "src", buildConfigPackage.SourceName);
        }

        private static void ProcessPackageEntry(ProgramArgs args, PackageEntry packageConfigEntry, BuildConfigPackage buildConfigPackage,
            ZipFile zipFile)
        {
            switch (packageConfigEntry.Type)
            {
                // check entry type. if we're working with a file, just write the data - otherwise, create a hierarchy
                case PackageEntryType.File:
                    ProcessFileEntry(args, packageConfigEntry, buildConfigPackage, zipFile);
                    break;
                case PackageEntryType.Directory:
                    ProcessDirectoryEntry(args, packageConfigEntry, buildConfigPackage, zipFile);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void ProcessFileEntry(ProgramArgs args, PackageEntry packageConfigEntry, BuildConfigPackage buildConfigPackage,
            ZipFile zipFile)
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

            var packageBasePath = Path.Combine(Path.GetDirectoryName(args.BuildConfigPath) ?? "", "src", buildConfigPackage.SourceName);
            var path = Path.Combine(packageBasePath, packageConfigEntry.LocalPath);

            if (!File.Exists(path))
            {
                throw new Exception($"Package {buildConfigPackage.SourceName}: Directory {path} does not exist");
            }

            var data = File.ReadAllBytes(path);

            zipFile.AddEntry(packageConfigEntry.GamePath, data);
        }

        private static void ProcessDirectoryEntry(ProgramArgs args, PackageEntry packageConfigEntry, BuildConfigPackage buildConfigPackage,
            ZipFile zipFile)
        {
            var packageBasePath = Path.Combine(Path.GetDirectoryName(args.BuildConfigPath) ?? "", "src", buildConfigPackage.SourceName);
            var directoryPath = Path.Combine(packageBasePath, packageConfigEntry.LocalPath);

            if (!Directory.Exists(directoryPath))
            {
                throw new Exception($"Package {buildConfigPackage.SourceName}: Directory {directoryPath} does not exist");
            }

            Console.WriteLine($"Read files from {directoryPath}...");

            zipFile.AddDirectory(directoryPath, packageConfigEntry.GamePath);
        }

        private static string DirectoryContentsChecksum(string directory)
        {
            // assuming you want to include nested folders
            var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
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
