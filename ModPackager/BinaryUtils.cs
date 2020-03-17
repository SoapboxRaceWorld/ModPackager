using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ModPackager
{
    public static class BinaryUtils
    {
        private static byte[] GetBytes<T>(T obj) where T : struct
        {
            int size = Marshal.SizeOf(obj);
            byte[] arr = new byte[size];

            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(obj, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }

        public static void MarshalStruct<T>(BinaryWriter bw, T obj) where T : struct
        {
            bw.Write(GetBytes(obj));
        }

        public static void MarshalStruct<T>(Stream stream, T obj) where T : struct
        {
            stream.Write(GetBytes(obj));
        }
    }
}
