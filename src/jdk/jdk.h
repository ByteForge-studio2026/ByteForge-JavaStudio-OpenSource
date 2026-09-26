// jdk.h —— JDK 发现与注册
//
// Java Studio 自带三套 JDK（17 / 21 / 25），装在 exe 旁边的 jdk/ 下。
// 启动时扫一遍，认出来是什么版本，用户可以设一个默认的。
//
// 为什么要自带：用户机器上不一定有 JDK，或者版本对不上。
// 把 JDK 跟软件绑在一起，装完就能编译，不用先配环境变量。
#pragma once

#include <string>
#include <vector>

namespace jdk {

struct Info {
    int major = 0;              // 17 / 21 / 25
    std::string version;        // "17.0.20.1"
    std::string vendor;         // "Eclipse Adoptium"
    std::string home;           // JDK 根目录，例 D:\...\jdk\jdk-17
    std::string javac;          // home\bin\javac.exe
    std::string java;           // home\bin\java.exe
    std::string jar;
    bool ok = false;            // 上面的路径都验过存在

    // 是不是随软件一起发布的那套（exe 旁边的 jdk\ 下）。
    // 界面上「位置」一栏对它显示「内置」而不是把完整路径甩给用户 ——
    // 那个路径是安装目录，用户既改不了也不关心，写出来只会让人以为
    // 自己哪里配错了。只有外部 JDK 才值得显示路径。
    bool builtin = false;

    // 显示用，例 "JDK 21.0.11"
    std::string label() const;
};

// 扫描。顺序：
//   1) exe 旁边的 jdk/          ← 自带的，优先
//   2) JAVA_HOME
//   3) PATH 里的 javac
//   4) 几个常见安装位置
// 同一个 major 只留第一个找到的（自带的赢）。
std::vector<Info> scan();

// 当前选中的 JDK。没选过就挑最高的一个。
Info current();

// 换一个。major 不存在就什么都不做，返回 false。
bool select(int major);

// 上次选的是哪个版本，持久化在 %APPDATA%\JavaStudio\settings.txt
void loadPreference();
void savePreference();

// 按 major 找。找不到返回 ok=false 的 Info。
Info find(int major);

// 供界面用：已经扫到的那批
const std::vector<Info>& all();

// 让下次 current() 重新挑
void refresh();

}  // namespace jdk
