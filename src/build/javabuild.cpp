#include "build/javabuild.h"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <ctime>

#include "jdk/jdk.h"
#include "platform/platform.h"

namespace jbuild {

using spp::plat::dirExists;
using spp::plat::fileExists;
using spp::plat::joinPath;
using spp::plat::readFile;

// ---------------------------------------------------------------- 小工具

static std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && (unsigned char)s[a] <= ' ') a++;
    while (b > a && (unsigned char)s[b - 1] <= ' ') b--;
    return s.substr(a, b - a);
}

static bool endsWith(const std::string& s, const char* suf) {
    size_t n = strlen(suf);
    return s.size() >= n && s.compare(s.size() - n, n, suf) == 0;
}

static std::string lower(std::string s) {
    for (auto& c : s) c = (char)tolower((unsigned char)c);
    return s;
}

// ---------------------------------------------------------------- 语言
//
// 构建输出面板里有一部分行是我们自己拼的（「编译完成（0.0 秒）」这种），
// 另外一部分是 javac / mvn 自己吐的，那部分原样透传、绝不翻译。
// 这里只解决前者：一张极小的双语表，UI 切语言时通过 setLang 打过来。
// 不做成完整的 i18n 框架是因为需要翻译的句子就十来条，
// 上框架的成本比这张表高得多。

static bool g_en = false;

void setLang(bool english) { g_en = english; }
bool english() { return g_en; }

// 按 key 取中/英两句。key 用语义名，不用中文原文当 key
// （中文原文改一个字，英文那行就跟着对不上了）。
static std::string tr(const char* key) {
    struct Row { const char* k; const char* zh; const char* en; };
    static const Row rows[] = {
        {"notFoundTool",   "没找到 %s，改用 javac 直接编译",       "%s not found; falling back to javac"},
        {"noJavaFiles",    "找不到 .java 文件（在 %s 下）",         "No .java files found (under %s)"},
        {"compileDone",    "编译完成（%s）",                        "Compile finished (%s)"},
        {"outputDir",      "产物目录：%s",                          "Output dir: %s"},
        {"classFiles",     "class 文件：%s 个",                     "Class files: %s"},
        {"andMore",        "  …还有 %s 个",                         "  ...and %s more"},
        {"notCompiledYet", "还没编译过（找不到 %s），先编译一下",   "Not compiled yet (%s missing) - compile first"},
        {"noMainClass",    "找不到主类（没有一个类带 public static void main）",
                           "No main class (none has public static void main)"},
        // javac 命令行回显："> /path/to/javac.exe  (3 个源文件)"
        {"cmdSourceCount", "%s 个源文件",                            "%s source files"},
    };
    for (const auto& r : rows) {
        if (strcmp(r.k, key) == 0) return g_en ? r.en : r.zh;
    }
    return key;
}

// 把 tr() 拿到的模板里第一个 %s 换成 v。
static std::string tr1(const char* key, const std::string& v) {
    std::string s = tr(key);
    size_t p = s.find("%s");
    if (p != std::string::npos) s.replace(p, 2, v);
    return s;
}

// ---------------------------------------------------------------- 识别

std::string detectTool(const std::string& root) {
    if (fileExists(joinPath(root, "pom.xml"))) return "maven";
    if (fileExists(joinPath(root, "build.gradle")) ||
        fileExists(joinPath(root, "build.gradle.kts")))
        return "gradle";
    return "javac";
}

// 递归找 .java 文件
static void findJava(const std::string& dir, std::vector<std::string>& out,
                     int depth = 0) {
    if (depth > 24) return;
    for (const auto& e : spp::plat::listDir(dir)) {
        std::string full = joinPath(dir, e.name);
        if (e.isDir) {
            // 跳过构建产物和版本库
            std::string n = lower(e.name);
            if (n == "target" || n == "build" || n == "out" || n == ".git" ||
                n == "bin" || n == "node_modules")
                continue;
            findJava(full, out, depth + 1);
        } else if (endsWith(e.name, ".java")) {
            out.push_back(full);
        }
    }
}

