// api.cpp —— C 接口的实现
//
// 见 api.h 的说明。这里只做「把 C++ 的返回值编成 JSON」的活，
// 业务逻辑全在 jdk/ jar/ libs/ project/ build/ jlex/ 这些模块里，
// 一行都不搬过来。
#include "core/api.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

#include "build/javabuild.h"
#include "icons/iconlib.h"
#include "jar/jar.h"
#include "jdk/jdk.h"
#include "jlex/javalex.h"
#include "libs/jlibs.h"
#include "platform/platform.h"
#include "project/javaproject.h"

using spp::plat::dirExists;
using spp::plat::fileExists;
using spp::plat::joinPath;
using spp::plat::listDir;
using spp::plat::makeDirs;
using spp::plat::readFile;
using spp::plat::writeFile;

namespace {

// ---------------------------------------------------------------- 全局

std::string g_appData;
std::string g_exeDir;

void (*g_sink)(const char*, const char*, void*) = nullptr;
void* g_sinkUd = nullptr;

// ---------------------------------------------------------------- JSON 输出
//
// 只写不读（进参都是扁平的），所以不用实现解析器。
// 一个极简的构建器就够，没必要为这点东西引第三方库。

std::string jesc(const std::string& s) {
    std::string o;
    o.reserve(s.size() + 8);
    for (unsigned char c : s) {
        switch (c) {
            case '"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default:
                if (c < 0x20) {
                    char b[8];
                    snprintf(b, sizeof(b), "\\u%04x", c);
                    o += b;
                } else {
                    o += (char)c;
                }
        }
    }
    return o;
}

std::string jstr(const std::string& s) { return "\"" + jesc(s) + "\""; }

// ["a","b"]
std::string jarr(const std::vector<std::string>& v) {
    std::string o = "[";
    for (size_t i = 0; i < v.size(); i++) {
        if (i) o += ",";
        o += jstr(v[i]);
    }
    return o + "]";
}

// 把 "a;b;c" 切成列表。空串得到空列表。
std::vector<std::string> splitSemi(const std::string& s) {
    std::vector<std::string> out;
    size_t p = 0;
    while (p <= s.size()) {
        size_t q = s.find(';', p);
        if (q == std::string::npos) q = s.size();
        std::string one = s.substr(p, q - p);
        if (!one.empty()) out.push_back(one);
        if (q == s.size()) break;
        p = q + 1;
    }
    return out;
}

char* dup(const std::string& s) {
    char* p = (char*)malloc(s.size() + 1);
    if (!p) return nullptr;
    memcpy(p, s.c_str(), s.size() + 1);
    return p;
}

const char* safe(const char* s) { return s ? s : ""; }

// ---------------------------------------------------------------- 目录树

bool isJunk(const std::string& name) {
    static const char* kJunk[] = {".git",       "target", "build",
                                  "out",        "bin",    ".idea",
                                  ".gradle",    ".vscode", "node_modules",
                                  ".javastudio"};
    for (const char* j : kJunk)
        if (name == j) return true;
    return false;
}

// 目录在前、文件在后，各自按名字排 —— 和 IDEA 的项目树一致。
void buildTree(const std::string& path, std::string& out, int depth) {
    if (depth > 12) return;    // 防环，正常项目到不了这么深

    auto es = listDir(path);
    std::vector<spp::plat::DirEntry> dirs, files;
    for (auto& e : es) {
        if (isJunk(e.name)) continue;
        (e.isDir ? dirs : files).push_back(e);
    }
    auto byName = [](const spp::plat::DirEntry& a,
                     const spp::plat::DirEntry& b) {
        return a.name < b.name;
    };
    std::sort(dirs.begin(), dirs.end(), byName);
    std::sort(files.begin(), files.end(), byName);

    bool first = true;
    auto emit = [&](const spp::plat::DirEntry& e, bool isDir) {
        if (!first) out += ",";
        first = false;
        std::string full = joinPath(path, e.name);
        out += "{\"name\":" + jstr(e.name);
        out += ",\"path\":" + jstr(full);
        out += std::string(",\"dir\":") + (isDir ? "true" : "false");
        if (isDir) {
            out += ",\"children\":[";
            size_t mark = out.size();
            buildTree(full, out, depth + 1);
            // 空目录也要留个 []，上面已经写了 '['
            out += "]";
            (void)mark;
        }
        out += "}";
    };
    for (auto& d : dirs) emit(d, true);
    for (auto& f : files) emit(f, false);
}

// ---------------------------------------------------------------- 设置 / 最近

std::string settingsPath() { return joinPath(g_appData, "settings.txt"); }
std::string sessionPath() { return joinPath(g_appData, "session.txt"); }

// 用记事本改过 settings.txt / session.txt 的话，行尾会带 \r。
// 不去掉的话，拿到的路径结尾多一个看不见的字符，Directory.Exists 直接判否，
// 表现为「明明填了上次打开的项目，主页上就是不出来」。
std::string chomp(std::string s) {
    while (!s.empty() && (s.back() == '\r' || s.back() == '\n' || s.back() == ' '))
        s.pop_back();
    return s;
}

}  // namespace

