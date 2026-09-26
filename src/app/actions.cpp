// actions.cpp —— 编译 / 运行 / 检查 / 新建文件 / 新建项目
//
// 这一层把「界面上的一个动作」翻译成「对 build / jar / project 模块的调用」，
// 再把结果整理成界面能直接显示的东西（日志行 + 诊断列表）。
//
// 分工很明确：
//   javabuild  只管跑 javac/mvn/gradle，返回结构化结果
//   这里       只管挑 JDK、填 Job、把结果翻译成人话
#include <algorithm>
#include <cstdio>
#include <cstring>

#include "app/app.h"
#include "libs/jlibs.h"
#include "project/javaproject.h"

namespace app {

using namespace spp::plat;

// ---------------------------------------------------------------- 工具

static std::string baseName_(const std::string& path) {
    size_t slash = path.find_last_of("\\/");
    return slash == std::string::npos ? path : path.substr(slash + 1);
}

// 把 javabuild 的诊断翻译成界面里的日志行。
// 红色给错误，黄色给警告 —— 这是「红色标错」的具体落点。
static void applyDiagnostics(App& a, const jbuild::Result& r) {
    a.diags = r.diags;

    for (const auto& d : r.diags) {
        LogLevel lv = d.level == jbuild::Level::Error ? LogLevel::Error
                      : d.level == jbuild::Level::Warning ? LogLevel::Warn
                                                          : LogLevel::Info;
        char buf[1024];
        std::string file = baseName_(d.file);
        if (d.line > 0) {
            snprintf(buf, sizeof(buf), "%s:%d:%d  %s", file.c_str(), d.line,
                     d.col, d.message.c_str());
        } else {
            snprintf(buf, sizeof(buf), "%s  %s", file.c_str(),
                     d.message.c_str());
        }
        a.build(lv, buf);
    }
}

// 找编译要用的 JDK。优先用用户设的默认版本，其次看项目里
// 有没有指定（pom 的 maven.compiler.release），最后用能拿到的。
static jdk::Info pickJdk(App& a) {
    jdk::Info cur = jdk::current();
    if (cur.ok) return cur;

    // 一个都没扫到。这时 javac 还能靠 PATH 里的碰运气，
    // 但得让用户知道情况。
    a.log(LogLevel::Warn, "没有找到可用的 JDK，请到「设置」里检查");
    return cur;
}

// ---------------------------------------------------------------- 第三方库

// 打开对话框。先把项目里已经勾过的读进来，再把状态算一遍 ——
// 用户打开这个框最关心的第一件事是「我现在有哪些」。
void openLibraryDialog(App& a) {
    if (a.projectRoot.empty()) {
        a.log(LogLevel::Warn, "先打开一个项目，再加第三方库");
        return;
    }

    a.libs.selected = jlibs::loadSelection(a.projectRoot);
    a.libs.query.clear();
    a.libs.category.clear();
    a.libs.scroll = 0;
    syncLibs(a, false);

    char buf[256];
    snprintf(buf, sizeof(buf), "第三方库：可选 %d 个，本机缓存已有 %d 个 jar",
             (int)jlibs::catalog().size(), jlibs::cacheCount());
    a.log(LogLevel::Info, buf);

    a.dlg = Dialog::Libraries;
}

// 把「想要哪些」算成「classpath 上有哪些」。
//
// 这一步不下载 —— 只做「本机有没有」的判断。下载是用户显式点
// 「下载」才发生的事，不然勾一下就偷偷跑网络，用户没法预期。
void syncLibs(App& a, bool verbose) {
    if (a.projectRoot.empty()) {
        a.libJars.clear();
        a.libMissing.clear();
        a.libMsg.clear();
        return;
    }

    jlibs::saveSelection(a.projectRoot, a.libs.selected);

    jlibs::Resolved r = jlibs::resolve(a.projectRoot, a.libs.selected);
    a.libJars = r.jars;
    a.libMissing = r.missingIds;
    a.libLastSync = (double)nowMs();

    int ready = (int)r.readyIds.size();
    int miss = (int)r.missingIds.size();

    if (a.libs.selected.empty()) {
        a.libMsg = "还没加库。勾上左边任意一个，代码里就能 import";
    } else if (miss == 0) {
        a.libMsg = "已就绪 " + std::to_string(ready) +
                   " 个库，都在 classpath 上了";
    } else {
        a.libMsg = "已就绪 " + std::to_string(ready) + " 个，还有 " +
                   std::to_string(miss) + " 个没下到本机";
    }

    if (verbose) {
        a.build(LogLevel::Info, "── 第三方库（" + std::to_string(ready + miss) +
                                    " 个）");
        for (const auto& id : r.readyIds) {
            const jlibs::Entry* e = jlibs::find(id);
            a.build(LogLevel::Ok, "✓ " + (e ? e->coord() : id));
        }
        for (const auto& id : r.missingIds) {
            const jlibs::Entry* e = jlibs::find(id);
            a.build(LogLevel::Warn,
                    "… " + (e ? e->coord() : id) + " 还没下载");
        }
    }
}

// 下一个库。成功返回 true。
//
// 是同步的 —— 下载期间界面不动。取这个折中是因为：
// 后台线程要配锁去改 a.libJars / a.files 这些共享状态，而下载
// 一个小 jar 通常一两秒；为它引一套并发模型不划算。
bool fetchLib(App& a, const std::string& id) {
    const jlibs::Entry* e = jlibs::find(id);
    if (!e) {
        a.libMsg = "目录里没有这个库：" + id;
        return false;
    }

    a.libMsg = "正在下载 " + e->name + "（" + e->version + "）…";
    requestPaint(nullptr);

    std::string err;
    std::string saved;
    bool ok = jlibs::download(*e, &err, &saved);

    // 不管成不成都要重算一次 —— 成功后它就从「缺」变成「就绪」
    syncLibs(a, false);

    if (!ok) {
        // syncLibs 会把 libMsg 改成汇总文案，所以失败信息要在它之后写
        a.libMsg = "下载 " + e->name + " 失败：" + err;
        a.log(LogLevel::Error, a.libMsg);
        return false;
    }

    a.log(LogLevel::Ok, "已下载 " + e->name + " → " + saved);
    a.build(LogLevel::Ok, "已下载 " + e->coord());
    return true;
}

std::string libClasspathString(App& a) {
    std::string s;
    for (size_t i = 0; i < a.libJars.size(); i++) {
        if (i) s += jlibs::separator();
        s += a.libJars[i];
    }
    return s;
}

// ---------------------------------------------------------------- 编译

void doCompile(App& a, bool thenRun) {
    if (a.projectRoot.empty()) {
        a.log(LogLevel::Warn, "还没打开项目，先在起始页新建或打开一个");
        a.bottom = BottomTab::Build;
        a.bottomOpen = true;
        return;
    }

    // 万一用户在别处改了 .javastudio/libs.txt，编译前再对一次，
    // 免得 classpath 用的是上一轮的旧结果
    if (a.libs.selected.empty()) {
        a.libs.selected = jlibs::loadSelection(a.projectRoot);
    }
    syncLibs(a, false);

    jdk::Info j = pickJdk(a);

    jbuild::Job job;
    job.projectRoot = a.projectRoot;
    job.jdkHome = j.ok ? j.home : "";
    job.sourceDir = joinPath(joinPath(a.projectRoot, "src"), "main");
    job.sourceDir = joinPath(job.sourceDir, "java");
    job.outputDir = joinPath(a.projectRoot, "out");
    job.mainClass = a.lastMainClass;
    job.classpath = a.libJars;   // 第三方库进 classpath
    job.verbose = false;

    std::string tool = jbuild::detectTool(a.projectRoot);

    // 起手先说清楚用哪套 JDK、走哪条路 —— 编译失败时这两条
    // 是最先要确认的信息。
    char head[320];
    std::string libNote;
    if (!a.libJars.empty()) {
        libNote = " + " + std::to_string(a.libJars.size()) + " 个第三方库";
    }
    snprintf(head, sizeof(head), "── 编译 %s（%s / %s%s）",
             a.projectName.c_str(),
             tool == "maven"    ? "Maven"
             : tool == "gradle" ? "Gradle"
                                : "javac",
             j.ok ? j.label().c_str() : "系统默认 JDK", libNote.c_str());
    a.build(LogLevel::Info, head);

    // 缺库要说出来。不说的话用户看到的是「找不到符号」，
    // 那条报错本身完全没提「你少了个 jar」。
    if (!a.libMissing.empty()) {
        std::string names;
        for (const auto& id : a.libMissing) {
            const jlibs::Entry* e = jlibs::find(id);
            if (!names.empty()) names += "、";
            names += (e ? e->name : id);
        }
        a.build(LogLevel::Warn, "⚠ 这些库还没下到本机：" + names);
        a.build(LogLevel::Warn,
                "  去「工具 → 第三方库」点下载，或者干脆不用它们");
    }

    a.bottom = BottomTab::Build;
    a.bottomOpen = true;
    a.diags.clear();

    jbuild::Result r = jbuild::compile(job);
    a.lastBuildSeconds = r.seconds;

    // 原始输出整段收进来。用户想看细节时这是唯一来源。
    if (!r.rawOutput.empty()) {
        std::string cur;
        for (size_t i = 0; i <= r.rawOutput.size(); i++) {
            if (i == r.rawOutput.size() || r.rawOutput[i] == '\n') {
                if (!cur.empty() && cur.back() == '\r') cur.pop_back();
                if (!cur.empty()) a.build(LogLevel::Info, cur);
                cur.clear();
            } else {
                cur += r.rawOutput[i];
            }
        }
    }

    applyDiagnostics(a, r);

    char tail[256];
    if (r.ok) {
        snprintf(tail, sizeof(tail), "✓ 编译通过（%.2f 秒）", r.seconds);
        a.build(LogLevel::Ok, tail);
        a.log(LogLevel::Ok, "编译通过 " + a.projectName);
        a.statusLeft = "● 编译通过";
    } else {
        snprintf(tail, sizeof(tail), "✗ 编译失败：%d 个错误，%d 个警告（%.2f 秒）",
                 r.errorCount, r.warnCount, r.seconds);
        a.build(LogLevel::Error, tail);
        a.log(LogLevel::Error, "编译失败：" + std::to_string(r.errorCount) +
                                   " 个错误");
        a.statusLeft = "● 编译失败";
        // 有错就自动切到「问题」页 —— 用户最想看的是那几行红字
        if (r.errorCount > 0) a.bottom = BottomTab::Problems;
    }
    a.statusRight = "Java Studio";

    if (r.ok && thenRun) doRun(a);
}

// ---------------------------------------------------------------- 运行

void doRun(App& a) {
    if (a.projectRoot.empty()) {
        a.log(LogLevel::Warn, "还没打开项目");
        return;
    }

    if (a.libs.selected.empty()) {
        a.libs.selected = jlibs::loadSelection(a.projectRoot);
    }
    syncLibs(a, false);

    jdk::Info j = pickJdk(a);

    jbuild::Job job;
    job.projectRoot = a.projectRoot;
    job.jdkHome = j.ok ? j.home : "";
    job.outputDir = joinPath(a.projectRoot, "out");
    job.mainClass = a.lastMainClass;
    job.classpath = a.libJars;   // 运行时也要能加载到这些类
    if (job.mainClass.empty()) job.mainClass = jbuild::guessMainClass(a.projectRoot);
    a.lastMainClass = job.mainClass;

    if (job.mainClass.empty()) {
        a.log(LogLevel::Warn, "找不到主类（没有 public static void main），无法运行");
        a.bottom = BottomTab::Run;
        a.bottomOpen = true;
        return;
    }

    a.bottom = BottomTab::Run;
    a.bottomOpen = true;
    a.build(LogLevel::Info, "── 运行 " + job.mainClass);

    jbuild::Result r = jbuild::run(job, {});
    a.lastExitCode = r.exitCode;

    if (!r.rawOutput.empty()) {
        std::string cur;
        for (size_t i = 0; i <= r.rawOutput.size(); i++) {
            if (i == r.rawOutput.size() || r.rawOutput[i] == '\n') {
                if (!cur.empty() && cur.back() == '\r') cur.pop_back();
                a.log(LogLevel::Info, cur);
                cur.clear();
            } else {
                cur += r.rawOutput[i];
            }
        }
    }

    char buf[192];
    if (r.exitCode == 0) {
        snprintf(buf, sizeof(buf), "✓ 运行结束，退出码 0（%.2f 秒）", r.seconds);
        a.log(LogLevel::Ok, buf);
        a.statusLeft = "● 运行完成";
    } else {
        snprintf(buf, sizeof(buf), "✗ 运行异常退出，退出码 %d", r.exitCode);
        a.log(LogLevel::Error, buf);
        a.statusLeft = "● 运行失败";
    }
}

// ---------------------------------------------------------------- 代码检查
//
// 「检查」和「编译」不是一回事：检查只做词法 + 结构上的粗筛，
// 不真的调 javac，所以快。适合一边写一边按。
//
// 真正准的错还是得靠编译，但那个慢，不适合每次按键都跑。
void doCheck(App& a) {
    int idx = a.activeIndex();
    if (idx < 0) {
        a.log(LogLevel::Warn, "没有打开的文件");
        return;
    }

    OpenFile& f = a.files[idx];
    if (!f.isJava) {
        a.log(LogLevel::Info, f.title + " 不是 Java 文件，跳过检查");
        return;
    }

    a.log(LogLevel::Info, "检查 " + f.title + " …");

    // ---- 粗筛一：括号配对
    //
    // 这是最常见的「编译才发现的错」，而且纯文本就能查出来。
    // 用栈扫一遍，遇到不配对就记下来。
    struct Issue {
        int line;
        std::string msg;
    };
    std::vector<Issue> issues;

    std::vector<std::pair<char, int>> stack;   // {括号, 行号}
    bool inBlock = false, inText = false;

    for (size_t li = 0; li < f.lines.size(); li++) {
        bool cb = inBlock, ct = inText;
        auto spans = jlex::lexLine(f.lines[li], cb, ct);
        inBlock = cb;
        inText = ct;

        // 按 span 走，跳过注释和字符串里的括号
        for (const auto& sp : spans) {
            if (sp.cat == jlex::Cat::Comment || sp.cat == jlex::Cat::Javadoc ||
                sp.cat == jlex::Cat::StringLit || sp.cat == jlex::Cat::CharLit)
                continue;
            if (sp.cat != jlex::Cat::Punct) continue;

            for (int k = 0; k < sp.len; k++) {
                char c = f.lines[li][sp.start + k];
                if (c == '(' || c == '{' || c == '[') {
                    stack.push_back({c, (int)li + 1});
                } else if (c == ')' || c == '}' || c == ']') {
                    char want = (c == ')') ? '(' : (c == '}') ? '{' : '[';
                    if (stack.empty()) {
                        issues.push_back(
                            {(int)li + 1, std::string("多余的 '") + c + "'"});
                    } else if (stack.back().first != want) {
                        char got = stack.back().first;
                        char buf[128];
                        snprintf(buf, sizeof(buf),
                                 "括号不匹配：'%c'（第 %d 行）配了 '%c'",
                                 got, stack.back().second, c);
                        issues.push_back({(int)li + 1, buf});
                        stack.pop_back();
                    } else {
                        stack.pop_back();
                    }
                }
            }
        }
    }

    for (const auto& s : stack) {
        char buf[128];
        char close = (s.first == '(') ? ')' : (s.first == '{') ? '}' : ']';
        snprintf(buf, sizeof(buf), "'%c'（第 %d 行）没有闭合的 '%c'",
                 s.first, s.second, close);
        issues.push_back({s.second, buf});
    }

    // ---- 粗筛二：兜底
    //
    // 有结构性问题时至少要知道它的行，方便跳过去。
    a.diags.clear();
    for (const auto& s : issues) {
        char buf[256];
        snprintf(buf, sizeof(buf), "%s:%d  %s", f.title.c_str(), s.line,
                 s.msg.c_str());

        jbuild::Diagnostic d;
        d.level = jbuild::Level::Error;
        d.file = f.path;
        d.line = s.line;
        d.col = 1;
        d.message = s.msg;
        a.diags.push_back(d);

        a.build(LogLevel::Error, buf);
    }

    a.bottom = BottomTab::Problems;
    a.bottomOpen = true;

    char buf[192];
    if (issues.empty()) {
        snprintf(buf, sizeof(buf), "✓ %s 检查通过（%d 行）", f.title.c_str(),
                 (int)f.lines.size());
        a.log(LogLevel::Ok, buf);
        a.statusLeft = "● 检查通过";
    } else {
        snprintf(buf, sizeof(buf), "✗ %s 发现 %d 个问题", f.title.c_str(),
                 (int)issues.size());
        a.log(LogLevel::Error, buf);
        a.statusLeft = "● 有问题";
    }
}

// ---------------------------------------------------------------- 新建项目

// 真把项目造出来。成功就打开它。
void createProjectNow(App& a) {
    NewProjectForm& f = a.np;

    std::string name = f.name;
    while (!name.empty() && (name.front() == ' ' || name.front() == '\t'))
        name.erase(name.begin());
    while (!name.empty() && (name.back() == ' ' || name.back() == '\t'))
        name.pop_back();

    if (name.empty()) {
        f.error = "项目名不能为空";
        return;
    }

    std::string err = javaproj::validateName(name);
    if (!err.empty()) {
        f.error = err;
        return;
    }
    if (f.parentDir.empty()) {
        f.error = "先选一个位置";
        return;
    }

    std::string group = f.group.empty() ? "com.example" : f.group;
    err = javaproj::validatePackage(group, name);
    if (!err.empty()) {
        f.error = err;
        return;
    }

    std::string target = joinPath(f.parentDir, name);

    javaproj::Options opt;
    opt.dir = target;
    opt.name = name;
    opt.group = group;
    opt.artifact = f.artifact.empty() ? name : f.artifact;
    opt.version = "1.0.0";
    opt.jdkRelease = f.jdkRelease.empty() ? "17" : f.jdkRelease;
    opt.kind = (javaproj::Kind)f.kind;
    opt.tool = (javaproj::BuildTool)f.tool;

    javaproj::Result r = javaproj::create(opt);
    if (!r.ok) {
        f.error = r.error;
        a.log(LogLevel::Error, "新建失败：" + r.error);
        return;
    }

    a.log(LogLevel::Ok, "已创建 " + name + "（" + std::to_string(r.created.size()) +
                            " 个文件，" + javaproj::kindName(opt.kind) + " / " +
                            javaproj::buildToolName(opt.tool) + "，JDK " +
                            opt.jdkRelease + "）");

    a.dlg = Dialog::None;
    openProject(a, target);
}

// 新建一个空文件。路径相对项目根。
void createFileNow(App& a) {
    std::string name = a.newFileName;
    while (!name.empty() && (name.front() == ' ' || name.front() == '\t'))
        name.erase(name.begin());
    while (!name.empty() && (name.back() == ' ' || name.back() == '\t'))
        name.pop_back();

    if (name.empty()) {
        a.log(LogLevel::Warn, "文件名不能为空");
        return;
    }
    if (a.projectRoot.empty()) {
        a.log(LogLevel::Warn, "还没打开项目");
        a.dlg = Dialog::None;
        return;
    }

    // 裸文件名补上 .java 和包路径，省得用户自己拼目录
    if (name.find('.') == std::string::npos) name += ".java";

    std::string path = name;
    if (name.find('/') == std::string::npos &&
        name.find('\\') == std::string::npos) {
        if (name.size() > 5 && name.compare(name.size() - 5, 5, ".java") == 0) {
            std::string pkg = a.np.group;
            if (pkg.empty()) pkg = "com/example";
            for (char& c : pkg)
                if (c == '.') c = '/';
            path = joinPath(joinPath(joinPath(joinPath(a.projectRoot, "src"),
                                              "main"),
                                     "java"),
                            pkg);
            path = joinPath(path, name);
        } else {
            path = joinPath(a.projectRoot, name);
        }
    } else {
        path = joinPath(a.projectRoot, name);
    }

    if (fileExists(path)) {
        a.log(LogLevel::Warn, "文件已存在，直接打开：" + baseName_(path));
        a.dlg = Dialog::None;
        loadFile(a, path);
        return;
    }

    // 内容给一个能编译通过的最小骨架，比空文件有用
    std::string body;
    if (path.size() > 5 && path.compare(path.size() - 5, 5, ".java") == 0) {
        std::string cls = baseName_(path);
        cls = cls.substr(0, cls.size() - 5);
        body = "public class " + cls + " {\n    \n}\n";
    }

    if (!writeFile(path, body)) {
        a.log(LogLevel::Error, "创建失败：" + path);
        a.dlg = Dialog::None;
        return;
    }

    a.log(LogLevel::Ok, "已创建 " + baseName_(path));
    a.dlg = Dialog::None;
    rebuildTree(a);
    loadFile(a, path);
}

}  // namespace app
