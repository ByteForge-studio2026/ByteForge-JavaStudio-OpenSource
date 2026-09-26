// jlibs.cpp —— 第三方库目录、查找与下载
//
// 目录表是从 Maven 中央仓库逐个核对过的（HEAD 请求，确认 200 且有
// 实际内容），不是凭记忆写的。少一个字符的坐标会变成一个 404，
// 用户看到的是「下不下来」而不知道为什么。
//
// 缓存目录照 Maven 仓库的目录约定排（group 的点换成斜杠），
// 这样和 ~/.m2 长得一样，两边可以互相认。
#include "libs/jlibs.h"

#include <cstdio>
#include <cstring>
#include <algorithm>

#include "platform/platform.h"

namespace jlibs {

using spp::plat::dirExists;
using spp::plat::executableDir;
using spp::plat::fileExists;
using spp::plat::fileSize;
using spp::plat::homeDir;
using spp::plat::httpGet;
using spp::plat::httpGetToFile;
using spp::plat::joinPath;
using spp::plat::listDir;
using spp::plat::appDataDir;
using spp::plat::readFile;
using spp::plat::removeDir;
using spp::plat::removeFile;
using spp::plat::whichExe;
using spp::plat::writeFile;

// ---------------------------------------------------------------- 目录
//
// 59 个常用库。挑的原则是「新手做个小东西会真的用到」，
// 所以 JSON、日志、测试、工具类给得多，企业级的 Spring 全家桶
// 只留最核心的几个 —— 那些真要用的人会去用 Maven。
const std::vector<Entry>& catalog() {
    static const std::vector<Entry> kAll = {
    {"fastjson2", "com.alibaba.fastjson2", "fastjson2", "2.0.52", "Fastjson2", "阿里出品的高性能 JSON 库", "JSON"},
    {"gson", "com.google.code.gson", "gson", "2.11.0", "Gson", "Google 的 JSON 库，toJson / fromJson 一行搞定", "JSON"},
    {"jackson-core", "com.fasterxml.jackson.core", "jackson-core", "2.17.2", "Jackson Core", "Jackson 的流式解析核心", "JSON"},
    {"jackson-databind", "com.fasterxml.jackson.core", "jackson-databind", "2.17.2", "Jackson Databind", "Spring 默认的 JSON 序列化引擎", "JSON"},
    {"snakeyaml", "org.yaml", "snakeyaml", "2.2", "SnakeYAML", "读写 YAML 配置文件", "JSON"},
    {"xstream", "com.thoughtworks.xstream", "xstream", "1.4.20", "XStream", "XML 与对象互转", "JSON"},
    {"log4j-core", "org.apache.logging.log4j", "log4j-core", "2.23.1", "Log4j 2", "Apache 日志框架", "日志"},
    {"logback-classic", "ch.qos.logback", "logback-classic", "1.5.6", "Logback", "SLF4J 的原生实现", "日志"},
    {"slf4j-api", "org.slf4j", "slf4j-api", "2.0.13", "SLF4J API", "日志门面，几乎所有框架都靠它", "日志"},
    {"assertj-core", "org.assertj", "assertj-core", "3.26.0", "AssertJ", "流式断言", "测试"},
    {"junit4", "junit", "junit", "4.13.2", "JUnit 4", "经典单元测试框架", "测试"},
    {"junit-jupiter-api", "org.junit.jupiter", "junit-jupiter-api", "5.10.2", "JUnit 5 API", "JUnit 5 的注解与断言（真身，不是聚合 POM）", "测试"},
    {"junit-platform-console", "org.junit.platform", "junit-platform-console-standalone", "1.10.2", "JUnit 平台控制台", "自带全套依赖，命令行直接跑测试用", "测试"},
    {"mockito-core", "org.mockito", "mockito-core", "5.12.0", "Mockito", "打桩 / 模拟对象", "测试"},
    {"caffeine", "com.github.ben-manes.caffeine", "caffeine", "3.1.8", "Caffeine", "高性能本地缓存", "通用"},
    {"commons-codec", "commons-codec", "commons-codec", "1.17.1", "Commons Codec", "Base64 / MD5 / Hex", "通用"},
    {"commons-collections4", "org.apache.commons", "commons-collections4", "4.4", "Commons Collections4", "增强集合类型", "通用"},
    {"commons-io", "commons-io", "commons-io", "2.16.1", "Commons IO", "文件与流工具", "通用"},
    {"commons-lang3", "org.apache.commons", "commons-lang3", "3.14.0", "Commons Lang3", "字符串 / 日期 / 反射工具集", "通用"},
    {"commons-math3", "org.apache.commons", "commons-math3", "3.6.1", "Commons Math3", "统计 / 线性代数 / 优化", "通用"},
    {"guava", "com.google.guava", "guava", "33.2.1-jre", "Guava", "Google 核心 Java 工具库", "通用"},
    {"hutool-all", "cn.hutool", "hutool-all", "5.8.28", "Hutool", "国产全能工具包，一个 jar 全都有", "通用"},
    {"jna", "net.java.dev.jna", "jna", "5.14.0", "JNA", "直接调用本地 DLL / so", "通用"},
    {"joda-time", "joda-time", "joda-time", "2.12.7", "Joda-Time", "经典的日期时间库", "通用"},
    {"kotlin-stdlib", "org.jetbrains.kotlin", "kotlin-stdlib", "1.9.24", "Kotlin 标准库", "在 Java 项目里用 Kotlin 写的库时需要", "通用"},
    {"lombok", "org.projectlombok", "lombok", "1.18.32", "Lombok", "用注解省掉 getter / setter", "通用"},
    {"protobuf-java", "com.google.protobuf", "protobuf-java", "4.27.2", "Protocol Buffers", "Google 的二进制序列化", "通用"},
    {"httpclient5", "org.apache.httpcomponents.client5", "httpclient5", "5.3.1", "HttpClient 5", "Apache 的 HTTP 客户端", "网络"},
    {"jsoup", "org.jsoup", "jsoup", "1.17.2", "Jsoup", "HTML 解析与抓取", "网络"},
    {"okhttp", "com.squareup.okhttp3", "okhttp", "4.12.0", "OkHttp", "HTTP 客户端", "网络"},
    {"retrofit", "com.squareup.retrofit2", "retrofit", "2.11.0", "Retrofit", "把 HTTP 接口变成 Java 接口", "网络"},
    {"druid", "com.alibaba", "druid", "1.2.23", "Druid", "阿里连接池 + 监控", "数据库"},
    {"h2", "com.h2database", "h2", "2.2.224", "H2", "纯 Java 嵌入式数据库", "数据库"},
    {"hibernate-core", "org.hibernate.orm", "hibernate-core", "6.5.2.Final", "Hibernate ORM", "JPA 实现", "数据库"},
    {"hikaricp", "com.zaxxer", "HikariCP", "5.1.0", "HikariCP", "最快的 JDBC 连接池", "数据库"},
    {"jedis", "redis.clients", "jedis", "5.1.3", "Jedis", "Redis 客户端", "数据库"},
    {"kafka-clients", "org.apache.kafka", "kafka-clients", "3.7.1", "Kafka 客户端", "收发 Kafka 消息", "数据库"},
    {"mybatis", "org.mybatis", "mybatis", "3.5.16", "MyBatis", "SQL 映射框架", "数据库"},
    {"mybatis-plus-core", "com.baomidou", "mybatis-plus-core", "3.5.7", "MyBatis-Plus", "MyBatis 增强，CRUD 免写 SQL", "数据库"},
    {"mysql-connector-j", "com.mysql", "mysql-connector-j", "8.4.0", "MySQL 驱动", "连 MySQL 用", "数据库"},
    {"pagehelper", "com.github.pagehelper", "pagehelper", "6.1.0", "PageHelper", "分页插件", "数据库"},
    {"postgresql", "org.postgresql", "postgresql", "42.7.3", "PostgreSQL 驱动", "连 PostgreSQL 用", "数据库"},
    {"sqlite-jdbc", "org.xerial", "sqlite-jdbc", "3.46.0.0", "SQLite 驱动", "零配置的嵌入式数据库", "数据库"},
    {"jetty-server", "org.eclipse.jetty", "jetty-server", "11.0.22", "Jetty", "轻量 Web 服务器", "Web"},
    {"jakarta.servlet-api", "jakarta.servlet", "jakarta.servlet-api", "6.0.0", "Servlet API", "写 Web 应用要的接口", "Web"},
    {"spring-boot", "org.springframework.boot", "spring-boot", "3.3.2", "Spring Boot", "开箱即用的 Spring", "Web"},
    {"spring-context", "org.springframework", "spring-context", "6.1.10", "Spring Context", "依赖注入容器", "Web"},
    {"spring-core", "org.springframework", "spring-core", "6.1.10", "Spring Core", "Spring 的 IoC 内核", "Web"},
    {"spring-web", "org.springframework", "spring-web", "6.1.10", "Spring Web", "Spring 的 Web 基础", "Web"},
    {"poi", "org.apache.poi", "poi", "5.2.5", "Apache POI", "读写 Excel / Word（xls 系）", "文档"},
    {"poi-ooxml", "org.apache.poi", "poi-ooxml", "5.2.5", "Apache POI OOXML", "读写 .xlsx / .docx", "文档"},
    {"easyexcel-core", "com.alibaba", "easyexcel-core", "4.0.3", "EasyExcel Core", "阿里出的 Excel 读写，省内存", "文档"},
    {"jfreechart", "org.jfree", "jfreechart", "1.5.4", "JFreeChart", "画柱状图 / 折线图 / 饼图", "文档"},
    {"pdfbox", "org.apache.pdfbox", "pdfbox", "3.0.2", "PDFBox", "生成和读取 PDF", "文档"},
    {"thumbnailator", "net.coobird", "thumbnailator", "0.4.20", "Thumbnailator", "一行缩放 / 裁剪图片", "文档"},
    {"zxing-core", "com.google.zxing", "core", "3.5.3", "ZXing", "生成 / 识别二维码", "文档"},
    {"jjwt-api", "io.jsonwebtoken", "jjwt-api", "0.12.6", "JJWT", "签发和校验 JWT", "安全"},
    {"jbcrypt", "org.mindrot", "jbcrypt", "0.4", "jBCrypt", "密码加盐哈希", "安全"},
    {"flatlaf", "com.formdev", "flatlaf", "3.4.1", "FlatLaf", "Swing 的现代扁平主题", "桌面"},
    };
    return kAll;
}

const Entry* find(const std::string& id) {
    for (const auto& e : catalog())
        if (e.id == id) return &e;
    return nullptr;
}

bool parseCoord(const std::string& coord, Entry& out) {
    std::vector<std::string> parts;
    std::string cur;
    for (char c : coord) {
        if (c == ':') { parts.push_back(cur); cur.clear(); }
        else cur += c;
    }
    parts.push_back(cur);
    if (parts.size() != 3) return false;

    for (auto& p : parts) {
        size_t a = p.find_first_not_of(" \t\r\n");
        size_t b = p.find_last_not_of(" \t\r\n");
        if (a == std::string::npos) return false;
        p = p.substr(a, b - a + 1);
    }
    if (parts[0].empty() || parts[1].empty() || parts[2].empty()) return false;

    out = Entry{};
    out.group    = parts[0];
    out.artifact = parts[1];
    out.version  = parts[2];
    out.id       = out.coord();
    out.name     = parts[1];
    out.category = "自定义";
    return true;
}

std::vector<std::string> categories() {
    std::vector<std::string> out;
    for (const auto& e : catalog()) {
        bool has = false;
        for (const auto& c : out)
            if (c == e.category) { has = true; break; }
        if (!has) out.push_back(e.category);
    }
    return out;
}

std::string Entry::repoPath() const {
    std::string p;
    for (char ch : group) p += (ch == '.') ? '/' : ch;
    p += '/';
    p += artifact;
    p += '/';
    p += version;
    p += '/';
    p += fileName();
    return p;
}

std::string Entry::url() const {
    // 中央仓库。国内网络慢的话，第一次下会等几秒 ——
    // 但比内置一个私服地址然后因为它挂了而完全用不了要好。
    return "https://repo1.maven.org/maven2/" + repoPath();
}

char separator() {
#ifdef _WIN32
    return ';';
#else
    return ':';
#endif
}

// ---------------------------------------------------------------- 位置

std::string cacheDir() {
    return joinPath(appDataDir("JavaStudio"), "libs");
}

// 项目里用户自己放 jar 的地方
static std::vector<std::string> projectLibDirs(const std::string& root) {
    std::vector<std::string> v;
    if (root.empty()) return v;
    v.push_back(joinPath(root, "lib"));
    v.push_back(joinPath(root, "libs"));
    return v;
}

// repoPath() 用的是 '/'，因为那是 URL 的写法。落到本地磁盘时
// 换成平台分隔符 —— 混着写（libs\com/google/code/…）Windows 认，
// 但日志和状态栏里看着很脏，而且用户会想复制这个路径。
static std::string nativize(const std::string& p) {
#ifdef _WIN32
    std::string s = p;
    for (char& c : s)
        if (c == '/') c = '\\';
    return s;
#else
    return p;
#endif
}

// 在一个遵循 Maven 布局的根目录下找这个库
static std::string underMavenRoot(const std::string& root, const Entry& e) {
    if (root.empty()) return "";
    std::string p = joinPath(root, nativize(e.repoPath()));
    return fileExists(p) ? p : "";
}

// 在一个扁平目录里找 xxx-版本.jar（用户丢 jar 通常不带 group 目录）
static std::string underFlatDir(const std::string& dir, const Entry& e) {
    if (dir.empty() || !dirExists(dir)) return "";
    std::string want = e.fileName();

    std::string direct = joinPath(dir, want);
    if (fileExists(direct)) return direct;

    // 也认「名字对但版本不同」的，比如用户放的是 gson-2.10.1.jar。
    // 版本可能不兼容，但总比「明明有却说没有」好。
    std::string prefix = e.artifact + "-";
    for (const auto& d : listDir(dir)) {
        if (d.isDir) continue;
        if (d.name.size() <= 4) continue;
        if (d.name.compare(d.name.size() - 4, 4, ".jar") != 0) continue;
        if (d.name.rfind(prefix, 0) == 0) return joinPath(dir, d.name);
    }
    return "";
}

std::string findJar(const Entry& e, const std::string& projectRoot) {
    // 1) 项目里的 lib / libs —— 用户显式放的东西优先
    for (const auto& d : projectLibDirs(projectRoot)) {
        std::string p = underFlatDir(d, e);
        if (!p.empty()) return p;
    }

    // 2) 本机缓存（IDE 自己下下来的）
    {
        std::string p = underMavenRoot(cacheDir(), e);
        if (!p.empty()) return p;
    }

    // 3) exe 旁边的 libs/ —— 离线分发用
    {
        std::string p = joinPath(executableDir(), "libs");
        std::string r = underMavenRoot(p, e);
        if (!r.empty()) return r;
        r = underFlatDir(p, e);
        if (!r.empty()) return r;
    }

    // 4) Maven 本地仓库。用户机器上装了 Maven 或者跑过别的 Java
    //    项目时，这里通常已经有一大堆 jar 了，能用就用，不用重下。
    {
        std::string m2 = joinPath(homeDir(), ".m2");
        m2 = joinPath(m2, "repository");
        std::string p = underMavenRoot(m2, e);
        if (!p.empty()) return p;
    }

    // 5) Gradle 的缓存。目录布局不一样，只能按文件名粗找一层。
    {
        std::string g = joinPath(homeDir(), ".gradle");
        g = joinPath(g, "caches");
        g = joinPath(g, "modules-2");
        g = joinPath(g, "files-2.1");
        std::string p = underFlatDir(joinPath(g, e.group), e);
        if (!p.empty()) return p;
    }

    return "";
}

// ---------------------------------------------------------------- 下载

bool download(const Entry& e, std::string* err, std::string* savedTo,
              DownloadProgress prog, void* progUser) {
    std::string dest = joinPath(cacheDir(), nativize(e.repoPath()));

    // 已经有就直接算成功 —— 重复点「下载」不该白跑一趟网络
    if (fileExists(dest)) {
        if (savedTo) *savedTo = dest;
        return true;
    }

    if (!httpGetToFile(e.url(), dest, err, prog, progUser)) return false;

    if (!fileExists(dest)) {
        if (err) *err = "下载后文件不存在";
        return false;
    }
    if (savedTo) *savedTo = dest;
    return true;
}

// 递归收 jar。缓存目录按 group 分层，深度不固定，只能递归。
static void walkJars(const std::string& dir, int depth,
                     std::vector<std::string>& out) {
    if (depth > 8) return;
    for (const auto& d : listDir(dir)) {
        std::string p = joinPath(dir, d.name);
        if (d.isDir) {
            walkJars(p, depth + 1, out);
        } else if (d.name.size() > 4 &&
                   d.name.compare(d.name.size() - 4, 4, ".jar") == 0) {
            out.push_back(p);
        }
    }
}

// 从缓存里的路径反推坐标。
//
// 布局是 <group/用/斜杠>/<artifact>/<version>/<artifact>-<version>.jar，
// 所以从尾巴往前数：倒数第一是文件名、倒数第二是版本、倒数第三是 artifact，
// 剩下的全是 group（拼回去时斜杠换回点）。
static std::string coordFromCachePath(const std::string& root,
                                      const std::string& path) {
    if (path.size() <= root.size()) return "";
    std::string rel = path.substr(root.size());
    while (!rel.empty() && (rel.front() == '\\' || rel.front() == '/'))
        rel.erase(rel.begin());

    std::vector<std::string> parts;
    std::string cur;
    for (char c : rel) {
        if (c == '\\' || c == '/') { parts.push_back(cur); cur.clear(); }
        else cur += c;
    }
    parts.push_back(cur);

    // 至少要有 group 一段 + artifact + version + 文件名
    if (parts.size() < 4) return "";

    std::string artifact = parts[parts.size() - 3];
    std::string version  = parts[parts.size() - 2];
    std::string group;
    for (size_t i = 0; i + 3 < parts.size(); i++) {
        if (i) group += '.';
        group += parts[i];
    }
    if (group.empty() || artifact.empty() || version.empty()) return "";
    return group + ":" + artifact + ":" + version;
}

int cacheCount() {
    std::string root = cacheDir();
    if (!dirExists(root)) return 0;
    std::vector<std::string> jars;
    walkJars(root, 0, jars);
    return (int)jars.size();
}

std::vector<CacheItem> cacheList() {
    std::vector<CacheItem> out;
    std::string root = cacheDir();
    if (!dirExists(root)) return out;

    std::vector<std::string> jars;
    walkJars(root, 0, jars);

    for (const auto& p : jars) {
        std::string coord = coordFromCachePath(root, p);
        if (coord.empty()) continue;   // 布局不对的（比如谁手放了个 jar）就不列
        CacheItem ci;
        ci.coord = coord;
        ci.path = p;
        ci.size = fileSize(p);
        out.push_back(std::move(ci));
    }
    std::sort(out.begin(), out.end(),
              [](const CacheItem& a, const CacheItem& b) { return a.coord < b.coord; });
    return out;
}

uint64_t cacheBytes() {
    uint64_t n = 0;
    for (const auto& c : cacheList()) n += c.size;
    return n;
}

bool cacheRemove(const std::string& coord) {
    Entry e;
    if (!parseCoord(coord, e)) return false;

    std::string root = cacheDir();
    std::string jar = joinPath(root, nativize(e.repoPath()));
    if (!fileExists(jar)) return false;
    if (!removeFile(jar)) return false;

    // 顺手把变空的 version / artifact 目录收掉。
    // 这两层是缓存自己建的，空了就是垃圾；再往上（group 那几层）
    // 不碰 —— 别的版本可能还在用。removeDir 只删空目录，非空自然失败。
    std::string verDir = joinPath(root, nativize(e.repoPath()));
    size_t cut = verDir.find_last_of("\\/");
    if (cut != std::string::npos) {
        verDir = verDir.substr(0, cut);
        removeDir(verDir);
        cut = verDir.find_last_of("\\/");
        if (cut != std::string::npos) removeDir(verDir.substr(0, cut));
    }
    return true;
}

// ---------------------------------------------------------------- 仓库查询

// 只转义 URL 里不能裸着出现的那几个字符。
// 关键字来自用户输入，中文、空格、& 都很常见 —— 不转义的话
// "&" 会被当成参数分隔符，搜索条件当场被截断。
static std::string urlEncode(const std::string& s) {
    static const char* kHex = "0123456789ABCDEF";
    std::string out;
    for (unsigned char c : s) {
        bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '_' ||
                    c == '.' || c == '~';
        if (safe) {
            out += (char)c;
        } else if (c == ' ') {
            out += '+';
        } else {
            out += '%';
            out += kHex[(c >> 4) & 0xF];
            out += kHex[c & 0xF];
        }
    }
    return out;
}

// 从一小段 JSON 里抠一个字符串字段。
//
// 不引 JSON 库：仓库返回的就三层结构，而且这里只需要三个字段，
// 为了它把依赖树再拉长一截不划算。转义只处理 \" 和 \\ ——
// 这两个是仓库描述里真会出现的。
static std::string jsonStr(const std::string& s, const std::string& key,
                           size_t from = 0, size_t* end = nullptr) {
    std::string pat = "\"" + key + "\":\"";
    size_t a = s.find(pat, from);
    if (a == std::string::npos) return "";
    a += pat.size();

    std::string v;
    for (size_t i = a; i < s.size(); i++) {
        char c = s[i];
        if (c == '\\' && i + 1 < s.size()) {
            char n = s[i + 1];
            if (n == '"' || n == '\\') { v += n; i++; continue; }
            if (n == 'n') { v += '\n'; i++; continue; }
            v += c;
            continue;
        }
        if (c == '"') {
            if (end) *end = i + 1;
            break;
        }
        v += c;
    }
    return v;
}

std::vector<SearchHit> search(const std::string& query, std::string* err,
                              int rows) {
    std::vector<SearchHit> out;
    if (query.empty()) return out;
    if (rows < 1) rows = 1;
    if (rows > 100) rows = 100;

    std::string url = "https://search.maven.org/solrsearch/select?q=" +
                      urlEncode(query) + "&rows=" + std::to_string(rows) +
                      "&wt=json";

    std::string body, why;
    if (!httpGet(url, &body, &why)) {
        if (err) *err = why.empty() ? "搜不了（网络不通或仓库没响应）" : why;
        return out;
    }

    // docs 里每条都有 "g"、"a"、"latestVersion"，按出现顺序成对出现。
    // 用下一个 "g":" 当这一条的边界，够用又不用真的解析 JSON。
    size_t pos = 0;
    while ((int)out.size() < rows) {
        size_t g = body.find("\"g\":\"", pos);
        if (g == std::string::npos) break;
        size_t nextG = body.find("\"g\":\"", g + 4);

        SearchHit h;
        h.group = jsonStr(body, "g", g);
        h.artifact = jsonStr(body, "a", g);
        h.version = jsonStr(body, "latestVersion", g);
        // 描述留空：搜索接口不返回说明文字。与其去猜一段，
        // 不如让界面只显示坐标 —— 坐标才是用户真正核对的东西。

        if (h.group.empty() || h.artifact.empty()) break;
        out.push_back(std::move(h));

        if (nextG == std::string::npos) break;
        pos = nextG;
    }
    return out;
}

std::vector<std::string> versions(const std::string& group,
                                  const std::string& artifact,
                                  std::string* err) {
    std::vector<std::string> out;
    if (group.empty() || artifact.empty()) return out;

    std::string path;
    for (char c : group) path += (c == '.') ? '/' : c;
    std::string url = "https://repo1.maven.org/maven2/" + path + "/" +
                      artifact + "/maven-metadata.xml";

    std::string body, why;
    if (!httpGet(url, &body, &why)) {
        if (err) *err = why.empty() ? "取不到版本列表" : why;
        return out;
    }

    // 只看 <versions> 这一节 —— metadata 里还有 <lastUpdated> 之类的，
    // 全扫会把时间戳也当成版本号。
    size_t a = body.find("<versions>");
    size_t b = body.find("</versions>");
    if (a == std::string::npos || b == std::string::npos) {
        if (err) *err = "这个 artifact 没有版本列表";
        return out;
    }

    std::string sec = body.substr(a, b - a);
    size_t pos = 0;
    while (true) {
        size_t s = sec.find("<version>", pos);
        if (s == std::string::npos) break;
        s += 9;
        size_t e = sec.find("</version>", s);
        if (e == std::string::npos) break;
        std::string v = sec.substr(s, e - s);
        size_t p = v.find_first_not_of(" \t\r\n");
        size_t q = v.find_last_not_of(" \t\r\n");
        if (p != std::string::npos) out.push_back(v.substr(p, q - p + 1));
        pos = e + 10;
    }

    // 文件里是从旧到新，界面要新的在前
    std::reverse(out.begin(), out.end());
    return out;
}

// ---------------------------------------------------------------- 项目选择

static std::string selectionFile(const std::string& projectRoot) {
    std::string d = joinPath(projectRoot, ".javastudio");
    return joinPath(d, "libs.txt");
}

std::vector<std::string> loadSelection(const std::string& projectRoot) {
    std::vector<std::string> out;
    if (projectRoot.empty()) return out;

    std::string txt = readFile(selectionFile(projectRoot));
    if (txt.empty()) return out;

    std::string cur;
    for (size_t i = 0; i <= txt.size(); i++) {
        char ch = (i == txt.size()) ? '\n' : txt[i];
        if (ch == '\r') continue;
        if (ch == '\n') {
            // 去掉首尾空白和注释
            size_t a = cur.find_first_not_of(" \t");
            size_t b = cur.find_last_not_of(" \t");
            if (a != std::string::npos) {
                std::string line = cur.substr(a, b - a + 1);
                if (!line.empty() && line[0] != '#' && line[0] != '/') {
                    out.push_back(line);
                }
            }
            cur.clear();
        } else {
            cur += ch;
        }
    }
    return out;
}

bool saveSelection(const std::string& projectRoot,
                   const std::vector<std::string>& ids) {
    if (projectRoot.empty()) return false;

    std::string dir = joinPath(projectRoot, ".javastudio");
    if (!spp::plat::makeDirs(dir)) return false;

    std::string body =
        "# Java Studio 第三方库\n"
        "# 一行一个 id，改完在 IDE 里重新打开项目即可生效\n";
    for (const auto& id : ids) body += id + "\n";

    return writeFile(selectionFile(projectRoot), body);
}

// ---------------------------------------------------------------- pom 解析
//
// 不求完整 XML —— 只把 <dependencies> 里的 groupId / artifactId /
// version 三元组抠出来。属性占位符（${...}）认不出来就跳过。
// 抠一个 XML 标签的正文并去空白。属性占位符（${...}）解析不了，
// 一律当成「没有」—— 显示一个 ${junit.version} 给用户看没有意义。
static std::string xmlTag(const std::string& block, const std::string& tag) {
    std::string open = "<" + tag + ">";
    std::string close = "</" + tag + ">";
    size_t a = block.find(open);
    if (a == std::string::npos) return "";
    a += open.size();
    size_t b = block.find(close, a);
    if (b == std::string::npos) return "";
    std::string v = block.substr(a, b - a);

    size_t s = v.find_first_not_of(" \t\r\n");
    size_t e = v.find_last_not_of(" \t\r\n");
    if (s == std::string::npos) return "";
    v = v.substr(s, e - s + 1);
    if (v.find("${") != std::string::npos) return "";
    return v;
}

std::vector<std::string> pomCoords(const std::string& projectRoot) {
    std::vector<std::string> out;
    if (projectRoot.empty()) return out;

    std::string txt = readFile(joinPath(projectRoot, "pom.xml"));
    if (txt.empty()) return out;

    auto tagOf = [&](const std::string& block, const char* tag) -> std::string {
        return xmlTag(block, tag);
    };

    size_t pos = 0;
    while (true) {
        size_t a = txt.find("<dependency>", pos);
        if (a == std::string::npos) break;
        size_t b = txt.find("</dependency>", a);
        if (b == std::string::npos) break;
        std::string block = txt.substr(a, b - a);

        std::string g = tagOf(block, "groupId");
        std::string art = tagOf(block, "artifactId");
        std::string v = tagOf(block, "version");
        if (!g.empty() && !art.empty() && !v.empty())
            out.push_back(g + ":" + art + ":" + v);

        pos = b + 12;
    }
    return out;
}

// ---------------------------------------------------------------- pom 编辑
//
// 手改 XML 而不是引一个 XML 库：pom 是用户自己也会编辑的文件，
// 改完必须保持他原来的排版、注释、空行都在。解析器读一遍再序列化出去
// 会把这些全抹平，下次他打开 pom 看到被重写过的文件，第一反应是
// 「这 IDE 动我的东西」。所以只在 <dependencies> 那一节里做插入/删除。

bool pomExists(const std::string& projectRoot) {
    if (projectRoot.empty()) return false;
    return fileExists(joinPath(projectRoot, "pom.xml"));
}

// 缩进取 pom 里现有 <dependency> 那一行的缩进；没有就退到 4 空格。
// 跟着文件走，插进去才不会看着像外来物。
static std::string pomIndent(const std::string& txt) {
    size_t a = txt.find("<dependency>");
    if (a == std::string::npos) return "    ";
    size_t lineStart = txt.rfind('\n', a);
    lineStart = (lineStart == std::string::npos) ? 0 : lineStart + 1;
    std::string ws;
    for (size_t i = lineStart; i < a; i++) {
        char c = txt[i];
        if (c == ' ' || c == '\t') ws += c;
        else break;
    }
    return ws.empty() ? "    " : ws;
}

// 删掉所有 group+artifact 匹配的 <dependency> 块（连同它后面那行空行）。
// 返回删了几块。
static int pomDropBlocks(std::string& txt, const std::string& group,
                         const std::string& artifact) {
    int n = 0;
    size_t pos = 0;
    while (true) {
        size_t a = txt.find("<dependency>", pos);
        if (a == std::string::npos) break;
        size_t b = txt.find("</dependency>", a);
        if (b == std::string::npos) break;
        b += 13;

        std::string block = txt.substr(a, b - a);
        if (xmlTag(block, "groupId") == group &&
            xmlTag(block, "artifactId") == artifact) {
            // 把这段整体挖掉，前头留下的空行也一并吃掉，
            // 否则删一次多一个空行，删三次 pom 里就一堆空行。
            size_t from = a;
            while (from > 0) {
                char c = txt[from - 1];
                if (c == ' ' || c == '\t') from--;
                else break;
            }
            size_t lineStart = txt.rfind('\n', from == 0 ? 0 : from - 1);
            lineStart = (lineStart == std::string::npos) ? 0 : lineStart + 1;
            bool onlySpace = true;
            for (size_t i = lineStart; i < from; i++)
                if (txt[i] != ' ' && txt[i] != '\t') { onlySpace = false; break; }
            if (onlySpace) from = lineStart;

            txt.erase(from, b - from);
            n++;
            pos = from;
        } else {
            pos = b;
        }
    }
    return n;
}

bool pomAdd(const std::string& projectRoot, const Entry& e) {
    if (projectRoot.empty()) return false;

    std::string pomPath = joinPath(projectRoot, "pom.xml");
    std::string txt = readFile(pomPath);
    if (txt.empty()) return false;   // 没有 pom 就不替他建，见头文件里的说明

    // 同一个 artifact 已经声明过就先换掉 —— 这样「换版本」走的是同一条路，
    // 不会出现两条同名的 dependency（Maven 见到后者会静默忽略前者，很难查）。
    pomDropBlocks(txt, e.group, e.artifact);

    std::string ind = pomIndent(txt);
    std::string ind2 = ind + "    ";
    std::string block;
    block += ind + "<dependency>\n";
    block += ind2 + "<groupId>" + e.group + "</groupId>\n";
    block += ind2 + "<artifactId>" + e.artifact + "</artifactId>\n";
    block += ind2 + "<version>" + e.version + "</version>\n";
    block += ind + "</dependency>\n";

    size_t deps = txt.find("<dependencies>");
    if (deps != std::string::npos) {
        size_t end = txt.find("</dependencies>", deps);
        if (end == std::string::npos) return false;
        txt.insert(end, block);
    } else {
        // 没有 <dependencies> 就自己开一节，放在 </project> 前面。
        // 放在那儿而不是文件末尾：末尾可能是 </project> 之后的别的东西。
        size_t proj = txt.rfind("</project>");
        if (proj == std::string::npos) return false;
        std::string section = "    <dependencies>\n" + block + "    </dependencies>\n";
        txt.insert(proj, section);
    }

    return writeFile(pomPath, txt);
}

bool pomRemove(const std::string& projectRoot, const std::string& group,
               const std::string& artifact) {
    if (projectRoot.empty()) return false;

    std::string pomPath = joinPath(projectRoot, "pom.xml");
    std::string txt = readFile(pomPath);
    if (txt.empty()) return false;

    if (pomDropBlocks(txt, group, artifact) == 0) return false;
    return writeFile(pomPath, txt);
}

// ---------------------------------------------------------------- 解析

Resolved resolve(const std::string& projectRoot,
                 const std::vector<std::string>& ids) {
    Resolved r;
    for (const auto& id : ids) {
        const Entry* e = find(id);
        Entry fromCoord;
        if (!e) {
            // 不是目录里的 id，那就当坐标试。都认不出来才跳过。
            if (!parseCoord(id, fromCoord)) continue;
            e = &fromCoord;
        }

        std::string jar = findJar(*e, projectRoot);
        if (jar.empty()) {
            r.missingIds.push_back(id);
        } else {
            r.jars.push_back(jar);
            r.readyIds.push_back(id);
        }
    }
    return r;
}

}  // namespace jlibs
