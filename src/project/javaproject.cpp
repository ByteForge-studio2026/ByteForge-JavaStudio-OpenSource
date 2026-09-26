#include "project/javaproject.h"

#include <algorithm>
#include <cctype>
#include <cstring>

#include "platform/platform.h"

namespace javaproj {

using spp::plat::dirExists;
// 注：dllDir() 以前是给 copyIconLibrary 找图标目录用的，图标库不再拷进项目后
// 这里就不需要它了（platform.h 里的声明保留，DLL 仍然导出）。
using spp::plat::executableDir;
using spp::plat::fileExists;
using spp::plat::joinPath;
using spp::plat::listDir;
using spp::plat::makeDirs;
using spp::plat::parentDir;
using spp::plat::readFile;
using spp::plat::runCommand;
using spp::plat::writeFile;

// ---------------------------------------------------------------- 小工具

static std::string sub(const std::string& s, const std::string& from,
                       const std::string& to) {
    if (from.empty()) return s;
    std::string r = s;
    size_t p = 0;
    while ((p = r.find(from, p)) != std::string::npos) {
        r.replace(p, from.size(), to);
        p += to.size();
    }
    return r;
}

// 所有占位符一次替换掉
static std::string expand(const std::string& tpl, const Options& o) {
    std::string s = tpl;
    s = sub(s, "$NAME", o.name);
    s = sub(s, "$GROUP", o.group);
    s = sub(s, "$ARTIFACT", o.artifact);
    s = sub(s, "$VERSION", o.version);
    s = sub(s, "$RELEASE", o.jdkRelease);
    s = sub(s, "$PKGPATH", packageToPath(o.group + "." + o.artifact));
    s = sub(s, "$PKG", o.group + "." + o.artifact);
    // 类名：项目名去掉非法字符，首字母大写
    std::string cls;
    for (char c : o.name) {
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9'))
            cls += c;
    }
    if (cls.empty()) cls = "App";
    cls[0] = (char)toupper((unsigned char)cls[0]);
    s = sub(s, "$CLASS", cls);
    // Spring 要全小写无连字符的名字（会变成 bean 名）
    std::string low;
    for (char c : o.name) {
        if (c >= 'A' && c <= 'Z') low += (char)(c - 'A' + 'a');
        else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) low += c;
        else if (c == '-' || c == '_' || c == ' ') low += '-';
    }
    if (low.empty()) low = "app";
    s = sub(s, "$LOWER", low);
    return s;
}

static bool put(Result& r, const std::string& base, const std::string& rel,
                const std::string& content) {
    std::string full = joinPath(base, rel);
    size_t slash = full.find_last_of("\\/");
    if (slash != std::string::npos) makeDirs(full.substr(0, slash));
    if (!writeFile(full, content)) {
        r.error = "写不了文件：" + rel;
        return false;
    }
    r.created.push_back(rel);
    return true;
}

static bool mkdir(Result& r, const std::string& base, const std::string& rel) {
    std::string full = joinPath(base, rel);
    if (!makeDirs(full)) {
        r.error = "建不了目录：" + rel;
        return false;
    }
    r.created.push_back(rel + "/");
    return true;
}

std::string packageToPath(const std::string& pkg) {
    std::string s = pkg;
    for (auto& c : s)
        if (c == '.') c = '/';
    return s;
}

// ---------------------------------------------------------------- 校验

static bool identOk(const std::string& s) {
    if (s.empty()) return false;
    char c0 = s[0];
    // 不能以数字开头（Java 标识符规则）
    if (!((c0 >= 'a' && c0 <= 'z') || (c0 >= 'A' && c0 <= 'Z') || c0 == '_'))
        return false;
    for (char c : s) {
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
              (c >= '0' && c <= '9') || c == '_'))
            return false;
    }
    return true;
}

