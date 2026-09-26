// javaproject.h —— Java 项目骨架生成
//
// 建出来的目录是标准 Maven 布局，这样 IDE 和命令行都能用：
//
//   MyApp/
//     src/main/java/com/example/myapp/Main.java
//     src/main/resources/
//     src/test/java/
//     pom.xml            （Maven）
//     build.gradle       （Gradle，可选）
//     .gitignore
//     README.md
//
// 两种构建方式都能选，也可以都生成。
#pragma once

#include <string>
#include <vector>

namespace javaproj {

enum class BuildTool {
    None,       // 只要目录和源码，自己用 javac
    Maven,      // 生成 pom.xml
    Gradle,     // 生成 build.gradle + settings.gradle
    Both,       // 两个都生成
};

enum class Kind {
    Console,    // 控制台程序，一个 main 就够
    Lib,        // 库，没有 main
    Spring,     // Spring Boot 起手
};

struct Options {
    std::string dir;         // 项目根目录（父目录 + 项目名）
    std::string name;        // 项目名，也是目录名
    std::string group;       // 例 com.example
    std::string artifact;    // 例 myapp
    std::string version = "1.0.0";
    std::string jdkRelease = "17";   // maven.compiler.release
    Kind kind = Kind::Console;
    BuildTool tool = BuildTool::Maven;

    // ---- 照着 IntelliJ IDEA 的新建项目向导加的两个开关
    //
    // IDEA 2023 之后向导里有「Add sample code」：勾上才生成一个带 main 的
    // 示例文件；不勾就只有目录骨架和 pom.xml。用户明确要求「可以不生成
    // 初始项目结构」，所以这里做成可关的，默认跟 IDEA 一样是开的。
    bool addSampleCode = true;

    // IDEA 的「Create Git repository」：建完跑一次 git init。
    bool createGitRepo = false;
};

struct Result {
    bool ok = false;
    std::string error;
    std::vector<std::string> created;   // 相对路径，界面里显示用
};

// 包名合法性检查。返回空串表示 OK，否则是人话的错误说明。
std::string validatePackage(const std::string& group,
                            const std::string& artifact);

// 项目名合法性。同上。
std::string validateName(const std::string& name);

// 造。同名目录已存在且有东西就失败。
Result create(const Options& opt);

// 把 "com.example" 变成 "com/example"
std::string packageToPath(const std::string& pkg);

const char* buildToolName(BuildTool t);
const char* kindName(Kind k);
const char* kindDesc(Kind k);

}  // namespace javaproj