// ================================================================ 生命周期

extern "C" void js_init(const char* appDataDir, const char* exeDir) {
    g_appData = safe(appDataDir);
    g_exeDir = safe(exeDir);
    if (g_appData.empty()) g_appData = spp::plat::appDataDir("JavaStudio");
    makeDirs(g_appData);
    // 关键：让平台层的 appDataDir() 也返回这个目录。
    // 不设这一句的话，内核里那些直接调 spp::plat::appDataDir("JavaStudio") 的模块
    // （最近项目、JDK 偏好、第三方库缓存）仍然会把文件写到 C 盘的 %APPDATA%，
    // 跟这里读写的不是同一份 —— 表现为「打开过项目，首页却说没有项目」。
    spp::plat::setAppDataOverride(g_appData);
    spp::plat::logInit(joinPath(g_appData, "javastudio.log"));
    jdk::loadPreference();
}

extern "C" void js_shutdown(void) { jdk::savePreference(); }

extern "C" void js_free(char* s) { free(s); }

extern "C" void js_set_log_sink(void* cb, void* ud) {
    g_sink = (void (*)(const char*, const char*, void*))cb;
    g_sinkUd = ud;
    // 让平台层的 logWrite 也往这边发一份
    spp::plat::setLogSink(g_sink, g_sinkUd);
}

// ================================================================ JDK

extern "C" char* js_jdk_list(void) {
    auto v = jdk::all();
    if (v.empty()) v = jdk::scan();
    std::string o = "[";
    for (size_t i = 0; i < v.size(); i++) {
        if (i) o += ",";
        const auto& j = v[i];
        // 一个条目里塞了 3 条路径（home / javac / java 之外还有 label），
        // 路径长的机器上一不留神就截断成一截坏 JSON。给宽一点。
        char buf[1024];
        snprintf(buf, sizeof(buf),
                 "{\"major\":%d,\"version\":%s,\"vendor\":%s,\"home\":%s,"
                 "\"label\":%s,\"ok\":%s,\"builtin\":%s}",
                 j.major, jstr(j.version).c_str(), jstr(j.vendor).c_str(),
                 jstr(j.home).c_str(), jstr(j.label()).c_str(),
                 j.ok ? "true" : "false", j.builtin ? "true" : "false");
        o += buf;
    }
    return dup(o + "]");
}

extern "C" char* js_jdk_current(void) {
    jdk::Info j = jdk::current();
    std::string o = "{\"ok\":";
    o += j.ok ? "true" : "false";
    o += ",\"major\":" + std::to_string(j.major);
    o += ",\"home\":" + jstr(j.home);
    o += ",\"label\":" + jstr(j.label());
    o += ",\"builtin\":" + std::string(j.builtin ? "true" : "false");
    return dup(o + "}");
}

extern "C" int js_jdk_select(int major) { return jdk::select(major) ? 1 : 0; }

extern "C" void js_jdk_rescan(void) { jdk::refresh(); }

// ================================================================ 项目