std::string validatePackage(const std::string& group,
                            const std::string& artifact) {
    if (group.empty()) return "包名前缀不能为空";

    // 拆开逐段验
    std::vector<std::string> parts;
    size_t pos = 0;
    while (pos <= group.size()) {
        size_t e = group.find('.', pos);
        if (e == std::string::npos) e = group.size();
        std::string seg = group.substr(pos, e - pos);
        pos = e + 1;
        if (seg.empty()) return "包名里有空的段（连续两个点？）";
        parts.push_back(seg);
    }
    for (const auto& s : parts) {
        if (!identOk(s)) return "包名的段 \"" + s + "\" 不是合法标识符";
        if (!s.empty() && isdigit((unsigned char)s[0]))
            return "包名的段不能以数字开头：" + s;
    }
    if (artifact.empty()) return "项目标识不能为空";
    if (!identOk(artifact)) return "项目标识不是合法标识符：" + artifact;
    if (isdigit((unsigned char)artifact[0]))
        return "项目标识不能以数字开头";

    // Java 关键字不能当包名段
    static const char* kws[] = {"int", "class", "new", "public", "private",
                                "static", "void", "return", "for", "if",
                                "else", "while", "do", "switch", "case",
                                "break", "continue", "try", "catch", "finally",
                                "throw", "throws", "package", "import", "this",
                                "super", "true", "false", "null", "long",
                                "short", "byte", "char", "float", "double",
                                "boolean", "final", "abstract", "interface",
                                "enum", "default"};
    for (const auto& s : parts) {
        for (const char* k : kws) {
            if (s == k) return "\"" + s + "\" 是 Java 关键字，不能当包名";
        }
    }
    for (const char* k : kws) {
        if (artifact == k) return "\"" + artifact + "\" 是 Java 关键字";
    }
    return "";
}

std::string validateName(const std::string& name) {
    if (name.empty()) return "项目名不能为空";
    if (name.size() > 64) return "项目名太长了";
    // Windows 目录名不能带的字符
    const char* bad = "\\/:*?\"<>|";
    for (char c : name) {
        if (strchr(bad, c)) return std::string("项目名不能包含 ") + c;
    }
    if (name == "." || name == "..") return "换个名字";
    // 结尾是点或空格，Windows 上会出问题
    if (name.back() == '.' || name.back() == ' ')
        return "项目名结尾不能是点或空格";
    return "";
}

const char* buildToolName(BuildTool t) {
    switch (t) {
        case BuildTool::None: return "无";
        case BuildTool::Maven: return "Maven";
        case BuildTool::Gradle: return "Gradle";
        case BuildTool::Both: return "Maven + Gradle";
    }
    return "?";
}

const char* kindName(Kind k) {
    switch (k) {
        case Kind::Console: return "控制台";
        case Kind::Lib: return "类库";
        case Kind::Spring: return "Spring Boot";
    }
    return "?";
}

const char* kindDesc(Kind k) {
    switch (k) {
        case Kind::Console:
            return "一个带 main 的类，编译完直接跑";
        case Kind::Lib:
            return "纯类库，没有 main，给别人当依赖用";
        case Kind::Spring:
            return "Spring Boot 起手式，含启动类和一个 REST 接口";
    }
    return "";
}

// ---------------------------------------------------------------- 模板

static const char* kPom = R"JAVA(<?xml version="1.0" encoding="UTF-8"?>
<project xmlns="http://maven.apache.org/POM/4.0.0"
         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
         xsi:schemaLocation="http://maven.apache.org/POM/4.0.0
                             http://maven.apache.org/xsd/maven-4.0.0.xsd">
    <modelVersion>4.0.0</modelVersion>

    <groupId>$GROUP</groupId>
    <artifactId>$ARTIFACT</artifactId>
    <version>$VERSION</version>
    <packaging>jar</packaging>

    <name>$NAME</name>
    <description>用 Java Studio 创建</description>

    <properties>
        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
        <maven.compiler.release>$RELEASE</maven.compiler.release>
    </properties>

    <dependencies>
        <dependency>
            <groupId>org.junit.jupiter</groupId>
            <artifactId>junit-jupiter</artifactId>
            <version>5.10.2</version>
            <scope>test</scope>
        </dependency>
    </dependencies>

    <build>
        <plugins>
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-compiler-plugin</artifactId>
                <version>3.13.0</version>
            </plugin>
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-surefire-plugin</artifactId>
                <version>3.2.5</version>
            </plugin>
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-jar-plugin</artifactId>
                <version>3.4.1</version>
                <configuration>
                    <archive>
                        <manifest>
                            <mainClass>$PKG.Main</mainClass>
                        </manifest>
                    </archive>
                </configuration>
            </plugin>
        </plugins>
    </build>