// 从源码里抠出包名
static std::string packageOf(const std::string& file) {
    std::string s = readFile(file);
    size_t p = s.find("package");
    if (p == std::string::npos) return "";
    // 得是行首的 package
    size_t lineStart = s.rfind('\n', p);
    if (lineStart != std::string::npos) {
        std::string before = trim(s.substr(lineStart + 1, p - lineStart - 1));
        if (!before.empty()) return "";
    } else if (p != 0) {
        return "";
    }
    size_t e = s.find(';', p);
    if (e == std::string::npos) return "";
    std::string pkg = trim(s.substr(p + 7, e - p - 7));
    return pkg;
}

// 有没有 main 方法
static bool hasMain(const std::string& file) {
    std::string s = readFile(file);
    // 宽松匹配：static void main 且带 String[]
    size_t p = s.find("static");
    while (p != std::string::npos) {
        // 往后看一小段
        size_t seg = 200;
        std::string chunk = s.substr(p, std::min(seg, s.size() - p));
        if (chunk.find("void") != std::string::npos &&
            chunk.find("main") != std::string::npos &&
            chunk.find("String") != std::string::npos) {
            return true;
        }
        p = s.find("static", p + 1);
    }
    return false;
}

std::string guessMainClass(const std::string& root) {
    // pom 里写了就用它
    if (fileExists(joinPath(root, "pom.xml"))) {
        std::string s = readFile(joinPath(root, "pom.xml"));
        size_t p = s.find("<mainClass>");
        if (p != std::string::npos) {
            size_t e = s.find("</mainClass>", p);
            if (e != std::string::npos)
                return trim(s.substr(p + 11, e - p - 11));
        }
    }

    // 找源码里的 main
    std::vector<std::string> files;
    std::string src = joinPath(root, "src/main/java");
    if (dirExists(src)) findJava(src, files);
    if (files.empty()) {
        // 退而求其次，全项目找
        findJava(root, files);
    }

    for (const auto& f : files) {
        if (!hasMain(f)) continue;
        std::string cls = f;
        size_t s1 = cls.find_last_of("\\/");
        std::string base = (s1 == std::string::npos) ? cls : cls.substr(s1 + 1);
        if (endsWith(base, ".java")) base = base.substr(0, base.size() - 5);
        std::string pkg = packageOf(f);
        return pkg.empty() ? base : pkg + "." + base;
    }
    return "";
}

// 递归找 .class 文件，返回相对 out 目录的路径（带 .class 后缀）
static void findClasses(const std::string& dir, std::vector<std::string>& out,
                        const std::string& rel = "") {
    for (const auto& e : spp::plat::listDir(dir)) {
        std::string full = joinPath(dir, e.name);
        std::string r = rel.empty() ? e.name : rel + "/" + e.name;
        if (e.isDir) findClasses(full, out, r);
        else if (endsWith(e.name, ".class")) out.push_back(r);
    }
}

// ---------------------------------------------------------------- 诊断解析

// javac 输出形如：
//   D:\p\src\Main.java:5: error: ';' expected
//       int x = 1
//                ^
//   1 error
// 也有 warning: [deprecation] ...
void parseDiagnostics(const std::string& output, std::vector<Diagnostic>& out) {
    size_t pos = 0;
    while (pos < output.size()) {
        size_t e = output.find('\n', pos);
        if (e == std::string::npos) e = output.size();
        std::string line = output.substr(pos, e - pos);
        pos = e + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();

        // 找 ": error:" 或 ": warning:"
        size_t ep = line.find(": error:");
        size_t wp = line.find(": warning:");
        bool isErr = (ep != std::string::npos);
        bool isWarn = (wp != std::string::npos);
        if (!isErr && !isWarn) continue;

        size_t colon = isErr ? ep : wp;
        std::string head = line.substr(0, colon);       // "file:line" 或 "file:line:col"
        std::string msg = line.substr(colon + (isErr ? 8 : 10));
        msg = trim(msg);

        // 从 head 尾部抠行号列号
        int lineNo = 0, colNo = 0;
        size_t c2 = head.find_last_of(':');
        if (c2 != std::string::npos) {
            std::string tail = head.substr(c2 + 1);
            bool allDigit = !tail.empty();
            for (char c : tail)
                if (!isdigit((unsigned char)c)) { allDigit = false; break; }

            if (allDigit) {
                lineNo = atoi(tail.c_str());
                head = head.substr(0, c2);
                size_t c3 = head.find_last_of(':');
                if (c3 != std::string::npos) {
                    std::string t2 = head.substr(c3 + 1);
                    bool d2 = !t2.empty();
                    for (char c : t2)
                        if (!isdigit((unsigned char)c)) { d2 = false; break; }
                    if (d2) {
                        colNo = atoi(t2.c_str());
                        head = head.substr(0, c3);
                    }
                }
            }
        }

        Diagnostic d;
        d.level = isErr ? Level::Error : Level::Warning;
        d.file = head;
        d.line = lineNo;
        d.col = colNo;
        d.message = msg;
        out.push_back(d);
    }
}