extern "C" char* js_open_project(const char* root) {
    std::string r = safe(root);
    std::string o = "{\"ok\":";
    if (!dirExists(r)) {
        return dup("{\"ok\":false,\"error\":\"目录不存在：" + jesc(r) + "\"}");
    }
    o += "true,\"path\":" + jstr(r);
    o += ",\"name\":" + jstr(spp::plat::baseName(r));
    o += ",\"tool\":" + jstr(jbuild::detectTool(r));
    o += ",\"mainClass\":" + jstr(jbuild::guessMainClass(r));

    // 第三方库：libs.txt 里勾的 + pom.xml 里声明的，合起来
    std::vector<std::string> ids = jlibs::loadSelection(r);
    for (const auto& coord : jlibs::pomCoords(r)) {
        for (const auto& e : jlibs::catalog()) {
            std::string pre = e.group + ":" + e.artifact;
            if (coord.rfind(pre, 0) == 0) {
                bool dup2 = false;
                for (const auto& x : ids)
                    if (x == e.id) dup2 = true;
                if (!dup2) ids.push_back(e.id);
                break;
            }
        }
    }
    auto res = jlibs::resolve(r, ids);
    o += ",\"libs\":{\"jars\":" + jarr(res.jars);
    o += ",\"ready\":" + jarr(res.readyIds);
    o += ",\"missing\":" + jarr(res.missingIds) + "}";

    o += ",\"tree\":[";
    buildTree(r, o, 0);
    o += "]}";
    return dup(o);
}

extern "C" char* js_dir_tree(const char* root) {
    std::string o = "[";
    buildTree(safe(root), o, 0);
    return dup(o + "]");
}

extern "C" char* js_validate_project(const char* name, const char* group,
                                     const char* artifact) {
    std::string e = javaproj::validateName(safe(name));
    if (e.empty()) e = javaproj::validatePackage(safe(group), safe(artifact));
    return dup(jstr(e));
}

extern "C" char* js_create_project(const char* parentDir, const char* name,
                                   const char* group, const char* artifact,
                                   const char* version,
                                   const char* buildSystem,
                                   const char* jdkRelease, int addSampleCode,
                                   int createGitRepo) {
    javaproj::Options opt;
    opt.name = safe(name);
    opt.dir = joinPath(safe(parentDir), opt.name);
    opt.group = safe(group);
    opt.artifact = safe(artifact);
    opt.version = safe(version);
    if (opt.version.empty()) opt.version = "1.0.0";
    opt.kind = javaproj::Kind::Console;

    std::string bs = safe(buildSystem);
    opt.tool = (bs == "maven")    ? javaproj::BuildTool::Maven
               : (bs == "gradle") ? javaproj::BuildTool::Gradle
                                  : javaproj::BuildTool::None;

    std::string rel = safe(jdkRelease);
    opt.jdkRelease = rel.empty() ? "17" : rel;

    opt.addSampleCode = addSampleCode != 0;
    opt.createGitRepo = createGitRepo != 0;

    javaproj::Result r = javaproj::create(opt);

    std::string o = "{\"ok\":";
    o += r.ok ? "true" : "false";
    o += ",\"dir\":" + jstr(opt.dir);
    o += ",\"error\":" + jstr(r.error);
    o += ",\"created\":" + jarr(r.created);
    return dup(o + "}");
}

extern "C" char* js_default_projects_dir(void) {
    std::string d = joinPath(g_exeDir.empty() ? spp::plat::executableDir()
                                              : g_exeDir,
                             "item");
    makeDirs(d);
    return dup(jstr(d));
}

extern "C" char* js_guess_main(const char* root) {
    return dup(jstr(jbuild::guessMainClass(safe(root))));
}

// ================================================================ 文件

extern "C" char* js_read_file(const char* path) {
    std::string p = safe(path);
    if (!fileExists(p))
        return dup("{\"ok\":false,\"error\":\"文件不存在\"}");
    std::string t = readFile(p);
    for (size_t i = 0; i < t.size() && i < 8192; i++) {
        if (t[i] == '\0')
            return dup("{\"ok\":false,\"error\":\"这是二进制文件\"}");
    }
    std::string o = "{\"ok\":true,\"text\":" + jstr(t);
    o += ",\"path\":" + jstr(p) + "}";
    return dup(o);
}

extern "C" char* js_write_file(const char* path, const char* text) {
    bool ok = writeFile(safe(path), safe(text));
    std::string o = "{\"ok\":";
    o += ok ? "true" : "false";
    o += ",\"path\":" + jstr(safe(path)) + "}";
    return dup(o);
}

