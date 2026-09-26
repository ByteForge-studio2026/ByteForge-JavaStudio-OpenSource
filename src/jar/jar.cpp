#include "jar/jar.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

#include "jdk/jdk.h"
#include "platform/platform.h"

namespace jarpack {

using spp::plat::fileExists;
using spp::plat::readFile;

// ---------------------------------------------------------------- 字节读

static uint16_t rd16(const unsigned char* p) {
    return (uint16_t)(p[0] | (p[1] << 8));
}
static uint32_t rd32(const unsigned char* p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) |
           ((uint32_t)p[3] << 24);
}

std::string classMajorToJava(int major) {
    // class 文件格式的 major version。
    // 45 起步（Java 1.1），之后每版 +1，Java 5 开始 major = 版本 + 44。
    if (major == 0) return "";
    if (major <= 45) return "1.1";
    if (major == 46) return "1.2";
    if (major == 47) return "1.3";
    if (major == 48) return "1.4";
    if (major == 49) return "5";
    if (major == 50) return "6";
    if (major == 51) return "7";
    if (major == 52) return "8";
    if (major == 53) return "9";
    if (major == 54) return "10";
    if (major == 55) return "11";
    if (major == 56) return "12";
    if (major == 57) return "13";
    if (major == 58) return "14";
    if (major == 59) return "15";
    if (major == 60) return "16";
    if (major == 61) return "17";
    if (major == 62) return "18";
    if (major == 63) return "19";
    if (major == 64) return "20";
    if (major == 65) return "21";
    if (major == 66) return "22";
    if (major == 67) return "23";
    if (major == 68) return "24";
    if (major == 69) return "25";
    if (major > 69) return "未知（更新）";
    return "";
}

std::string Info::javaVersionName() const {
    return classMajorToJava(classMajor);
}

// ---------------------------------------------------------------- 解压

// 只解 deflate 里的 stored 情况。真正的 deflate 要 inflate 算法，
// 这里不做 —— 见头文件里的说明。
static bool inflateStored(const std::string& file, const Entry& e,
                          std::string* out, std::string* err) {
    if (e.method == 0) {
        FILE* fp = fopen(file.c_str(), "rb");
        if (!fp) {
            if (err) *err = "打不开文件";
            return false;
        }
        if (fseek(fp, (long)e.dataOffset, SEEK_SET) != 0) {
            fclose(fp);
            if (err) *err = "定位失败";
            return false;
        }
        out->resize(e.size);
        size_t got = fread(&(*out)[0], 1, e.size, fp);
        fclose(fp);
        out->resize(got);
        return got == e.size;
    }
    if (err) *err = "这个条目的数据是压缩的（deflate），需要解压";
    return false;
}

// ---------------------------------------------------------------- MANIFEST

// MANIFEST.MF 是纯文本，行形如 "Main-Class: com.example.Main"
// 续行以单个空格开头。
static void parseManifest(const std::string& text, Info& info) {
    info.hasManifest = true;

    // 先把续行拼起来
    std::vector<std::string> lines;
    std::string cur;
    size_t pos = 0;
    while (pos <= text.size()) {
        size_t e = text.find('\n', pos);
        if (e == std::string::npos) e = text.size();
        std::string line = text.substr(pos, e - pos);
        pos = e + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();

        if (!line.empty() && line[0] == ' ') {
            cur += line.substr(1);      // 续行
        } else {
            if (!cur.empty()) lines.push_back(cur);
            cur = line;
        }
        if (e == text.size()) break;
    }
    if (!cur.empty()) lines.push_back(cur);

    auto value = [&](const char* key) -> std::string {
        size_t klen = strlen(key);
        for (const auto& l : lines) {
            if (l.size() > klen && l.compare(0, klen, key) == 0 &&
                l[klen] == ':') {
                std::string v = l.substr(klen + 1);
                while (!v.empty() && (v.front() == ' ' || v.front() == '\t'))
                    v.erase(v.begin());
                return v;
            }
        }
        return "";
    };

    info.mainClass = value("Main-Class");
    info.specVersion = value("Manifest-Version");
    info.createdBy = value("Created-By");
    info.classPath = value("Class-Path");
}

// ---------------------------------------------------------------- 主体