</project>
)JAVA";

static const char* kPomSpring = R"JAVA(<?xml version="1.0" encoding="UTF-8"?>
<project xmlns="http://maven.apache.org/POM/4.0.0"
         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
         xsi:schemaLocation="http://maven.apache.org/POM/4.0.0
                             http://maven.apache.org/xsd/maven-4.0.0.xsd">
    <modelVersion>4.0.0</modelVersion>

    <parent>
        <groupId>org.springframework.boot</groupId>
        <artifactId>spring-boot-starter-parent</artifactId>
        <version>3.3.2</version>
        <relativePath/>
    </parent>

    <groupId>$GROUP</groupId>
    <artifactId>$ARTIFACT</artifactId>
    <version>$VERSION</version>
    <name>$NAME</name>
    <description>用 Java Studio 创建</description>

    <properties>
        <java.version>$RELEASE</java.version>
    </properties>

    <dependencies>
        <dependency>
            <groupId>org.springframework.boot</groupId>
            <artifactId>spring-boot-starter-web</artifactId>
        </dependency>
        <dependency>
            <groupId>org.springframework.boot</groupId>
            <artifactId>spring-boot-starter-test</artifactId>
            <scope>test</scope>
        </dependency>
    </dependencies>

    <build>
        <plugins>
            <plugin>
                <groupId>org.springframework.boot</groupId>
                <artifactId>spring-boot-maven-plugin</artifactId>
            </plugin>
        </plugins>
    </build>
</project>
)JAVA";

static const char* kGradle = R"JAVA(plugins {
    id 'java'
    id 'application'
}

group = '$GROUP'
version = '$VERSION'

java {
    toolchain {
        languageVersion = JavaLanguageVersion.of($RELEASE)
    }
}

repositories {
    mavenCentral()
}

dependencies {
    testImplementation platform('org.junit:junit-bom:5.10.2')
    testImplementation 'org.junit.jupiter:junit-jupiter'
}

application {
    mainClass = '$PKG.Main'
}

tasks.named('test') {
    useJUnitPlatform()
}
)JAVA";

static const char* kGradleSpring = R"JAVA(plugins {
    id 'java'
    id 'org.springframework.boot' version '3.3.2'
    id 'io.spring.dependency-management' version '1.1.6'
}

group = '$GROUP'
version = '$VERSION'

java {
    toolchain {
        languageVersion = JavaLanguageVersion.of($RELEASE)
    }
}

repositories {
    mavenCentral()
}

dependencies {
    implementation 'org.springframework.boot:spring-boot-starter-web'
    testImplementation 'org.springframework.boot:spring-boot-starter-test'
}

tasks.named('test') {
    useJUnitPlatform()
}
)JAVA";

static const char* kSettingsGradle = R"JAVA(rootProject.name = '$NAME'
)JAVA";

static const char* kMainJava = R"JAVA(package $PKG;

public class Main {

    public static void main(String[] args) {
        System.out.println("Hello, $NAME!");
    }
}
)JAVA";

static const char* kLibJava = R"JAVA(package $PKG;

/**
 * $NAME 的公开接口。
 */
public class Greeter {

    private final String who;

    public Greeter(String who) {
        this.who = who;
    }

    public String greet() {
        return "Hello, " + who + "!";
    }
}
)JAVA";

static const char* kLibTestJava = R"JAVA(package $PKG;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

class GreeterTest {

    @Test
    void greetsByName() {
        assertEquals("Hello, world!", new Greeter("world").greet());
    }
}
)JAVA";