extern "C" char* js_list_dir(const char* path) {
    auto es = listDir(safe(path));
    std::string o = "[";
    bool first = true;
    for (auto& e : es) {
        if (!first) o += ",";
        first = false;
        o += "{\"name\":" + jstr(e.name);
        o += ",\"path\":" + jstr(joinPath(safe(path), e.name));
        o += std::string(",\"dir\":") + (e.isDir ? "true" : "false") + "}";
    }
    return dup(o + "]");
}

// ================================================================ 构建

static std::string diagJson(const jbuild::Result& r) {
    std::string o = "{\"ok\":";
    o += r.ok ? "true" : "false";
    o += ",\"exitCode\":" + std::to_string(r.exitCode);
    o += ",\"tool\":" + jstr(r.tool);
    o += ",\"output\":" + jstr(r.rawOutput);
    o += ",\"errorCount\":" + std::to_string(r.errorCount);
    o += ",\"warnCount\":" + std::to_string(r.warnCount);
    char secs[32];
    snprintf(secs, sizeof(secs), "%.2f", r.seconds);
    o += ",\"seconds\":" + std::string(secs);
    o += ",\"diags\":[";
    for (size_t i = 0; i < r.diags.size(); i++) {
        const auto& d = r.diags[i];
        if (i) o += ",";
        const char* lv = d.level == jbuild::Level::Error    ? "error"
                         : d.level == jbuild::Level::Warning ? "warning"
                                                             : "info";
        o += "{\"level\":\"" + std::string(lv) + "\"";
        o += ",\"file\":" + jstr(d.file);
        o += ",\"line\":" + std::to_string(d.line);
        o += ",\"col\":" + std::to_string(d.col);
        o += ",\"message\":" + jstr(d.message) + "}";
    }
    return o + "]}";
}

extern "C" char* js_compile(const char* root, const char* jdkHome,
                            const char* classpath, int verbose) {
    jbuild::Job job;
    job.projectRoot = safe(root);
    job.jdkHome = safe(jdkHome);
    job.classpath = splitSemi(safe(classpath));
    job.verbose = verbose != 0;
    return dup(diagJson(jbuild::compile(job)));
}

extern "C" char* js_run(const char* root, const char* jdkHome,
                        const char* mainClass, const char* classpath) {
    jbuild::Job job;
    job.projectRoot = safe(root);
    job.jdkHome = safe(jdkHome);
    job.mainClass = safe(mainClass);
    job.classpath = splitSemi(safe(classpath));
    return dup(diagJson(jbuild::run(job, {})));
}