Info read(const std::string& path) {
    Info info;
    info.path = path;

    if (!fileExists(path)) {
        info.error = "文件不存在";
        return info;
    }

    FILE* fp = fopen(path.c_str(), "rb");
    if (!fp) {
        info.error = "打不开文件";
        return info;
    }

    // 文件大小
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    if (fsz < 22) {
        fclose(fp);
        info.error = "不是一个 zip/jar（太小）";
        return info;
    }

    // ---- 找 End of Central Directory
    // 签名 0x06054b50。注释最长 65535，所以从尾部往前找 64KB。
    const long kMaxScan = 65535 + 22;
    long scanStart = fsz > kMaxScan ? fsz - kMaxScan : 0;
    long scanLen = fsz - scanStart;

    std::vector<unsigned char> tail((size_t)scanLen);
    fseek(fp, scanStart, SEEK_SET);
    if (fread(tail.data(), 1, tail.size(), fp) != tail.size()) {
        fclose(fp);
        info.error = "读文件尾失败";
        return info;
    }

    long eocdRel = -1;
    for (long i = scanLen - 22; i >= 0; i--) {
        if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 &&
            tail[i + 3] == 0x06) {
            eocdRel = i;
            break;
        }
    }
    if (eocdRel < 0) {
        fclose(fp);
        info.error = "找不到 zip 结尾记录，不是 zip/jar";
        return info;
    }

    const unsigned char* e = tail.data() + eocdRel;
    uint16_t total = rd16(e + 10);
    uint32_t cdSize = rd32(e + 12);
    uint32_t cdOffset = rd32(e + 16);

    if (cdOffset == 0xFFFFFFFFu || total == 0xFFFF) {
        // zip64，少见。这里不支持，给个明确的话。
        fclose(fp);
        info.error = "这是 zip64 格式的 jar，暂时打不开";
        return info;
    }

    // ---- 读中央目录
    std::vector<unsigned char> cd(cdSize);
    if (fseek(fp, (long)cdOffset, SEEK_SET) != 0 ||
        fread(cd.data(), 1, cd.size(), fp) != cd.size()) {
        fclose(fp);
        info.error = "读中央目录失败";
        return info;
    }

    size_t p = 0;
    for (uint16_t k = 0; k < total; k++) {
        if (p + 46 > cd.size()) break;
        if (!(cd[p] == 0x50 && cd[p + 1] == 0x4b && cd[p + 2] == 0x01 &&
              cd[p + 3] == 0x02)) {
            break;      // 签名不对，停
        }

        Entry en;
        en.method = rd16(&cd[p + 10]);
        en.crc = rd32(&cd[p + 16]);
        en.compressedSize = rd32(&cd[p + 20]);
        en.size = rd32(&cd[p + 24]);
        uint16_t nameLen = rd16(&cd[p + 28]);
        uint16_t extraLen = rd16(&cd[p + 30]);
        uint16_t commentLen = rd16(&cd[p + 32]);
        uint32_t localOffset = rd32(&cd[p + 42]);

        if (p + 46 + nameLen > cd.size()) break;
        en.name.assign((const char*)&cd[p + 46], nameLen);

        // 目录条目的名字通常以 / 结尾
        en.isDir = (!en.name.empty() && en.name.back() == '/');

        // 本地头的 extra 字段长度可能和中央目录不一样，
        // 所以要去读本地头拿真实的数据起点。
        if (!en.isDir) {
            long save = ftell(fp);
            unsigned char lh[30];
            if (fseek(fp, (long)localOffset, SEEK_SET) == 0 &&
                fread(lh, 1, 30, fp) == 30) {
                if (lh[0] == 0x50 && lh[1] == 0x4b && lh[2] == 0x03 &&
                    lh[3] == 0x04) {
                    uint16_t lNameLen = rd16(lh + 26);
                    uint16_t lExtraLen = rd16(lh + 28);
                    en.dataOffset = (uint64_t)localOffset + 30 + lNameLen +
                                    lExtraLen;
                }
            }
            fseek(fp, save, SEEK_SET);
        }

        p += 46 + nameLen + extraLen + commentLen;

        if (en.isDir) continue;

        info.entries.push_back(en);
        info.totalSize += en.size;

        if (en.name.size() > 6 &&
            en.name.compare(en.name.size() - 6, 6, ".class") == 0) {
            info.classCount++;
        } else if (en.name == "META-INF/MANIFEST.MF") {
            // 下面单独读
        } else {
            info.resourceCount++;
        }
    }

    // ---- 读 MANIFEST.MF
    for (const auto& en : info.entries) {
        if (en.name != "META-INF/MANIFEST.MF") continue;
        std::string text, err;
        if (inflateStored(path, en, &text, &err)) {
            parseManifest(text, info);
        }
        break;
    }

    // ---- 从第一个 class 里抠 major version
    // class 文件头：CAFEBABE, minor(2), major(2)。major 在偏移 6。
    for (const auto& en : info.entries) {
        if (en.name.size() <= 6) continue;
        if (en.name.compare(en.name.size() - 6, 6, ".class") != 0) continue;
        if (!en.stored()) continue;      // 压缩的读不了，跳过
        FILE* f2 = fopen(path.c_str(), "rb");
        if (!f2) break;
        unsigned char hdr[8];
        if (fseek(f2, (long)en.dataOffset, SEEK_SET) == 0 &&
            fread(hdr, 1, 8, f2) == 8) {
            if (hdr[0] == 0xCA && hdr[1] == 0xFE && hdr[2] == 0xBA &&
                hdr[3] == 0xBE) {
                info.classMajor = rd16(hdr + 6);
            }
        }
        fclose(f2);
        break;
    }

    fclose(fp);

    // 条目按名字排序 —— 界面里树要好读
    std::sort(info.entries.begin(), info.entries.end(),
              [](const Entry& a, const Entry& b) { return a.name < b.name; });

    info.ok = true;
    return info;
}

bool readEntry(const std::string& jarPath, const Entry& e, std::string* out) {
    std::string err;
    return inflateStored(jarPath, e, out, &err);
}

int run(const std::string& jarPath,
        const std::vector<std::string>& args, std::string* output) {
    jdk::Info j = jdk::current();
    if (!j.ok) {
        if (output) *output = "找不到可用的 JDK，没法运行 jar";
        return -1;
    }

    std::vector<std::string> a;
    a.push_back("-jar");
    a.push_back(jarPath);
    for (const auto& x : args) a.push_back(x);

    // 在 jar 所在目录跑，相对路径的资源才找得到
    std::string work = spp::plat::parentDir(jarPath);
    return spp::plat::runCommand(j.java, a, work, output);
}

}  // namespace jarpack
