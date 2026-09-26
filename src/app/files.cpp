// files.cpp —— 打开项目 / 打开文件 / 打开 jar / 保存 / 项目树
//
// 「打开」这个动作在 Java Studio 里有四种含义，openPath 负责分辨：
//   .jar / .war / .zip  → 进 jar 查看器
//   目录                → 进工作区，建项目树
//   .java               → 进编辑器，高亮
//   别的文件            → 进编辑器，纯文本
//
// 另外这里管「最近打开」的持久化和项目树的构建。
#include <algorithm>
#include <cstdio>
#include <cstring>

#include "app/app.h"
#include "libs/jlibs.h"

namespace app {

using namespace spp::plat;

// ---------------------------------------------------------------- 单例

App& get() {
    static App a;
    return a;
}

// ---------------------------------------------------------------- 资源目录

std::string findAssetsDir() {
    // 开发时从源码目录跑，发布时 assets 在 exe 旁边。
    // 都试一下，第一个找到的算。
    std::string exeDir = executableDir();
    const char* rel[] = {"assets/icons", "../assets/icons",
                         "../../assets/icons",
                         "packages/javastudio/assets/icons",
                         "packages/side/assets/icons"};

    for (const char* r : rel) {
        if (fileExists(joinPath(r, "icons.json"))) return r;
    }
    // 相对 exe 找
    if (!exeDir.empty()) {
        std::string p = joinPath(exeDir, "assets");
        p = joinPath(p, "icons");
        if (fileExists(joinPath(p, "icons.json"))) return p;
    }
    return "assets/icons";
}

// ---------------------------------------------------------------- 会话
//
// 只记「最近打开的项目」，不自动恢复上次的项目 ——
// 用户明确说过启动要停在起始页。

static std::string sessionFile() {
    return joinPath(appDataDir("JavaStudio"), "session.txt");
}

void saveSession(App& a) {
    std::string out = "# Java Studio session\n";
    for (const auto& r : a.recent) {
        out += r;
        out += "\n";
    }
    writeFile(sessionFile(), out);
}

void loadSession(App& a) {
    std::string data = readFile(sessionFile());
    if (data.empty()) return;

    std::string cur;
    for (size_t i = 0; i <= data.size(); i++) {
        if (i == data.size() || data[i] == '\n') {
            if (!cur.empty() && cur.back() == '\r') cur.pop_back();
            // 读入时就统一写法，历史文件里的 D://a//b 之类一并理顺
            if (!cur.empty() && cur[0] != '#') a.recent.push_back(normalizePath(cur));
            cur.clear();
        } else {
            cur += data[i];
        }
    }
    if (a.recent.size() > 12) a.recent.resize(12);
}

void pushRecent(App& a, const std::string& path) {
    // 存之前先规范化：同一个项目可能一会儿是 D:/a/b 一会儿是 D:\\a\\b，
    // 不统一的话列表里会出现两条长得不一样、其实是同一个项目的记录。
    std::string p = normalizePath(path);
    auto same = [&](const std::string& x) { return normalizePath(x) == p; };
    a.recent.erase(std::remove_if(a.recent.begin(), a.recent.end(), same),
                   a.recent.end());
    a.recent.insert(a.recent.begin(), p);
    if (a.recent.size() > 12) a.recent.resize(12);
}

// ---------------------------------------------------------------- 文件名工具

static bool endsWith(const std::string& s, const char* suf) {
    size_t n = strlen(suf);
    return s.size() >= n && s.compare(s.size() - n, n, suf) == 0;
}

static bool isJarName(const std::string& s) {
    return endsWith(s, ".jar") || endsWith(s, ".war") || endsWith(s, ".zip");
}

static std::string baseName(const std::string& path) {
    size_t slash = path.find_last_of("\\/");
    if (slash == std::string::npos) return path;
    return path.substr(slash + 1);
}

static std::string lowerOf(const std::string& s) {
    std::string r = s;
    for (char& c : r) {
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
    }
    return r;
}

// 文件树里显示什么图标。图标名要和 assets/icons 里的对得上。
static const char* iconForFile(const std::string& name) {
    std::string n = lowerOf(name);

    if (endsWith(n, ".java")) return "file-text";
    if (isJarName(n)) return "package";
    if (endsWith(n, ".xml") || endsWith(n, ".gradle") || endsWith(n, ".kts"))
        return "settings";
    if (n == "pom.xml") return "package";
    if (endsWith(n, ".properties") || endsWith(n, ".yml") ||
        endsWith(n, ".yaml") || endsWith(n, ".toml"))
        return "sliders";
    if (endsWith(n, ".md") || endsWith(n, ".txt")) return "book";
    if (endsWith(n, ".json") || endsWith(n, ".js") || endsWith(n, ".ts"))
        return "code";
    if (endsWith(n, ".png") || endsWith(n, ".jpg") || endsWith(n, ".jpeg") ||
        endsWith(n, ".gif") || endsWith(n, ".svg") || endsWith(n, ".ico"))
        return "image";
    if (endsWith(n, ".class")) return "package";
    if (n == ".gitignore" || endsWith(n, ".gitignore")) return "git-branch";
    return "file";
}

// ---------------------------------------------------------------- 项目树
//
// 建树规则（针对 Java 项目挑了挑）：
//   - 跳过 .git / build / target / out / node_modules / .idea
//     这些目录又大又吵，IDE 里看到它们只会干扰
//   - 只递归到 6 层，避免有人把项目建在 C:\ 上面卡死
//   - 目录在前、文件在后，各自按名字排

static bool skipDir(const std::string& name) {
    static const char* kSkip[] = {".git",  "build", "target", "out",
                                  ".idea", "node_modules", ".gradle",
                                  ".workbuddy", ".probe-cpp", "dist"};
    for (const char* s : kSkip) {
        if (name == s) return true;
    }
    return false;
}

static void addDir(App& a, const std::string& dir, int depth, int parent,
                   int limit) {
    if (depth > limit) return;

    auto entries = listDir(dir);
    if (entries.empty()) return;

    // 排序：目录优先，然后按名字（不区分大小写的字典序）
    std::sort(entries.begin(), entries.end(),
              [](const DirEntry& x, const DirEntry& y) {
                  if (x.isDir != y.isDir) return x.isDir;
                  std::string xs = x.name, ys = y.name;
                  for (char& c : xs)
                      if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
                  for (char& c : ys)
                      if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
                  return xs < ys;
              });

    for (const auto& e : entries) {
        if (e.isDir && skipDir(e.name)) continue;
        // 隐藏文件基本是噪音，但 .gitignore 有用
        if (!e.isDir && !e.name.empty() && e.name[0] == '.' &&
            e.name != ".gitignore")
            continue;
        if (e.isDir && !e.name.empty() && e.name[0] == '.' && e.name != ".github")
            continue;

        Node n;
        n.ui.name = e.name;
        n.ui.depth = depth;
        n.ui.isDir = e.isDir;
        n.ui.isFile = !e.isDir;
        n.ui.path = joinPath(dir, e.name);
        n.ui.iconName = e.isDir ? (depth == 0 ? "folder-open" : "folder")
                                : iconForFile(e.name);
        n.kind = e.isDir ? NodeKind::Dir
                         : (endsWith(lowerOf(e.name), ".java")
                                ? NodeKind::JavaFile
                                : (isJarName(lowerOf(e.name))
                                       ? NodeKind::JarFile
                                       : NodeKind::OtherFile));

        int idx = (int)a.tree.size();
        a.tree.push_back(n);
        a.treeParent.push_back(parent);

        if (e.isDir) {
            addDir(a, n.ui.path, depth + 1, idx, limit);
        }
    }
}

void rebuildTree(App& a) {
    a.tree.clear();
    a.treeParent.clear();
    a.treeSelected = -1;
    a.treeScroll = 0;
    if (a.projectRoot.empty() || !dirExists(a.projectRoot)) return;

    addDir(a, a.projectRoot, 0, -1, 6);
}

// ---------------------------------------------------------------- 打开项目

static void closeAll(App& a) {
    a.files.clear();
    a.activeTab = -1;
    a.tree.clear();
    a.treeParent.clear();
    a.treeSelected = -1;
    a.treeScroll = 0;
    a.diags.clear();
    a.lastMainClass.clear();
}

bool openProject(App& a, const std::string& root) {
    if (!dirExists(root)) {
        a.log(LogLevel::Error, "目录不存在：" + root);
        return false;
    }

    closeAll(a);
    a.projectRoot = root;
    a.projectName = baseName(root);
    if (a.projectName.empty() || a.projectName == "." ||
        a.projectName == "..")
        a.projectName = "当前目录";

    rebuildTree(a);
    a.screen = Screen::Editor;
    a.statusLeft = "项目已打开";

    // 打开项目产生的日志全在「运行日志」页，把底部面板切过去并展开，
    // 否则用户看到的是空的「构建输出」，会以为没日志。
    a.bottom = BottomTab::Run;
    a.bottomOpen = true;
    a.logScroll = 0;

    a.log(LogLevel::Ok, "打开项目 " + a.projectName + "（" +
                            std::to_string(a.tree.size()) + " 个条目）");

    // 认一下构建方式和主类，状态栏能显示出来
    std::string tool = jbuild::detectTool(root);
    a.lastMainClass = jbuild::guessMainClass(root);
    a.log(LogLevel::Info, std::string("构建方式：") +
                              (tool == "maven"    ? "Maven"
                               : tool == "gradle" ? "Gradle"
                                                  : "javac"));
    if (!a.lastMainClass.empty())
        a.log(LogLevel::Info, "主类：" + a.lastMainClass);

    // ---- 第三方库
    //
    // 从两处凑：「.javastudio/libs.txt」是用户在 IDE 里勾的，
    // pom.xml 是他自己写的。合起来去重 —— 项目本来就有 pom 的时候，
    // 用户不该被要求再勾一遍相同的库。
    {
        std::vector<std::string> ids = jlibs::loadSelection(root);

        for (const auto& coord : jlibs::pomCoords(root)) {
            // 坐标转 id：目录里认得出来才收
            std::string want = coord;
            bool found = false;
            for (const auto& e : jlibs::catalog()) {
                std::string pre = e.group + ":" + e.artifact;
                if (want.rfind(pre, 0) == 0) {
                    bool dup = false;
                    for (const auto& x : ids)
                        if (x == e.id) dup = true;
                    if (!dup) ids.push_back(e.id);
                    found = true;
                    break;
                }
            }
            if (!found) {
                // 目录里没有的依赖（版本不同的第三方），不影响 ——
                // 走 Maven 的项目 mvn 自己会去拉
                (void)found;
            }
        }

        a.libs.selected = ids;
        syncLibs(a, false);

        if (!a.libs.selected.empty()) {
            std::string msg = "第三方库 " +
                              std::to_string(a.libJars.size()) + "/" +
                              std::to_string(a.libs.selected.size()) + " 就绪";
            a.log(a.libMissing.empty() ? LogLevel::Ok : LogLevel::Warn, msg);
        }
    }

    pushRecent(a, root);
    saveSession(a);
    return true;
}

// ---------------------------------------------------------------- 打开文件

bool loadFile(App& a, const std::string& path) {
    // 已经开着就切过去
    for (size_t i = 0; i < a.files.size(); i++) {
        if (a.files[i].path == path) {
            a.activeTab = (int)i;
            a.screen = Screen::Editor;
            return true;
        }
    }

    if (!fileExists(path)) {
        a.log(LogLevel::Error, "打不开文件: " + path);
        return false;
    }

    std::string data = readFile(path);
    // 超过 4MB 不往编辑器里塞。Java 源码没这么大的，
    // 撞到这种情况多半是用户点了个二进制文件。
    if (data.size() > 4 * 1024 * 1024) {
        a.log(LogLevel::Warn, "文件过大，未打开: " + baseName(path));
        return false;
    }

    // 二进制文件（含 0 字节）不打开，避免编辑器里出现一屏乱码
    for (size_t i = 0; i < data.size() && i < 8192; i++) {
        if (data[i] == '\0') {
            a.log(LogLevel::Warn, "这是二进制文件，未打开: " + baseName(path));
            return false;
        }
    }

    OpenFile f;
    f.path = path;
    f.title = baseName(path);
    f.isJava = endsWith(lowerOf(f.title), ".java");

    // 拆行。统一成 LF —— 存回去的时候也不还原 CRLF，
    // Git 的 autocrlf 会处理，IDE 里混着两种换行符才是麻烦。
    std::string cur;
    for (char c : data) {
        if (c == '\n') {
            if (!cur.empty() && cur.back() == '\r') cur.pop_back();
            f.lines.push_back(cur);
            cur.clear();
        } else {
            cur += c;
        }
    }
    if (!cur.empty()) f.lines.push_back(cur);
    if (f.lines.empty()) f.lines.push_back("");

    recolorAll(f);

    a.files.push_back(std::move(f));
    a.activeTab = (int)a.files.size() - 1;
    a.screen = Screen::Editor;

    OpenFile& nf = a.files[a.activeTab];
    a.log(LogLevel::Info, "打开 " + nf.title + "（" +
                              std::to_string(nf.lines.size()) + " 行）");
    return true;
}

// ---------------------------------------------------------------- 打开 jar

void openJar(App& a, const std::string& path) {
    a.log(LogLevel::Info, "读取 " + baseName(path) + " …");
    a.jar = jarpack::read(path);

    if (!a.jar.ok) {
        a.log(LogLevel::Error, "打不开 jar：" + a.jar.error);
        return;
    }

    a.jarScroll = 0;
    a.jarSelected = -1;
    a.screen = Screen::Jar;

    char buf[256];
    snprintf(buf, sizeof(buf), "已载入 %s：%d 个 class，%d 个资源（%s）",
             baseName(path).c_str(), a.jar.classCount, a.jar.resourceCount,
             a.jar.javaVersionName().c_str());
    a.log(LogLevel::Ok, buf);

    if (!a.jar.mainClass.empty())
        a.log(LogLevel::Info, "Main-Class: " + a.jar.mainClass);
    else
        a.log(LogLevel::Warn,
              "清单里没写 Main-Class，可能不能直接运行（不是可执行 jar）");
}

// ---------------------------------------------------------------- 统一入口

void openPath(App& a, const std::string& path) {
    if (dirExists(path)) {
        openProject(a, path);
        return;
    }
    if (!fileExists(path)) {
        a.log(LogLevel::Error, "路径不存在：" + path);
        return;
    }

    std::string low = lowerOf(path);
    if (isJarName(low)) {
        openJar(a, path);
        return;
    }
    loadFile(a, path);
}

// ---------------------------------------------------------------- 保存

bool saveFile(App& a, int idx) {
    if (idx < 0 || idx >= (int)a.files.size()) return false;
    OpenFile& f = a.files[idx];

    if (writeFile(f.path, f.text())) {
        f.dirty = false;
        a.log(LogLevel::Ok, "已保存 " + f.title);
        return true;
    }
    a.log(LogLevel::Error, "保存失败：" + f.path);
    return false;
}

bool saveAll(App& a) {
    bool any = false;
    for (size_t i = 0; i < a.files.size(); i++) {
        if (!a.files[i].dirty) continue;
        if (saveFile(a, (int)i)) any = true;
    }
    if (!any) a.log(LogLevel::Info, "没有需要保存的文件");
    return any;
}

// ---------------------------------------------------------------- 新建项目

// 项目默认建在「安装目录/item」。
//
// 为什么不是「我的文档」：这个 IDE 是便携式的，整目录拷走就能用。
// 项目也跟着走，插到别人机器上打开还是同一份，不会散落在用户目录里
// 找不着。而且 exe 装在 Program Files 时写不进去 —— 那种情况下
// 退回到用户目录，别让「创建」静默失败。
//
// 目录不存在就当场建。第一次用的时候它一定不存在，
// 让用户自己去建一个空文件夹是没道理的。
std::string projectsDir() {
    std::string exeDir = executableDir();
    if (!exeDir.empty()) {
        std::string item = joinPath(exeDir, "item");

        if (dirExists(item)) return item;
        if (makeDirs(item)) return item;

        // 装到写不进的地方（Program Files），退到用户目录
        std::string home = homeDir();
        std::string alt = home.empty() ? "" : joinPath(home, "JavaStudioProjects");
        if (!alt.empty() && (dirExists(alt) || makeDirs(alt))) return alt;
        if (!home.empty()) return home;
    }

    std::string home2 = homeDir();
    return home2.empty() ? std::string(".") : home2;
}

void beginNewProject(App& a) {
    a.np = NewProjectForm{};

    a.np.parentDir = projectsDir();
    if (a.np.parentDir.empty()) a.np.parentDir = ".";

    a.np.jdkRelease = "17";
    jdk::Info cur = jdk::current();
    if (cur.ok && cur.major > 0)
        a.np.jdkRelease = std::to_string(cur.major);

    a.dlg = Dialog::NewProject;
}

}  // namespace app
