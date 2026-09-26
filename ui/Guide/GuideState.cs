// Guide.cs —— 新手引导的状态层
//
// 三套东西共用这一份状态：
//   阶段一  首次启动向导（GuideWizard.cs）
//   阶段二  进界面后的高亮巡礼（GuideTour.cs）
//   阶段三  首页那块「快速上手」清单（GuideChecklist.cs 建UI，状态在这儿算）
//
// 存的地方统一走 Core.SetSetting / Core.Setting，
// 跟 lastProject、theme 这些挤在同一个 settings 里，不加第二套配置文件。
//
// 一条原则：**清单上的每一步都按真实状态算，不靠「点过就算」。**
// 「配了 JDK」得真是 CurrentJdk().Ok 才算完成 —— 用户手抖点了按钮、
// 或者 JDK 后来被删了，清单还得说实话。只有「编译运行过一次」这种
// 留不下痕迹的动作才落成标记。

using System;
using System.Collections.Generic;
using System.IO;

namespace JavaStudio;

internal static class GuideState
{
    /// <summary>整套引导（向导 + 巡礼）已经走完，或者被用户跳过了。</summary>
    public const string KeyDone = "guide.done";

    /// <summary>剩下的是首页清单的完成标记。JDK 和项目两项不看标记，见 Steps。</summary>
    public const string KeyBuilt = "check.build";
    public const string KeyToured = "check.guide";

    /// <summary>上次见到的 install.stamp 内容（见 <see cref="IsFreshInstall"/>）。</summary>
    public const string KeyStamp = "guide.installStamp";

    private static bool Flag(string key) => Core.Setting(key) == "1";

    private static void Set(string key) => Core.SetSetting(key, "1");

    /// <summary>第一次运行吗。标记为「走完」之前，每次启动都算第一次。</summary>
    public static bool IsFirstRun => !Flag(KeyDone);

    /// <summary>
    /// 这一份是**刚装上的**吗 —— 哪怕用户以前用过、甚至重装过很多次。
    ///
    /// 为什么不能只看 <see cref="IsFirstRun"/>：那靠的是用户配置目录里的
    /// guide.done，而卸载时我们特意保留了那份配置（里面有他的主题、最近项目），
    /// 于是重装完 guide.done 还是 1 —— 「新装好的软件却不给新手引导」。
    ///
    /// 安装器装完会在 exe 旁边写一个 install.stamp（含安装时刻，每次安装都不同），
    /// 把它跟上次记下的那份比一比就知道了。
    ///
    /// ⚠️ 比对完立刻把新值存进去：这一问有副作用，同一个实例里问第二次会得到
    /// false。别在别处拿来当「是不是安装包装的」这种无副作用的查询用。
    /// ⚠️ stamp 不在（比如整个程序目录是直接拷过来的）返回 false ——
    /// 那是便携用法，不是「安装」，不替他做决定。
    /// </summary>
    public static bool IsFreshInstall()
    {
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "install.stamp");
            if (!File.Exists(p)) return false;

            string now = File.ReadAllText(p).Trim();
            if (string.IsNullOrEmpty(now)) return false;

            string last = Core.Setting(KeyStamp);
            Core.SetSetting(KeyStamp, now);   // 先记下，引导没跑成也不会天天弹
            return last != now;
        }
        catch { return false; }
    }

    public static void MarkDone() => Set(KeyDone);

    /// <summary>「帮助 → 新手引导」重看时用：清掉引导走过的标记，保留清单完成度。</summary>
    public static void ResetGuide() => Core.SetSetting(KeyDone, "0");

    // ---------------------------------------------------------------- 清单

    public sealed record Step(string Key, string TitleKey, bool Done, Action Go);

    /// <summary>
    /// 首页清单的那几步。Done 每次都现算（读起来不贵：一次 JDK 查询 + 翻最近列表）。
    /// Go 为空表示这步只能自己动手，点了没地方跳。
    /// </summary>
    public static List<Step> Steps()
    {
        bool jdkOk;
        try { jdkOk = Core.CurrentJdk().Ok; }
        catch { jdkOk = false; }

        // 项目这步只能现算。原先还留了个 check.project 标记兜底，
        // 结果是：用户把项目删了、或者项目在外接盘上拔了，
        // 清单照样打着勾 —— 界面在说假话，比不打勾更糟。
        // lastProject 和最近列表都是磁盘上的事实，两个里有一个真在就算数。
        bool projectOk = false;
        try
        {
            projectOk = Core.Recent().Exists(Directory.Exists);
            if (!projectOk)
            {
                string last = Core.Setting("lastProject");
                projectOk = !string.IsNullOrEmpty(last) && Directory.Exists(last);
            }
        }
        catch { /* 读不出来就当没项目 */ }

        return new List<Step>
        {
            new Step("jdk",     "guide.check.jdk",     jdkOk,     null),
            new Step("project", "guide.check.project", projectOk, null),
            new Step("build",   "guide.check.build",   Flag(KeyBuilt),  null),
            new Step("guide",   "guide.check.guide",   Flag(KeyToured), null),
        };
    }

    public static bool AllDone() => Steps().TrueForAll(s => s.Done);

    // ---------------------------------------------------------------- 各处回调

    /// <summary>编译成功时调一下：「编译并运行一次」这一步就算过了。</summary>
    public static void NoteBuildOk() => Set(KeyBuilt);

    /// <summary>巡礼走完（不是跳过）时调一下。</summary>
    public static void NoteTourFinished() => Set(KeyToured);
}