// ---------------------------------------------------------------- 编译

static double nowSec() {
    return (double)clock() / (double)CLOCKS_PER_SEC;
}

// 用哪个 javac / java
static std::string javacOf(const Job& job) {
    if (!job.jdkHome.empty()) {
        std::string p = joinPath(joinPath(job.jdkHome, "bin"), "javac.exe");
        if (fileExists(p)) return p;
    }
    jdk::Info j = jdk::current();
    return j.ok ? j.javac : "javac";
}

static std::string javaOf(const Job& job) {
    if (!job.jdkHome.empty()) {
        std::string p = joinPath(joinPath(job.jdkHome, "bin"), "java.exe");
        if (fileExists(p)) return p;
    }
    jdk::Info j = jdk::current();
    return j.ok ? j.java : "java";
}

Result compile(const Job& job) {
    Result r;
    double t0 = nowSec();

    std::string tool = detectTool(job.projectRoot);
    r.tool = tool;

    // ---- Maven / Gradle
    if (tool == "maven" || tool == "gradle") {
        std::string exe = spp::plat::whichExe(tool == "maven" ? "mvn.cmd" : "gradle.bat");
        if (exe.empty())
            exe = spp::plat::whichExe(tool == "maven" ? "mvn" : "gradle");

        if (!exe.empty()) {
            // 用项目选定的 JDK：设 JAVA_HOME 传给它
            jdk::Info j = jdk::current();
            std::vector<std::string> args;
            if (tool == "maven") {
                args = {"-q", "-B", "compile"};
            } else {
                args = {"--console=plain", "-q", "build"};
            }
            r.rawOutput = "> " + exe + "\n";
            std::string o;
            r.exitCode = spp::plat::runCommand(exe, args, job.projectRoot, &o);
            r.rawOutput += o;
            parseDiagnostics(o, r.diags);
            r.ok = (r.exitCode == 0);
        } else {
            // 本机没装 —— 别卡住用户，退到 javac
            r.tool = "javac";
            r.rawOutput = tr1("notFoundTool", tool == "maven" ? "mvn" : "gradle") + "\n\n";
            // 落到下面的 javac 分支
            tool = "javac";
        }
    }

    // ---- javac
    if (r.tool == "javac" || tool == "javac") {
        r.tool = "javac";

        std::string src = job.sourceDir.empty()
                              ? joinPath(job.projectRoot, "src/main/java")
                              : job.sourceDir;
        if (!dirExists(src)) {
            // 没有 Maven 布局就用项目根
            src = job.projectRoot;
        }

        std::vector<std::string> files;
        findJava(src, files);

        if (files.empty()) {
            r.rawOutput += tr1("noJavaFiles", src);
            r.ok = false;
            r.exitCode = 1;
            r.seconds = nowSec() - t0;
            return r;
        }

        std::string out = job.outputDir.empty()
                              ? joinPath(job.projectRoot, "out")
                              : job.outputDir;
        spp::plat::makeDirs(out);

        std::vector<std::string> args;
        // 让 javac 用英文报错。中文报错在管道里是 GBK 编码，按 UTF-8 解出来
        // 全是问号，parseDiagnostics 也认不到「错误」。英文最稳。
        args.push_back("-J-Duser.language=en");
        args.push_back("-J-Duser.country=US");
        args.push_back("-encoding");
        args.push_back("UTF-8");
        args.push_back("-d");
        args.push_back(out);

        // 用哪一个 release。项目里 pom 写了就以它为准，
        // 否则用当前 JDK 自己的版本。
        jdk::Info j = jdk::current();
        if (!job.jdkHome.empty()) {
            for (const auto& i : jdk::all())
                if (i.home == job.jdkHome) j = i;
        }
        if (j.ok && j.major > 0) {
            args.push_back("--release");
            args.push_back(std::to_string(j.major));
        }

        if (!job.classpath.empty()) {
            std::string cp;
            for (size_t i = 0; i < job.classpath.size(); i++) {
                if (i) cp += ";";
                cp += job.classpath[i];
            }
            args.push_back("-cp");
            args.push_back(cp);
        }
        if (job.verbose) args.push_back("-Xlint:all");

        args.insert(args.end(), files.begin(), files.end());

        std::string jc = javacOf(job);
        r.rawOutput += "> " + jc + "  (" +
                       tr1("cmdSourceCount", std::to_string(files.size())) +
                       ")\n";
        std::string o;
        r.exitCode = spp::plat::runCommand(jc, args, job.projectRoot, &o);
        r.rawOutput += o;
        parseDiagnostics(o, r.diags);
        r.ok = (r.exitCode == 0);
    }

    for (const auto& d : r.diags) {
        if (d.level == Level::Error) r.errorCount++;
        else if (d.level == Level::Warning) r.warnCount++;
    }
    if (r.ok) {
        // javac 成功时一个字都不打印，光写「编译完成」等于没输出。
        // 把产物列出来，用户才能确认到底编出了什么、放哪儿了。
        std::string outDir = job.outputDir.empty()
                                 ? joinPath(job.projectRoot, "out")
                                 : job.outputDir;
        std::vector<std::string> cls;
        findClasses(outDir, cls);

        char secs[32];
        snprintf(secs, sizeof(secs), "%.2f s", r.seconds);
        r.rawOutput += "\n" + tr1("compileDone", secs) + "\n";
        r.rawOutput += tr1("outputDir", outDir) + "\n";
        r.rawOutput += tr1("classFiles", std::to_string(cls.size())) + "\n";
        for (size_t i = 0; i < cls.size() && i < 20; i++)
            r.rawOutput += "  " + cls[i] + "\n";
        if (cls.size() > 20)
            r.rawOutput += tr1("andMore", std::to_string(cls.size() - 20)) + "\n";
    }

    r.seconds = nowSec() - t0;
    return r;
}

