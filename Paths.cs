// Path identity on Windows: a mapped drive and its UNC path, a subst drive, a junction or an 8.3 short name all
// name the same folder while comparing as different strings. The engine must never confuse an output with an input.
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Unsquish;

public static class Paths
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInfo info);

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleInfo
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, LastAccess, LastWrite;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    const uint ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;

    static SafeFileHandle Open(string path) => CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);

    /// <summary>The final path of an existing file or folder as the file system knows it (drive letters resolved through
    /// subst and network mappings, junctions followed, short names expanded). For a path that does not exist yet, the
    /// nearest existing ancestor is resolved and the rest appended. Falls back to the full path on any failure.</summary>
    public static string Canon(string path)
    {
        string full = Path.GetFullPath(path);
        string probe = full; var rest = new Stack<string>();
        while (!Directory.Exists(probe) && !File.Exists(probe))
        {
            string? parent = Path.GetDirectoryName(probe);
            if (parent == null || parent == probe) return full;
            rest.Push(Path.GetFileName(probe)); probe = parent;
        }
        string resolved;
        try
        {
            using var h = Open(probe);
            if (h.IsInvalid) return full;
            var sb = new StringBuilder(2048);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) return full;
            resolved = sb.ToString();
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) resolved = @"\\" + resolved.Substring(8);
            else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved.Substring(4);
        }
        catch { return full; }
        while (rest.Count > 0) resolved = Path.Combine(resolved, rest.Pop());
        return resolved;
    }

    /// <summary>True when both paths are the same file on disk (same volume and file index), whatever they are called.</summary>
    public static bool SameFile(string a, string b)
    {
        try
        {
            using var ha = Open(Path.GetFullPath(a)); using var hb = Open(Path.GetFullPath(b));
            if (ha.IsInvalid || hb.IsInvalid) return false;
            if (!GetFileInformationByHandle(ha, out var ia) || !GetFileInformationByHandle(hb, out var ib)) return false;
            return ia.VolumeSerial == ib.VolumeSerial && ia.IndexHigh == ib.IndexHigh && ia.IndexLow == ib.IndexLow;
        }
        catch { return false; }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies inside it, by resolved identity.</summary>
    public static bool IsInside(string path, string folder)
    {
        string p = Canon(path).TrimEnd('\\'), d = Canon(folder).TrimEnd('\\');
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
