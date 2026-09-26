// javabuild.h —— 编译和运行 Java
//
// 三条路，按项目里有什么自动选：
//   1) 有 pom.xml      → mvn（本机没有就自动用 javac）
//   2) 有 build.gradle → gradle（同上）
//   3) 都没有          → 直接 javac 扫 src/main/java
//
// javac 的输出要解析成「文件:行:列: 错误: 消息」，
// 这样「问题」面板才能点着跳到出错的位置。
#pragma once

#include <string>
#include <vector>

namespace jbuild {

enum class Level { Info, Warning, Error };

// 一条诊断。javac 会给 file:line:col，能跳转。
struct Diagnostic {
    Level level = Level::Info;
    std::string file;        // 绝对路径
    int line = 0;
    int col = 0;
    std::string message;
};

struct Job {
    std::string projectRoot;     // 项目根
    std::string jdkHome;         // 用哪套 JDK（空 = 用默认）
    std::string sourceDir;       // src/main/java
    std::string outputDir;       // 编译产物放哪
    std::string mainClass;       // 跑哪个类
    std::vector<std::string> classpath;   // 额外依赖 jar

    bool verbose = false;
};

struct Result {
    bool ok = false;
    int exitCode = 0;
    std::string tool;            // 实际用了什么（mvn/gradle/javac）
    std::string rawOutput;       // 原始输出，给「构建输出」面板
    std::vector<Diagnostic> diags;
    int errorCount = 0;
    int warnCount = 0;
    double seconds = 0;
};

// 从项目里认构建方式。返回 "maven" / "gradle" / "javac"
std::string detectTool(const std::string& projectRoot);

// 构建输出面板里那几行提示（「编译完成」「产物目录」…）说哪国话。
// UI 切语言时调一次；不调就默认中文（老行为）。
// 只影响我们自己拼的提示行，javac/mvn 自己吐的输出原样透传。
void setLang(bool english);
bool english();

// 项目主类。
//   1) 先看 build.sbuild 之类有没有写
//   2) 再看 pom 的 mainClass
//   3) 都没有就找含 "public static void main" 的 .java，
//      按包名推出来
std::string guessMainClass(const std::string& projectRoot);

// 编译
Result compile(const Job& job);

// 运行。编译没做也能直接跑（用已有的 class）。
Result run(const Job& job, const std::vector<std::string>& args);

// 把 javac 输出解析成诊断列表。
// javac 格式： "D:\p\Main.java:5: error: ';' expected"
//      也可能 "Main.java:5: warning: [deprecation] ..."
void parseDiagnostics(const std::string& output, std::vector<Diagnostic>& out);

}  // namespace jbuild