// ---------------------------------------------------------------- 运行

Result run(const Job& job, const std::vector<std::string>& args) {
    Result r;
    double t0 = nowSec();
    r.tool = "java";

    std::string out = job.outputDir.empty()
                          ? joinPath(job.projectRoot, "out")
                          : job.outputDir;
    if (!dirExists(out)) {
        r.rawOutput = tr1("notCompiledYet", out);
        r.ok = false;
        r.exitCode = 1;
        r.seconds = nowSec() - t0;
        return r;
    }

    std::string main = job.mainClass;
    if (main.empty()) main = guessMainClass(job.projectRoot);
    if (main.empty()) {
        r.rawOutput = tr("noMainClass");
        r.ok = false;
        r.exitCode = 1;
        r.seconds = nowSec() - t0;
        return r;
    }

    std::string cp = out;
    for (const auto& c : job.classpath) cp += ";" + c;

    std::vector<std::string> a;
    a.push_back("-Dfile.encoding=UTF-8");
    a.push_back("-cp");
    a.push_back(cp);
    a.push_back(main);
    for (const auto& x : args) a.push_back(x);

    std::string jv = javaOf(job);
    r.rawOutput = "> " + jv + " " + main + "\n\n";
    std::string o;
    r.exitCode = spp::plat::runCommand(jv, a, job.projectRoot, &o);
    r.rawOutput += o;
    r.ok = (r.exitCode == 0);

    r.seconds = nowSec() - t0;
    return r;
}

}  // namespace jbuild
