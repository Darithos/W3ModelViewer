using System.Runtime.InteropServices;

namespace Wc3ModelViewer.Core.Casc;

/// <summary>CASC_STORAGE_INFO_CLASS values CascLib answers for a local storage.</summary>
public enum CascInfo
{
    LocalFileCount = CascInterop.CascStorageLocalFileCount,
    TotalFileCount = CascInterop.CascStorageTotalFileCount,
    Features = CascInterop.CascStorageFeatures,
    InstalledLocales = CascInterop.CascStorageInstalledLocales,
    Product = CascInterop.CascStorageProduct,
}

/// <summary>
/// A locally-installed Warcraft III (Reforged) CASC storage.
/// Point it at the install folder that contains <c>.build.info</c> — e.g. <c>C:\games\Warcraft III</c>.
/// </summary>
/// <remarks>
/// Warcraft III's CASC exposes files through a virtual namespace built from the mod archives, which
/// CascView renders with colons: <c>war3.w3mod:_hd.w3mod:units\human\knight\knight.mdx</c>. The exact
/// spelling CascLib hands back is a property of the CascLib build and the game version, so this class
/// never hardcodes it: <see cref="BuildIndex"/> enumerates whatever names the storage actually reports
/// and <see cref="Wc3AssetIndex"/> normalises them afterwards.
/// </remarks>
public sealed class Wc3Storage : IDisposable
{
    private IntPtr _handle;

    public string InstallPath { get; }

    public Wc3Storage(string installPath)
    {
        InstallPath = installPath;

        // The bare path opens the storage's active product. An install that carries several products
        // (w3/w3t) needs the "path:product" form, so fall back to that before giving up.
        if (!CascInterop.CascOpenStorage(installPath, CascInterop.CASC_LOCALE_ALL, out _handle) || _handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (!CascInterop.CascOpenStorage(installPath + ":w3", CascInterop.CASC_LOCALE_ALL, out _handle) || _handle == IntPtr.Zero)
                throw new IOException($"Could not open the Warcraft III CASC storage at '{installPath}' " +
                                      $"(CascLib error {err}). Expected the folder that contains '.build.info'.");
        }
    }

    /// <summary>Reads a DWORD-valued storage info class, or 0 when CascLib will not say.</summary>
    public int GetInfoInt(CascInfo info)
    {
        IntPtr buf = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buf, 0);
            return CascInterop.CascGetStorageInfo(_handle, (int)info, buf, sizeof(int), out _)
                ? Marshal.ReadInt32(buf) : 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Reads a string-valued storage info class, or "" when CascLib will not say.</summary>
    public string GetInfoString(CascInfo info)
    {
        IntPtr buf = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteByte(buf, 0);
            return CascInterop.CascGetStorageInfo(_handle, (int)info, buf, 1024, out _)
                ? Marshal.PtrToStringAnsi(buf) ?? "" : "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Number of files the storage reports, or 0 when CascLib will not say.</summary>
    public int TotalFileCount => GetInfoInt(CascInfo.TotalFileCount);

    /// <summary>
    /// True when the storage holds a non-empty file under this exact name.
    /// </summary>
    /// <remarks>
    /// Warcraft III's storage answers <c>CascOpenFile</c> successfully for names it does not actually
    /// have, handing back a zero-length file instead of failing — so a bare open is not a membership
    /// test. Size is what distinguishes a real asset, and no real Warcraft III asset is zero bytes.
    /// </remarks>
    public bool Exists(string name)
    {
        if (!CascInterop.CascOpenFile(_handle, name, CascInterop.CASC_LOCALE_ALL, CascInterop.CASC_OPEN_BY_NAME, out IntPtr hFile))
            return false;
        try
        {
            uint size = CascInterop.CascGetFileSize(hFile, out _);
            return size is not (0 or uint.MaxValue);
        }
        finally { CascInterop.CascCloseFile(hFile); }
    }

    /// <summary>Reads a file by its internal CASC name. Throws when the name is not in the storage.</summary>
    public byte[] ReadFile(string name)
        => TryReadFile(name) ?? throw new FileNotFoundException($"Not present in the Warcraft III CASC storage: {name}", name);

    /// <summary>
    /// Reads a file by its internal CASC name, or null when the storage has no such file.
    /// A name the storage answers with zero bytes counts as absent — see <see cref="Exists"/>.
    /// </summary>
    public byte[]? TryReadFile(string name)
    {
        if (!CascInterop.CascOpenFile(_handle, name, CascInterop.CASC_LOCALE_ALL, CascInterop.CASC_OPEN_BY_NAME, out IntPtr hFile))
            return null;
        try
        {
            uint size = CascInterop.CascGetFileSize(hFile, out _);
            if (size is 0 or uint.MaxValue) return null;

            var buffer = new byte[size];
            uint offset = 0;
            while (offset < size)
            {
                // Read straight into the tail of the destination so no second copy is needed.
                var chunk = new byte[size - offset];
                if (!CascInterop.CascReadFile(hFile, chunk, size - offset, out uint read) || read == 0) break;
                Buffer.BlockCopy(chunk, 0, buffer, (int)offset, (int)read);
                offset += read;
            }
            return buffer;
        }
        finally
        {
            CascInterop.CascCloseFile(hFile);
        }
    }

    /// <summary>
    /// Walks the storage and returns the internal names CascLib reports. This is the only way to
    /// discover Warcraft III's virtual paths — there is no CoreTOC-style catalog as in Diablo III.
    /// </summary>
    /// <remarks>
    /// The walk yields every name the storage reports in about 30 ms but then never ends:
    /// <c>CascFindNextFile</c> keeps succeeding past the last real entry. So the loop stops once it
    /// has collected <see cref="TotalFileCount"/> names, and cancelling returns what was gathered so
    /// far rather than throwing.
    /// </remarks>
    /// <param name="progress">Called with the running name count, for UI feedback.</param>
    public List<string> EnumerateAll(IProgress<int>? progress = null, CancellationToken cancel = default)
    {
        int expected = TotalFileCount;
        var results = new List<string>(expected > 0 ? expected : 1 << 17);

        // CASC_FIND_DATA starts with char szFileName[MAX_PATH]; only that leading field is read here,
        // which keeps this independent of the rest of the struct's layout across CascLib versions.
        IntPtr findData = Marshal.AllocHGlobal(4096);
        IntPtr hFind = IntPtr.Zero;
        try
        {
            Marshal.WriteByte(findData, 0);
            hFind = CascInterop.CascFindFirstFile(_handle, "*", findData, null);
            if (hFind == IntPtr.Zero || hFind == new IntPtr(-1)) return results;

            bool ok = true;
            while (ok && !cancel.IsCancellationRequested)
            {
                string? name = Marshal.PtrToStringAnsi(findData);
                if (!string.IsNullOrEmpty(name))
                {
                    results.Add(name);
                    if (results.Count % 25_000 == 0) progress?.Report(results.Count);
                    if (expected > 0 && results.Count >= expected) break;
                }
                ok = CascInterop.CascFindNextFile(hFind, findData);
            }
        }
        finally
        {
            if (hFind != IntPtr.Zero && hFind != new IntPtr(-1)) CascInterop.CascFindClose(hFind);
            Marshal.FreeHGlobal(findData);
        }
        progress?.Report(results.Count);
        return results;
    }

    /// <summary>Enumerates the storage and sorts the names into the browsable asset index.</summary>
    public Wc3AssetIndex BuildIndex(IProgress<int>? progress = null, CancellationToken cancel = default)
        => Wc3AssetIndex.FromNames(EnumerateAll(progress, cancel));

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CascInterop.CascCloseStorage(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
