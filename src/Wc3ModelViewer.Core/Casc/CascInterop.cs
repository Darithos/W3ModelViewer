using System.Runtime.InteropServices;

namespace Wc3ModelViewer.Core.Casc;

/// <summary>Raw P/Invoke surface for CascLib.dll (Zezula), built x64 / MultiByte(ANSI) / WINAPI.</summary>
internal static class CascInterop
{
    public const uint CASC_LOCALE_ALL = 0xFFFFFFFF;
    public const uint CASC_OPEN_BY_NAME = 0x00000000;

    // CASC_STORAGE_INFO_CLASS, in CascLib.h declaration order.
    public const int CascStorageLocalFileCount = 0;
    public const int CascStorageTotalFileCount = 1;
    public const int CascStorageFeatures = 2;
    public const int CascStorageInstalledLocales = 3;
    public const int CascStorageProduct = 4;

    private const string DLL = "CascLib.dll";

    [DllImport(DLL, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool CascOpenStorage(string szParams, uint dwLocaleMask, out IntPtr phStorage);

    [DllImport(DLL, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool CascOpenFile(IntPtr hStorage, string pvFileName, uint dwLocaleFlags, uint dwOpenFlags, out IntPtr phFile);

    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern uint CascGetFileSize(IntPtr hFile, out uint pdwFileSizeHigh);

    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool CascReadFile(IntPtr hFile, byte[] lpBuffer, uint dwToRead, out uint pdwRead);

    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool CascCloseFile(IntPtr hFile);

    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool CascCloseStorage(IntPtr hStorage);

    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool CascGetStorageInfo(IntPtr hStorage, int InfoClass, IntPtr pvInfo, nint cbInfo, out nint pcbLengthNeeded);

    [DllImport(DLL, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern IntPtr CascFindFirstFile(IntPtr hStorage, string szMask, IntPtr pFindData, string? szListFile);

    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool CascFindNextFile(IntPtr hFind, IntPtr pFindData);

    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool CascFindClose(IntPtr hFind);
}