static const char* kSpringApp = R"JAVA(package $PKG;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;

@SpringBootApplication
public class Main {

    public static void main(String[] args) {
        SpringApplication.run(Main.class, args);
    }
}
)JAVA";

static const char* kSpringController = R"JAVA(package $PKG;

import java.util.Map;

import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class HelloController {

    @GetMapping("/hello")
    public Map<String, Object> hello() {
        return Map.of(
                "message", "Hello, $NAME!",
                "app", "$LOWER"
        );
    }
}
)JAVA";

static const char* kReadme = R"JAVA(# $NAME

用 **Java Studio** 创建。

## 目录

```
src/main/java/$PKGPATH/
    Main.java            程序入口
src/main/resources/      资源文件（会打进 jar）
src/test/java/           测试
```

在别的项目里想看全清单，打开 Java Studio 的「图标库」对话框搜就有。

## 编译运行

### 用 Java Studio

打开这个目录，按 **F5** 运行，或者 `Ctrl+B` 编译。

### 用命令行

$BUILDCMDS

## 直接用 javac

不依赖构建工具也能跑：

```
javac -d out $(find src/main/java -name '*.java')
java -cp out $PKG.Main
```

Windows 下 `$(find ...)` 换成手写文件列表，或者用 IDE 的编译按钮。
)JAVA";

static const char* kGitignore = R"JAVA(构建产物
target/
build/
out/
bin/
*.class
*.jar
!gradle/wrapper/gradle-wrapper.jar

# Gradle
.gradle/

# IDE
.idea/
*.iml
.vscode/
.settings/
.classpath
.project

# 系统
.DS_Store
Thumbs.db
*.log
)JAVA";

static const char* kResource = R"JAVA($NAME 的资源文件放这里。
#
# 这里的文件会被打进 jar，运行时用
#   getClass().getResourceAsStream("/文件名")
# 读出来。
)JAVA";

static const char* kTestDir = R"JAVA(测试代码放这里。
#
# 包名要和 src/main/java 下的一致，
# 编译产物不会进最终的 jar。
)JAVA";

// ---------------------------------------------------------------- 生成

// 注：以前这里会把内置图标库整套拷进项目根目录的 javastudio-icon/。
// 已经去掉了 —— 往每个项目里塞 178 个文件既污染项目目录又会进 git，
// IDE 自带的那套图标照旧能从「工具 → 图标库」里看和用。

