// Recycle.cs —— 把文件/目录送进回收站
//
// 不用 Microsoft.VisualBasic.FileIO：C# 的 SDK 工程默认不引用那个程序集，
// 为一个删除动作去加引用不值当。SHFileOperationW 是系统 API，
// 加 FOF_ALLOWUNDO 就是「移到回收站」，不加就是彻底删掉。
//
// 之前删项目老是失败，两个原因：
//   1) FOF_SILENT | FOF_NOERRORUI 会把所有错误吞掉，返回 0 也像成功，
//      实际上没删成。现在去掉了，出错直接冒出来。
//   2) 目标目录里如果有隐藏/只读文件、或者别的进程占着，SHFileOperationW
//      会失败。加 FOF_NOCONFIRMATION 只是不问确认，不是绕开权限。
//   3) SHFileOperationW 要求进程的当前目录不能是正在删的那个目录，而且
//      从 shell32 发起的操作最好给个有效的父窗口句柄。

using System;
using System.Runtime.InteropServices;
using System.IO;

namespace JavaStudio;

internal static class Recycle
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;      // 进回收站
    private const ushort FOF_NOCONFIRMATION = 0x0010; // 别弹系统确认框（我们自己问过了）

    /// <summary>
    /// 送进回收站。返回 (成功?, 出错信息)。出错信息空串表示成功。
    /// </summary>
    public static bool Send(string path)
    {
        return Send(path, out _);
    }

    public static bool Send(string path, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(path)) { error = Lang.T("rec.emptyPath"); return false; }
        if (!Directory.Exists(path) && !File.Exists(path))
        { error = Lang.T("rec.noPath") + path; return false; }

        // 别让当前目录就是被删的那个目录，否则 SHFileOperationW 会失败
        string cwd = Directory.GetCurrentDirectory();
        bool cwdInside = path.IndexOf(cwd, StringComparison.OrdinalIgnoreCase) == 0;
        if (cwdInside)
            Directory.SetCurrentDirectory(Path.GetPathRoot(cwd) ?? cwd);

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",          // 双 \0 结尾
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION
        };
        int rc = SHFileOperationW(ref op);

        if (rc != 0)
        {
            error = string.Format(Lang.T("rec.fail"), rc.ToString("X"));
            return false;
        }
        if (op.fAnyOperationsAborted != 0)
        {
            error = Lang.T("rec.aborted");
            return false;
        }
        // 有些情况返回 0 但没删掉，再确认一下
        if (Directory.Exists(path) || File.Exists(path))
        {
            error = Lang.T("rec.stillThere");
            return false;
        }
        return true;
    }
}