// 括号配平检查。
//
// 不能傻数括号 —— 字符串里的 "(" 和注释里的 "{" 都是正常的。
// 所以走一遍词法器，把字符串/字符/注释这几类跳掉再数。
extern "C" char* js_check(const char* root) {
    std::string r = safe(root);
    std::string srcDir = joinPath(r, "src");
    if (!dirExists(srcDir)) srcDir = r;

    std::vector<std::string> files;
    std::vector<std::string> stack{srcDir};
    while (!stack.empty()) {
        std::string cur = stack.back();
        stack.pop_back();
        for (auto& e : listDir(cur)) {
            std::string full = joinPath(cur, e.name);
            if (e.isDir) {
                if (!isJunk(e.name)) stack.push_back(full);
            } else if (e.name.size() > 5 &&
                       e.name.substr(e.name.size() - 5) == ".java") {
                files.push_back(full);
            }
        }
    }

    std::string o = "{\"ok\":true,\"diags\":[";
    bool first = true;
    int total = 0;

    for (const auto& f : files) {
        std::string text = readFile(f);
        std::vector<std::string> lines;
        {
            std::string cur;
            for (char ch : text) {
                if (ch == '\n') {
                    if (!cur.empty() && cur.back() == '\r') cur.pop_back();
                    lines.push_back(cur);
                    cur.clear();
                } else {
                    cur += ch;
                }
            }
            if (!cur.empty()) lines.push_back(cur);
        }

        bool inBlock = false, inText = false;
        std::vector<std::pair<char, int>> st;    // 括号 + 行号
        for (size_t li = 0; li < lines.size(); li++) {
            auto spans = jlex::lexLine(lines[li], inBlock, inText);
            size_t pos = 0;
            for (const auto& sp : spans) {
                bool skip = (sp.cat == jlex::Cat::StringLit ||
                             sp.cat == jlex::Cat::CharLit ||
                             sp.cat == jlex::Cat::Comment ||
                             sp.cat == jlex::Cat::Javadoc);
                for (int k = 0; k < sp.len; k++) {
                    char c = lines[li][pos + k];
                    if (skip) continue;
                    if (c == '(' || c == '[' || c == '{') {
                        st.push_back({c, (int)li + 1});
                    } else if (c == ')' || c == ']' || c == '}') {
                        char want = (c == ')') ? '(' : (c == ']') ? '[' : '{';
                        if (!st.empty() && st.back().first == want) {
                            st.pop_back();
                        } else {
                            if (!first) o += ",";
                            first = false;
                            o += "{\"level\":\"error\",\"file\":" + jstr(f);
                            o += ",\"line\":" + std::to_string(li + 1);
                            o += ",\"col\":" + std::to_string(pos + k + 1);
                            o += ",\"message\":\"多出来的 " +
                                 std::string(1, c) + "\"}";
                            total++;
                        }
                    }
                }
                pos += sp.len;
            }
        }
        for (const auto& s : st) {
            char want = (s.first == '(')   ? ')'
                        : (s.first == '[') ? ']'
                                           : '}';
            if (!first) o += ",";
            first = false;
            o += "{\"level\":\"error\",\"file\":" + jstr(f);
            o += ",\"line\":" + std::to_string(s.second);
            o += ",\"col\":1,\"message\":\"这里的 " + std::string(1, s.first) +
                 " 没有闭合，少了 " + std::string(1, want) + "\"}";
            total++;
        }
    }

    char buf[64];
    snprintf(buf, sizeof(buf), "],\"errorCount\":%d}", total);
    return dup(o + buf);
}

// ================================================================ 第三方库

extern "C" char* js_libs_catalog(void) {
    std::string o = "[";
    bool first = true;
    for (const auto& e : jlibs::catalog()) {
        if (!first) o += ",";
        first = false;
        o += "{\"id\":" + jstr(e.id);
        o += ",\"name\":" + jstr(e.name);
        o += ",\"group\":" + jstr(e.group);
        o += ",\"artifact\":" + jstr(e.artifact);
        o += ",\"version\":" + jstr(e.version);
        o += ",\"desc\":" + jstr(e.desc);
        o += ",\"category\":" + jstr(e.category);
        o += std::string(",\"cached\":") +
             (jlibs::findJar(e, "").empty() ? "false" : "true");
        o += "}";
    }
    return dup(o + "]");
}

extern "C" char* js_libs_selection(const char* root) {
    auto ids = jlibs::loadSelection(safe(root));
    std::string joined;
    for (size_t i = 0; i < ids.size(); i++) {
        if (i) joined += ";";
        joined += ids[i];
    }
    return dup(jstr(joined));
}

extern "C" int js_libs_save(const char* root, const char* idsSemicolon) {
    return jlibs::saveSelection(safe(root), splitSemi(safe(idsSemicolon))) ? 1
                                                                           : 0;
}

extern "C" char* js_libs_resolve(const char* root, const char* idsSemicolon) {
    auto res = jlibs::resolve(safe(root), splitSemi(safe(idsSemicolon)));
    std::string o = "{\"jars\":" + jarr(res.jars);
    o += ",\"ready\":" + jarr(res.readyIds);
    o += ",\"missing\":" + jarr(res.missingIds) + "}";
    return dup(o);
}

