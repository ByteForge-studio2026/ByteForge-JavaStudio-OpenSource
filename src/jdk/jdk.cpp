#include "jdk/jdk.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <set>
#include <string>
#include <vector>

#include "platform/platform.h"

namespace jdk {

using spp::plat::dirExists;
using spp::plat::fileExists;
using spp::plat::joinPath;
using spp::plat::readFile;
using spp::plat::writeFile;

// ---------------------------------------------------------------- 小工具

static std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t' || s[a] == '\r' || s[a] == '\n'))
        a++;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\r' ||
                     s[b - 1] == '\n'))
        b--;
    return s.substr(a, b - a);
}

// 从 "17.0.20.1" 里抠出 17
static int majorOf(const std::string& ver) {
    if (ver.empty()) return 0;
    size_t i = 0;
    int n = 0;
    while (i < ver.size() && ver[i] >= '0' && ver[i] <= '9') {
        n = n * 10 + (ver[i] - '0');
        i++;
    }
    // 老的 1.8.0 形式
    if (n == 1 && i < ver.size() && ver[i] == '.') {
        i++;
        int m = 0;
        while (i < ver.size() && ver[i] >= '0' && ver[i] <= '9') {
            m = m * 10 + (ver[i] - '0');
            i++;
        }
        return m;
    }
    return n;
}

std::string Info::label() const {
    if (!ok) return "未找到 JDK";
    char buf[64];
    snprintf(buf, sizeof(buf), "JDK %d", major);
    std::string s = buf;
    if (!version.empty()) s += "  (" + version + ")";
    return s;
}
// ---------------------------------------------------------------- 识别

// 从 JDK 根目录的 release 文件里读版本和厂商。
//
// 为什么优先读文件而不是跑 javac -version：
//   1) release 是纯文本，读一次几微秒；起一个进程要几十毫秒。
//      启动要扫 4 个位置，全走进程就是几百毫秒的卡顿（用户能感觉到
//      启动画面停在「扫描 JDK…」不动）。
//   2) 起进程要经过 cmd.exe。这条路上任何一环出问题（权限策略、
//      杀软拦截、PATH 被改）都会拿不到结果，版本就识别不出来。
//      读文件没有这些不确定性。
//
// 格式是一行一个 KEY="VALUE"：
//     IMPLEMENTOR="Eclipse Adoptium"
//     JAVA_VERSION="21.0.11"
static bool readRelease(const std::string& home, std::string& ver,
                        std::string& vendor) {
    std::string f = joinPath(home, "release");
    if (!fileExists(f)) return false;

    std::string s = readFile(f);
    if (s.empty()) return false;

    size_t pos = 0;
    while (pos < s.size()) {
        size_t e = s.find('\n', pos);
        if (e == std::string::npos) e = s.size();
        std::string line = trim(s.substr(pos, e - pos));
        pos = e + 1;

        size_t eq = line.find('=');
        if (eq == std::string::npos) continue;

        std::string key = trim(line.substr(0, eq));
        std::string val = trim(line.substr(eq + 1));
        // 去掉两边的引号
        if (val.size() >= 2 && val.front() == '"' && val.back() == '"')
            val = val.substr(1, val.size() - 2);

        if (key == "JAVA_VERSION") ver = val;
        else if (key == "IMPLEMENTOR") vendor = val;
    }
    return !ver.empty();
}