Result create(const Options& opt) {
    Result r;

    if (opt.dir.empty()) {
        r.error = "没有指定目录";
        return r;
    }

    std::string ve = validateName(opt.name);
    if (!ve.empty()) {
        r.error = ve;
        return r;
    }
    ve = validatePackage(opt.group, opt.artifact);
    if (!ve.empty()) {
        r.error = ve;
        return r;
    }

    // 目录得是空的（或者不存在）。里面有东西就拦住 ——
    // 不然可能把用户已有的项目覆盖掉。
    if (dirExists(opt.dir)) {
        auto es = listDir(opt.dir);
        if (!es.empty()) {
            r.error = "目录里已经有东西了：" + opt.dir;
            return r;
        }
    }
    if (!makeDirs(opt.dir)) {
        r.error = "建不了目录：" + opt.dir;
        return r;
    }

    const std::string& base = opt.dir;

    // ---- 目录
    //
    // 两种布局，跟 IDEA 一致：
    //   有构建系统（Maven / Gradle）：src/main/java、src/main/resources、
    //                                src/test/java、src/test/resources
    //   无构建系统（IDEA 的 IntelliJ 模式）：只有一个 src/
    bool noTool = (opt.tool == BuildTool::None);

    std::string srcPkg;
    if (noTool) {
        if (!mkdir(r, base, "src")) return r;
        srcPkg = "src";
        if (!opt.group.empty()) {
            srcPkg = "src/" + packageToPath(opt.group +
                                            (opt.artifact.empty()
                                                 ? std::string()
                                                 : "." + opt.artifact));
            if (!mkdir(r, base, srcPkg)) return r;
        }
    } else {
        srcPkg = "src/main/java/" +
                 packageToPath(opt.group + "." + opt.artifact);
        if (!mkdir(r, base, "src")) return r;
        if (!mkdir(r, base, "src/main")) return r;
        if (!mkdir(r, base, "src/main/java")) return r;
        if (!mkdir(r, base, srcPkg)) return r;
        if (!mkdir(r, base, "src/main/resources")) return r;
        if (!mkdir(r, base, "src/test")) return r;
        if (!mkdir(r, base, "src/test/java")) return r;
        if (!mkdir(r, base, "src/test/resources")) return r;
    }

    // ---- 源码
    //
    // 关键：IDEA 的「Add sample code」关掉时，一个 .java 都不生成，
    // 只留目录骨架。用户要的就是这个「可以不生成初始项目结构」。
    if (opt.addSampleCode) {
        if (opt.kind == Kind::Console) {
            if (!put(r, base, srcPkg + "/Main.java", expand(kMainJava, opt)))
                return r;
        } else if (opt.kind == Kind::Lib) {
            if (!put(r, base, srcPkg + "/Greeter.java", expand(kLibJava, opt)))
                return r;
            std::string testPkg = noTool
                                      ? "src/test"
                                      : "src/test/java/" +
                                            packageToPath(opt.group + "." +
                                                          opt.artifact);
            if (!mkdir(r, base, testPkg)) return r;
            if (!put(r, base, testPkg + "/GreeterTest.java",
                     expand(kLibTestJava, opt)))
                return r;
        } else {
            if (!put(r, base, srcPkg + "/Main.java", expand(kSpringApp, opt)))
                return r;
            if (!put(r, base, srcPkg + "/HelloController.java",
                     expand(kSpringController, opt)))
                return r;
        }
    }

    // ---- 构建文件
    bool maven = (opt.tool == BuildTool::Maven || opt.tool == BuildTool::Both);
    bool gradle = (opt.tool == BuildTool::Gradle || opt.tool == BuildTool::Both);

    if (maven) {
        if (!put(r, base, "pom.xml",
                 expand(opt.kind == Kind::Spring ? kPomSpring : kPom, opt)))
            return r;
    }
    if (gradle) {
        if (!put(r, base, "build.gradle",
                 expand(opt.kind == Kind::Spring ? kGradleSpring : kGradle,
                        opt)))
            return r;
        if (!put(r, base, "settings.gradle", expand(kSettingsGradle, opt)))
            return r;
    }

    // ---- 说明和杂项
    std::string cmds;
    if (maven) {
        cmds += "```\nmvn compile          # 编译\nmvn exec:java ";
        cmds += (opt.kind == Kind::Console || opt.kind == Kind::Spring)
                    ? "-Dexec.mainClass=" + opt.group + "." + opt.artifact +
                          ".Main"
                    : "";
        cmds += "   # 运行\nmvn package          # 打包\n```";
    }
    if (gradle) {
        if (!cmds.empty()) cmds += "\n\n";
        cmds += "```\ngradle build         # 编译 + 打包\ngradle run           # 运行";
        if (opt.kind == Kind::Lib) cmds += "（类库没有 main，这行用不了）";
        cmds += "\n```";
    }
    if (cmds.empty()) {
        cmds = "这个项目没生成构建文件，直接用 javac 编。";
    }

    std::string readme = expand(kReadme, opt);
    readme = sub(readme, "$BUILDCMDS", cmds);
    if (!put(r, base, "README.md", readme)) return r;
    if (!put(r, base, ".gitignore", std::string(kGitignore))) return r;

    // IDEA 的「Create Git repository」。没装 git 也不算失败 ——
    // 目录已经建好了，仓库用户自己随时能 init。
    if (opt.createGitRepo) {
        std::string out;
        int rc = runCommand("git", {"init", base}, base, &out);
        if (rc == 0)
            r.created.push_back(".git  (git init)");
        else
            r.created.push_back("(git init 没跑成，仓库没建)");
    }

    r.ok = true;
    return r;
}

}  // namespace javaproj
