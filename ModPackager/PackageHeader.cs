using System.Runtime.InteropServices;

namespace ModPackager
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PackageHeader
    {
        public uint Magic;

        public bool EncryptionEnabled;

        public long CompilationTimestamp;

        public int KeyLength;
    }
}
