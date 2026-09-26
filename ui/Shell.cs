// Shell.cs —— 清理资源管理器右键菜单里的「使用 Java Studio 打开」
//
// 这个功能去掉了：以前启动时（RegisterContextMenu）往 HKCU 写三个键，
// 老的安装程序（setup/_old/setup.cpp）也写过一批。现在改成启动时**反过来删** ——
// 用户升级到新版就会自动清掉，不用手工去注册表里抠。
//
// ⚠️ 为什么不是「干脆把这段代码删了」：键已经写在用户机器上了，
// 光删代码它还在右键里杵着，用户还得自己清理。留一段自愈的清理逻辑，
// 谁升到新版谁自动干净。

using System;
using System.Runtime.InteropServices;

namespace JavaStudio;

internal static class Shell
{
    private const string Verb = "JavaStudio";

    private static readonly IntPtr HKEY_CURRENT_USER = new IntPtr(unchecked((int)0x80000001));

    // 以前写过的三个位置：目录、目录背景（在文件夹空白处右键）、.jar 文件
    private static readonly string[] Keys =
    {
        @"Software\Classes\Directory\shell\" + Verb,
        @"Software\Classes\Directory\Background\shell\" + Verb,
        @"Software\Classes\SystemFileAssociations\.jar\shell\" + Verb,
    };

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteTreeW(IntPtr hKey, string lpSubKey);

    // 删完必须通知资源管理器「文件关联变了」，否则它一直用启动时的旧缓存，
    // 右键里那个菜单项要等重启 explorer.exe 才消失。
    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = false)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    /// <summary>
    /// 把以前注册过的右键菜单项删掉。键不存在时 RegDeleteTree 返回 2（找不到文件），
    /// 那是正常情况，不算错。
    /// </summary>
    public static void UnregisterContextMenu()
    {
        int removed = 0;
        try
        {
            foreach (var key in Keys)
            {
                // ⚠️ 用 RegDeleteTree 而不是 RegDeleteKey：我们的键下面还有个
                // command 子键，RegDeleteKey 在仍有子键时会失败（ERROR_ACCESS_DENIED
                // 或者干脆不删），留下半截键更麻烦。
                if (RegDeleteTreeW(HKEY_CURRENT_USER, key) == 0) removed++;
            }
            if (removed > 0)
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Core.Log("WARN", Lang.T("shell.unregisterFail") + ex.Message);
            return;
        }
        // 只有真的删掉了东西才记日志，否则每次启动都刷一行没意义的日志
        if (removed > 0) Core.Log("OK", string.Format(Lang.T("shell.unregistered"), removed));
    }
}