// 带不带进度，最后走的都是这一份：只是回调为不为空的区别。
// id 也可以是完整坐标 —— 自定义依赖没有 id，只有 group:artifact:version。
static char* doDownload(const char* id, jlibs::DownloadProgress prog,
                        void* progUser) {
    const jlibs::Entry* e = jlibs::find(safe(id));
    jlibs::Entry fromCoord;
    if (!e) {
        if (!jlibs::parseCoord(safe(id), fromCoord)) {
            return dup(std::string("{\"ok\":false,\"error\":\"") +
                       jstr(std::string("目录里没有「") + safe(id) +
                            "」，它也不是一个 group:artifact:version 坐标") +
                       "}");
        }
        e = &fromCoord;
    }
    std::string err, saved;
    bool ok = jlibs::download(*e, &err, &saved, prog, progUser);
    std::string o = std::string("{\"ok\":") + (ok ? "true" : "false");
    o += ",\"id\":" + jstr(e->id);
    o += ",\"path\":" + jstr(saved);
    o += ",\"error\":" + jstr(err) + "}";
    return dup(o);
}

extern "C" char* js_libs_download(const char* id) {
    return doDownload(id, nullptr, nullptr);
}

// C 那边回调返回 int（0 = 取消），平台层这边返回 bool（false = 取消）。
// 两个函数类型只差返回值，不能直接强转（编译器会警告「转成不兼容的函数类型」），
// 所以垫一层：把 C 的回调和它的 user data 包成一小坨，跳板函数里再解开。
// 这一坨活在 doDownload 调用期间（下载是同步的），不需要长期存储。
struct ProgressBridge {
    js_download_progress cb;
    void* user;
};

static bool progressTrampoline(unsigned long long done,
                               unsigned long long total, void* self) {
    auto* b = (ProgressBridge*)self;
    return b->cb ? b->cb(done, total, b->user) != 0 : true;
}

extern "C" char* js_libs_download_ex(const char* id, js_download_progress cb,
                                     void* user) {
    ProgressBridge bridge{cb, user};
    return doDownload(id, cb ? progressTrampoline : nullptr,
                      cb ? (void*)&bridge : nullptr);
}

// ---------------------------------------------------------------- 仓库查询

extern "C" char* js_libs_search(const char* query) {
    std::string err;
    auto hits = jlibs::search(safe(query), &err);
    std::string o = std::string("{\"ok\":") + (err.empty() ? "true" : "false");
    o += ",\"error\":" + jstr(err);
    o += ",\"hits\":[";
    bool first = true;
    for (const auto& h : hits) {
        if (!first) o += ",";
        first = false;
        o += "{\"group\":" + jstr(h.group);
        o += ",\"artifact\":" + jstr(h.artifact);
        o += ",\"version\":" + jstr(h.version) + "}";
    }
    return dup(o + "]}");
}

extern "C" char* js_libs_versions(const char* group, const char* artifact) {
    std::string err;
    auto vs = jlibs::versions(safe(group), safe(artifact), &err);
    std::string o = std::string("{\"ok\":") + (err.empty() ? "true" : "false");
    o += ",\"error\":" + jstr(err);
    o += ",\"versions\":" + jarr(vs) + "}";
    return dup(o);
}

// ---------------------------------------------------------------- 本地缓存

extern "C" char* js_libs_cache_list(void) {
    auto items = jlibs::cacheList();
    std::string o = "{\"items\":[";
    bool first = true;
    for (const auto& c : items) {
        if (!first) o += ",";
        first = false;
        o += "{\"coord\":" + jstr(c.coord);
        o += ",\"path\":" + jstr(c.path);
        o += ",\"size\":" + std::to_string((unsigned long long)c.size) + "}";
    }
    return dup(o + "]}");
}

extern "C" int js_libs_cache_remove(const char* coord) {
    return jlibs::cacheRemove(safe(coord)) ? 1 : 0;
}

// ---------------------------------------------------------------- pom

extern "C" char* js_libs_pom_coords(const char* root) {
    return dup(jarr(jlibs::pomCoords(safe(root))));
}

extern "C" int js_libs_pom_exists(const char* root) {
    return jlibs::pomExists(safe(root)) ? 1 : 0;
}

extern "C" int js_libs_pom_add(const char* root, const char* coord) {
    jlibs::Entry e;
    if (!jlibs::parseCoord(safe(coord), e)) return 0;
    return jlibs::pomAdd(safe(root), e) ? 1 : 0;
}

extern "C" int js_libs_pom_remove(const char* root, const char* group,
                                  const char* artifact) {
    return jlibs::pomRemove(safe(root), safe(group), safe(artifact)) ? 1 : 0;
}