// 给一个 JDK 根目录，问它的 javac 是什么版本。
//
// 注意：可执行文件在 <home>/bin/ 下面，不是 <home>/ 下面。
// 之前漏了这层 bin，结果自带的 JDK 全都识别不出来（日志里是
// 「没有扫描到 JDK」，但 jdk/jdk-17 明明躺在那儿）。
static bool probe(const std::string& home, Info& out) {
    std::string bin = joinPath(home, "bin");
    std::string javac = joinPath(bin, "javac.exe");
    std::string java = joinPath(bin, "java.exe");

    // 先看文件在不在。不在就直接判否，省得往下算。
    if (!fileExists(javac) || !fileExists(java)) return false;

    int major = 0;
    std::string ver;
    std::string vendor;

    // 首选 release 文件
    readRelease(home, ver, vendor);

    // 读不到才退回去问 javac
    if (ver.empty()) {
        // 输出形如 "javac 17.0.20.1"
        std::string o = spp::plat::runCapture(javac + " -version");
        size_t sp = o.find(' ');
        if (sp != std::string::npos) ver = trim(o.substr(sp + 1));
    }

    major = majorOf(ver);

    // 还是没有版本号，就从目录名里猜最后一道（jdk-17 / jdk-21 这种）
    if (major == 0) {
        // 取最后一段路径当目录名。parentDir 会削掉最后一段，
        // 所以「父目录长度 + 1」之后就是名字。
        std::string parent = spp::plat::parentDir(home);
        std::string base = (parent.size() < home.size())
                               ? home.substr(parent.size() + 1)
                               : home;
        for (size_t i = 0; i < base.size(); i++) {
            if (base[i] < '0' || base[i] > '9') continue;
            int n = 0;
            size_t j = i;
            while (j < base.size() && base[j] >= '0' && base[j] <= '9') {
                n = n * 10 + (base[j] - '0');
                j++;
            }
            if (n >= 8 && n <= 99) {
                major = n;
                break;
            }
            i = j;
        }
    }

    out.home = home;
    out.javac = javac;
    out.java = java;
    out.jar = joinPath(bin, "jar.exe");
    out.version = ver;
    out.vendor = vendor;
    out.major = major;

    out.ok = fileExists(out.javac) && fileExists(out.java);
    if (!out.ok) return false;
    if (major == 0) return false;
    return true;
}

// 目录下找 jdk* 子目录。
// builtin：从 exe 旁边的 jdk\ 扫出来的才置 true（见 scan 的第 1 步）。
static void scanUnder(const std::string& base, std::vector<Info>& out,
                      std::set<int>& seen, bool builtin = false) {
    if (!dirExists(base)) return;

    std::vector<std::string> subs = spp::plat::listDirs(base);
    // 没子目录，那 base 自己可能就是 JDK 根
    if (subs.empty()) {
        Info i;
        if (probe(base, i) && !seen.count(i.major)) {
            i.builtin = builtin;
            seen.insert(i.major);
            out.push_back(i);
        }
        return;
    }

    for (const auto& s : subs) {
        std::string full = joinPath(base, s);
        // 先粗筛一下，省得每个子目录都去起进程跑 javac -version。
        // 同样记得 bin/ 这一层。
        if (!fileExists(joinPath(joinPath(full, "bin"), "javac.exe"))) continue;
        Info i;
        if (!probe(full, i)) continue;
        if (seen.count(i.major)) continue;
        i.builtin = builtin;
        seen.insert(i.major);
        out.push_back(i);
    }
}

// ---------------------------------------------------------------- 全局

static std::vector<Info> g_all;
static int g_selected = 0;      // major，0 = 还没定
static bool g_scanned = false;

// exe 在哪个目录。JDK 就放在它旁边的 jdk/。
static std::string exeDir() { return spp::plat::executableDir(); }

static std::string settingsFile() {
    return joinPath(spp::plat::appDataDir("JavaStudio"), "settings.txt");
}

std::vector<Info> scan() {
    g_all.clear();
    std::set<int> seen;

    // 1) 自带的。这个优先级最高。
    //    开发时 exe 埋在 ui/bin/Release/net9.0-windows 里，项目根的 jdk/
    //    离它有四层，只看一层根本够不着——这就是「新建项目里只有 JDK25、
    //    没有 21 和 17」的原因。往上多爬几级。
    {
        std::string dir = exeDir();
        for (int i = 0; i < 5; i++) {
            std::string probe = joinPath(dir, "jdk");
            scanUnder(probe, g_all, seen, /*builtin=*/true);
            std::string parent = spp::plat::parentDir(dir);
            if (parent == dir || parent.empty()) break;
            dir = parent;
        }
    }

    // 2) JAVA_HOME
    {
        const char* jh = getenv("JAVA_HOME");
        if (jh && *jh) {
            Info i;
            if (probe(jh, i) && !seen.count(i.major)) {
                seen.insert(i.major);
                g_all.push_back(i);
            }
        }
    }

    // 3) PATH。javac 在哪，JDK 就在它的上一级。
    {
        std::string p = spp::plat::whichExe("javac.exe");
        if (!p.empty()) {
            std::string bin = spp::plat::parentDir(p);
            std::string home = spp::plat::parentDir(bin);
            Info i;
            if (probe(home, i) && !seen.count(i.major)) {
                seen.insert(i.major);
                g_all.push_back(i);
            }
        }
    }

    // 4) 常见安装位置
    {
        std::string pf = spp::plat::getEnv("ProgramFiles");
        std::string pfx86 = spp::plat::getEnv("ProgramFiles(x86)");
        const std::string bases[] = {
            joinPath(pf, "Java"),
            joinPath(pf, "Eclipse Adoptium"),
            joinPath(pf, "Microsoft"),
            joinPath(pf, "Zulu"),
            joinPath(pf, "Amazon Corretto"),
            joinPath(pf, "BellSoft"),
            joinPath(joinPath(pf, "BellSoft"), "LibericaJDK"),
            joinPath(pfx86, "Java"),
            "D:\\Java",
            "D:\\jdk",
            // IDEA 默认把下载的 JDK 放这里，用户从 IDEA 迁过来时能直接认到
            joinPath(spp::plat::getEnv("USERPROFILE"), ".jdks"),
        };
        for (const auto& b : bases) scanUnder(b, g_all, seen);
    }

    // 按 major 升序，选版本高的当默认更合理
    std::sort(g_all.begin(), g_all.end(),
              [](const Info& a, const Info& b) { return a.major < b.major; });

    g_scanned = true;
    return g_all;
}

