// jar.h —— 读 jar / 编译运行 Java
//
// jar 就是个 zip。这里不引第三方的 zip 库，直接读 zip 的中央目录
// （End of Central Directory → Central Directory Header），
// 够拿到文件名列表和内容了。
//
// 为什么手写：整个 IDE 一直是零第三方依赖编译的，
// 为了读个 zip 引 zlib 不值当。deflate 解压用系统自带的
// Windows API 走不通（那是 zip 不是 gzip），所以对压缩过的条目
// 就用 java -jar / javap 这些现成工具帮忙，不自己解 deflate。
#pragma once

#include <string>
#include <vector>

namespace jarpack {

struct Entry {
    std::string name;          // jar 内的路径，例 "com/example/Main.class"
    uint32_t compressedSize = 0;
    uint32_t size = 0;
    uint16_t method = 0;       // 0 = 存储, 8 = deflate
    uint32_t crc = 0;
    bool isDir = false;
    // zip 里该条目数据的偏移（已换算到文件绝对偏移）。
    // 只在 method == 0 时可直接读；deflate 的要解压。
    uint64_t dataOffset = 0;

    bool stored() const { return method == 0; }
};

struct Info {
    bool ok = false;
    std::string error;
    std::string path;

    std::vector<Entry> entries;

    // 从 MANIFEST.MF 读出来的
    std::string mainClass;      // Main-Class
    std::string specVersion;    // Manifest-Version
    std::string createdBy;
    std::string classPath;      // Class-Path
    bool hasManifest = false;

    // 统计
    int classCount = 0;
    int resourceCount = 0;
    uint64_t totalSize = 0;

    // 推断出来的 Java 版本（看 class 文件的 major version）
    int classMajor = 0;         // 61 = Java 17, 65 = 21, 69 = 25
    std::string javaVersionName() const;
};

// 读一个 jar 的目录结构和清单。不完整解压。
Info read(const std::string& path);

// 读单个条目（要完整解压的）。stored 的直接读，
// deflate 的走 java 自带的工具或者 store-only 场景。
// 现在只支持 stored —— 大多数 class 在 jar 里是 deflate 的，
// 所以这个主要用于小文本资源。
bool readEntry(const std::string& jarPath, const Entry& e, std::string* out);

// class 文件的 major version 对应哪个 Java 版本
std::string classMajorToJava(int major);

// 跑一个 jar。返回退出码，输出收进 output。
int run(const std::string& jarPath, const std::vector<std::string>& args,
        std::string* output);

}  // namespace jarpack