// ================================================================ jar

extern "C" char* js_jar_info(const char* path) {
    jarpack::Info j = jarpack::read(safe(path));
    std::string o = "{\"ok\":";
    o += j.ok ? "true" : "false";
    o += ",\"path\":" + jstr(j.path);
    o += ",\"error\":" + jstr(j.error);
    o += ",\"mainClass\":" + jstr(j.mainClass);
    o += ",\"classCount\":" + std::to_string(j.classCount);
    o += ",\"resourceCount\":" + std::to_string(j.resourceCount);
    o += ",\"javaVersion\":" + jstr(j.javaVersionName());
    o += ",\"entries\":[";
    bool first = true;
    for (const auto& e : j.entries) {
        if (!first) o += ",";
        first = false;
        o += "{\"name\":" + jstr(e.name);
        o += ",\"size\":" + std::to_string((unsigned long long)e.size);
        o += std::string(",\"dir\":") + (e.isDir ? "true" : "false") + "}";
    }
    return dup(o + "]}");
}

// ================================================================ 图标

extern "C" char* js_icons_all(const char* iconsDir) {
    spp::icons::Library lib(safe(iconsDir));
    int n = lib.load();
    if (n <= 0) return dup("{}");

    std::string o = "{";
    bool firstIcon = true;
    for (const auto& name : lib.names()) {
        if (!firstIcon) o += ",";
        firstIcon = false;
        o += jstr(name) + ":[";
        const auto& shapes = lib.shapes(name);
        bool firstShape = true;
        for (const auto& s : shapes) {
            if (!firstShape) o += ",";
            firstShape = false;
            const char* kind =
                s.kind == spp::icons::ShapeKind::Path        ? "path"
                : s.kind == spp::icons::ShapeKind::Polyline  ? "polyline"
                : s.kind == spp::icons::ShapeKind::Polygon   ? "polygon"
                : s.kind == spp::icons::ShapeKind::Line      ? "line"
                : s.kind == spp::icons::ShapeKind::Circle    ? "circle"
                                                             : "rect";
            o += "{\"kind\":\"" + std::string(kind) + "\"";
            if (s.kind == spp::icons::ShapeKind::Circle) {
                char b[128];
                snprintf(b, sizeof(b), ",\"cx\":%.3f,\"cy\":%.3f,\"r\":%.3f",
                         s.cx, s.cy, s.r);
                o += b;
            } else if (s.kind == spp::icons::ShapeKind::Rect) {
                char b[160];
                snprintf(b, sizeof(b),
                         ",\"x\":%.3f,\"y\":%.3f,\"w\":%.3f,\"h\":%.3f,"
                         "\"rx\":%.3f",
                         s.cx, s.cy, s.rw, s.rh, s.rx);
                o += b;
            } else {
                o += std::string(",\"closed\":") +
                     (s.closed ? "true" : "false");
                o += ",\"pts\":[";
                for (size_t i = 0; i < s.pts.size(); i++) {
                    if (i) o += ",";
                    char b[32];
                    snprintf(b, sizeof(b), "%.3f", s.pts[i]);
                    o += b;
                }
                o += "]";
            }
            o += "}";
        }
        o += "]";
    }
    return dup(o + "}");
}

// ================================================================ 设置 / 最近

extern "C" void js_build_set_lang(const char* lang) {
    // 注意：safe() 返回的是 const char*，直接 == "en" 比的是指针（编译器也警告），
    // 必须显式按内容比。
    jbuild::setLang(std::string(safe(lang)) == "en");
}

extern "C" char* js_settings_get(const char* key) {
    std::string k = safe(key);
    std::string txt = readFile(settingsPath());
    size_t p = 0;
    while (p <= txt.size()) {
        size_t q = txt.find('\n', p);
        if (q == std::string::npos) q = txt.size();
        std::string line = chomp(txt.substr(p, q - p));
        size_t eq = line.find('=');
        if (eq != std::string::npos && chomp(line.substr(0, eq)) == k)
            return dup(jstr(chomp(line.substr(eq + 1))));
        if (q == txt.size()) break;
        p = q + 1;
    }
    return dup(jstr(""));
}