const std::vector<Info>& all() {
    if (!g_scanned) scan();
    return g_all;
}

Info find(int major) {
    for (const auto& i : all())
        if (i.major == major) return i;
    return Info{};
}

Info current() {
    if (!g_scanned) scan();
    if (g_all.empty()) return Info{};

    if (g_selected) {
        for (const auto& i : g_all)
            if (i.major == g_selected) return i;
    }
    // 没选过 / 选的那个没了 —— 挑版本号最高的
    return g_all.back();
}

bool select(int major) {
    if (!g_scanned) scan();
    for (const auto& i : g_all) {
        if (i.major == major) {
            g_selected = major;
            savePreference();
            return true;
        }
    }
    return false;
}

void loadPreference() {
    std::string f = settingsFile();
    if (!fileExists(f)) return;
    std::string s = readFile(f);
    // 一行一个键值。现在只有 jdk=21 这一项。
    size_t pos = 0;
    while (pos < s.size()) {
        size_t e = s.find('\n', pos);
        if (e == std::string::npos) e = s.size();
        std::string line = trim(s.substr(pos, e - pos));
        pos = e + 1;
        if (line.rfind("jdk=", 0) == 0) {
            g_selected = atoi(line.c_str() + 4);
        }
    }
}

void savePreference() {
    std::string dir = spp::plat::appDataDir("JavaStudio");
    spp::plat::makeDirs(dir);
    std::string file = joinPath(dir, "settings.txt");

    // ⚠️ 这里原来是 `writeFile(file, "jdk=N\n")` —— 整文件覆盖。
    // settings.txt 是**共用**的 key=value 存储：language / theme / lastProject
    // / 新手引导的标记（guide.done 等）都写在同一个文件里。
    // 覆盖式写法等于「用户每选一次 JDK，别的设置全部清零」——
    // 表现出来就是：语言自己变回中文、主页的「上次打开」消失、
    // 引导走了还会再弹（guide.done 被抹了，程序以为是第一次运行）。
    //
    // 所以只改自己那一行，别的原样留着。没找到 jdk= 就追加到末尾。
    std::string txt = fileExists(file) ? readFile(file) : "";
    std::vector<std::string> lines;
    bool hit = false;
    size_t pos = 0;
    while (pos <= txt.size()) {
        size_t e = txt.find('\n', pos);
        if (e == std::string::npos) e = txt.size();
        std::string line = trim(txt.substr(pos, e - pos));
        if (!line.empty()) {
            if (line.rfind("jdk=", 0) == 0) {
                lines.push_back("jdk=" + std::to_string(g_selected));
                hit = true;
            } else {
                lines.push_back(line);
            }
        }
        if (e == txt.size()) break;
        pos = e + 1;
    }
    if (!hit) lines.push_back("jdk=" + std::to_string(g_selected));

    std::string out;
    for (auto& l : lines) out += l + "\n";
    writeFile(file, out);
}

void refresh() {
    g_scanned = false;
    scan();
}

}  // namespace jdk
