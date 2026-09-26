// jlibs.h —— 第三方库（Java 依赖）管理
//
// 一个 Java IDE 不能 import 不了 Gson。这里管的就是这件事：
// 内置一份常用库目录，用户勾上哪个，IDE 就把对应的 jar 下下来、
// 放进 javac / java 的 classpath，代码里就能直接 import。
//
// 三件事分开做，互不依赖：
//   1) catalog()   —— 认识哪些库（纯常量表，离线可用）
//   2) findJar()   —— 本机哪里已经有了（Maven 本地仓库 / 项目 lib / 缓存）
//   3) download()  —— 本机没有就去 Maven 中央仓库拿
//
// 为什么不做传递依赖：那要解析 pom 的 <dependencies> 树、处理
// optional / scope / 版本仲裁，是一整个 Maven 解析器。对 IDE 的
// 日常用途来说，用户勾一个 Gson 就想要 gson 本身，把依赖关系留给
// 「项目真有 pom.xml 就走 mvn」那条路更合适。
#pragma once

#include <string>
#include <vector>

namespace jlibs {

// 目录里的一个库。
struct Entry {
    std::string id;         // 稳定短名，界面和配置文件里用它
    std::string group;      // Maven groupId
    std::string artifact;   // Maven artifactId
    std::string version;    // 版本
    std::string name;       // 给人看的名字
    std::string desc;       // 一句话说明
    std::string category;   // 分组用

    // com.google.code.gson:gson:2.11.0
    std::string coord() const {
        return group + ":" + artifact + ":" + version;
    }
    // gson-2.11.0.jar
    std::string fileName() const {
        return artifact + "-" + version + ".jar";
    }
    // com/google/code/gson/gson/2.11.0/gson-2.11.0.jar
    // —— 这是 Maven 仓库的目录约定，缓存目录也照它排，
    //    这样和 .m2 里的东西长得一样，能互相认。
    std::string repoPath() const;
    // 下载地址
    std::string url() const;
};

// 内置目录。按分类排好的。
const std::vector<Entry>& catalog();

// 按 id 找。找不到返回 nullptr。
const Entry* find(const std::string& id);

// 解析 "group:artifact:version"。
//
// 目录表只有 59 个库，用户想要表里没有的（或者直接把 pom 里那一行
// 复制过来）就得认坐标。解析出来的 Entry 里 id 就是坐标本身，
// name/desc 是拼出来的占位 —— 目录项那些字段只有展示用，
// 下载和缓存路径全靠 group/artifact/version，不依赖 id。
bool parseCoord(const std::string& coord, Entry& out);

// 把目录里所有分类名按出现顺序列出来（去重）
std::vector<std::string> categories();

// ---------------------------------------------------------------- 位置

// 本机缓存目录：<AppData>/JavaStudio/libs
std::string cacheDir();

// 该库现在能不能找到。返回 jar 的全路径，找不到返回空串。
//
// 查找顺序（先找到先用）：
//   1) 项目里的 lib/ 和 libs/ —— 用户自己丢进去的 jar 优先级最高
//   2) 缓存目录
//   3) exe 旁边的 libs/ —— 随软件一起分发的离线库
//   4) Maven 本地仓库 ~/.m2/repository
std::string findJar(const Entry& e, const std::string& projectRoot);

// ---------------------------------------------------------------- 下载

// 下载进度。done = 已收到的字节，total = 总长度（0 = 服务器没给长度）。
// 返回 false 就中止这次下载。
using DownloadProgress = bool (*)(unsigned long long done,
                                  unsigned long long total, void* user);

// 从 Maven 中央仓库下到缓存目录。成功时 savedTo 是落地路径。
// prog 可以为空——不需要进度的地方（比如命令行）照旧什么都不传。
bool download(const Entry& e, std::string* err, std::string* savedTo,
              DownloadProgress prog = nullptr, void* progUser = nullptr);

// 缓存里已经有几个 jar
int cacheCount();

// 缓存清单（界面要列出来给人看、给人删，光有一个数字不够）
struct CacheItem {
    std::string coord;      // group:artifact:version
    std::string path;       // jar 的全路径
    uint64_t    size = 0;   // 字节
};
std::vector<CacheItem> cacheList();

// 删掉一个缓存条目。按坐标删 —— 界面拿到的就是坐标，
// 不让它自己拼路径（拼错就删到别的地方去了）。
bool cacheRemove(const std::string& coord);

// 缓存总字节数
uint64_t cacheBytes();

// ---------------------------------------------------------------- 仓库查询

// Maven 中央仓库搜索的一条结果
struct SearchHit {
    std::string group;
    std::string artifact;
    std::string version;    // 最新版
    std::string desc;       // 仓库给的一句话说明，可能为空
};

// 按关键字搜仓库。网络不通 / 超时时 err 里带一句人话。
// rows 是要几条，别贪多 —— 界面一屏也放不下几百条。
std::vector<SearchHit> search(const std::string& query, std::string* err,
                              int rows = 25);

// 某个 artifact 在仓库上有哪些版本（新的在前）。
// 用来做「换版本」那个下拉 —— 让用户手输版本号是不现实的。
std::vector<std::string> versions(const std::string& group,
                                  const std::string& artifact,
                                  std::string* err);

// ---------------------------------------------------------------- 项目选择
//
// 存成 <项目根>/.javastudio/libs.txt，一行一个坐标。
// 为什么不用 pom.xml：用户勾一下库不应该顺带把项目改成 Maven 工程，
// 而且没有 pom 的项目（纯 javac）也得能加库。

std::vector<std::string> loadSelection(const std::string& projectRoot);
bool saveSelection(const std::string& projectRoot,
                   const std::vector<std::string>& ids);

// 从 pom.xml 里扫 <dependency>，返回 "group:artifact:version"。
// 项目本来就是 Maven 的时候，让 IDE 认出它已经声明的依赖，
// 免得用户以为要重新勾一遍。
std::vector<std::string> pomCoords(const std::string& projectRoot);

// 往 pom.xml 的 <dependencies> 里加一条。没有 pom.xml 就返回 false ——
// 不替用户偷偷建一个：把「纯 javac 工程」变成 Maven 工程是件大事，
// 得由他在新建项目时选，或者自己写 pom，不能因为加个库就变了性质。
bool pomAdd(const std::string& projectRoot, const Entry& e);

// 从 pom.xml 里删一条（按 group + artifact 匹配，不看版本）
bool pomRemove(const std::string& projectRoot, const std::string& group,
               const std::string& artifact);

// pom 存不存在
bool pomExists(const std::string& projectRoot);

// ---------------------------------------------------------------- 解析

struct Resolved {
    std::vector<std::string> jars;       // classpath 用
    std::vector<std::string> readyIds;   // 已经就绪的
    std::vector<std::string> missingIds; // 还没下到本机的
};

// 把选中的库解析成本机 jar 列表。
// ids 里既可以放目录表的 id（"gson"），也可以放完整坐标
//（"com.google.code.gson:gson:2.11.0"）—— 后者是自定义依赖唯一的写法。
Resolved resolve(const std::string& projectRoot,
                 const std::vector<std::string>& ids);

// classpath 分隔符（Windows 是分号）
char separator();

}  // namespace jlibs