extern "C" void js_settings_set(const char* key, const char* value) {
    std::string k = safe(key), v = safe(value);
    std::vector<std::string> lines;
    std::string txt = readFile(settingsPath());
    size_t p = 0;
    bool hit = false;
    while (p <= txt.size()) {
        size_t q = txt.find('\n', p);
        if (q == std::string::npos) q = txt.size();
        std::string line = chomp(txt.substr(p, q - p));
        size_t eq = line.find('=');
        if (eq != std::string::npos && chomp(line.substr(0, eq)) == k) {
            lines.push_back(k + "=" + v);
            hit = true;
        } else if (!line.empty()) {
            lines.push_back(line);
        }
        if (q == txt.size()) break;
        p = q + 1;
    }
    if (!hit) lines.push_back(k + "=" + v);
    std::string out;
    for (auto& l : lines) out += l + "\n";
    writeFile(settingsPath(), out);
}

extern "C" char* js_recent(void) {
    std::vector<std::string> out;
    std::string txt = readFile(sessionPath());
    size_t p = 0;
    while (p <= txt.size()) {
        size_t q = txt.find('\n', p);
        if (q == std::string::npos) q = txt.size();
        std::string line = chomp(txt.substr(p, q - p));
        if (!line.empty() && line[0] != '#') out.push_back(line);
        if (q == txt.size()) break;
        p = q + 1;
    }
    return dup(jarr(out));
}

extern "C" void js_recent_remove(const char* root) {
    std::string r = spp::plat::normalizePath(safe(root));
    if (r.empty()) return;
    std::vector<std::string> keep;
    std::string txt = readFile(sessionPath());
    size_t p = 0;
    while (p <= txt.size()) {
        size_t q = txt.find('\n', p);
        if (q == std::string::npos) q = txt.size();
        std::string line = chomp(txt.substr(p, q - p));
        // 两边都走完整规范化：只去结尾斜杠不够，
        // D:/a 和 D:\a（甚至 D://a）会被当成三个不同项目，删掉一个剩下俩还在。
        if (!line.empty() && line[0] != '#' &&
            spp::plat::normalizePath(line) != r)
            keep.push_back(line);
        if (q == txt.size()) break;
        p = q + 1;
    }
    std::string out = "# Java Studio session\n";
    for (auto& l : keep) out += l + "\n";
    writeFile(sessionPath(), out);
}

extern "C" void js_recent_add(const char* root) {
    // 规范化后再入表。否则 D://a//b 和 D:\a\b 会被当成两个项目各存一条，
    // 首页列表里同一个项目出现两行。
    std::string r = spp::plat::normalizePath(safe(root));
    if (r.empty()) return;
    std::vector<std::string> list;
    {
        // 直接读，省一个解析函数
        std::string txt = readFile(sessionPath());
        size_t p = 0;
        while (p <= txt.size()) {
            size_t q = txt.find('\n', p);
            if (q == std::string::npos) q = txt.size();
            std::string line = chomp(txt.substr(p, q - p));
            if (!line.empty() && line[0] != '#' &&
                spp::plat::normalizePath(line) != r)
                list.push_back(spp::plat::normalizePath(line));
            if (q == txt.size()) break;
            p = q + 1;
        }
    }
    std::string out = "# Java Studio session\n" + r + "\n";
    for (auto& l : list) {
        out += l + "\n";
        if (out.size() > 4000) break;    // 别让这个文件无限长
    }
    writeFile(sessionPath(), out);
}

extern "C" void js_recent_clear(void) {
    writeFile(sessionPath(), std::string("# Java Studio session\n"));
}

// ================================================================ 杂项

extern "C" long long js_free_space(const char* path) {
    return (long long)spp::plat::freeSpaceOf(safe(path));
}

// 版本串由 mk.py 用 -DJS_VERSION 传进来（源头是包根的 VERSION 文件）。
// 这里那行 #ifndef 只是兜底：万一有人直接用别的命令编这一个文件，
// 至少还能编过，不会报「未声明的标识符」。真正的来源永远是构建脚本。
#ifndef JS_VERSION
#define JS_VERSION "0.1.0-Preview.1"
#endif

extern "C" char* js_version(void) {
    return dup(std::string("{\"version\":") + jstr(JS_VERSION) + "}");
}
