// api.h —— 给 C# 界面层用的 C 接口
//
// 界面换成 C#（WPF）之后，C++ 这一侧只留下「真正干活的」部分：
// JDK 探测、项目骨架、编译运行、jar 解析、第三方库下载、图标解析。
// 这一层对外只暴露 C 函数，原因很实在：
//
//   1) P/Invoke 只能封送 C 的东西。C++ 的 std::string、std::vector、
//      异常、名字修饰，跨过去全是坑。
//   2) 接口一旦是 C 的，两边就可以各自演进，不用同步改 ABI。
//
// 参数方向：**扁平参数进，JSON 出**。
// 进的方向故意不收 JSON —— 省掉在 C++ 里写一个 JSON 解析器，
// 而且 DllImport 封送几个字符串本来就很直接。
// 出的方向统一 JSON，是因为返回的东西往往是列表/树，
// 用 JSON 一次说清楚比一堆 out 参数好维护。
//
// 内存约定：所有返回的 char* 都由 DLL 内部 new[] 出来，
// 调用方（C#）用完必须调 js_free 释放。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define JSAPI __declspec(dllexport)
#else
#define JSAPI __attribute__((visibility("default")))
#endif

// ---------------------------------------------------------------- 生命周期

// appDataDir：日志和配置的存放目录（C# 传 %APPDATA%\JavaStudio）。
// exeDir：exe 所在目录，用来找 jdk/、assets/、libs/。
JSAPI void js_init(const char* appDataDir, const char* exeDir);
JSAPI void js_shutdown(void);

// 释放 js_* 返回的字符串。传 nullptr 是安全的。
JSAPI void js_free(char* s);

// 装一个日志回调。核心里的 logWrite 除了写文件，也会调它，
// 这样 C# 的「运行日志」面板能实时看到 C++ 侧发生了什么。
// cb 的函数签名：void (*)(const char* level, const char* msg, void* ud)
JSAPI void js_set_log_sink(void* cb, void* ud);

// ---------------------------------------------------------------- JDK

// [{"major":21,"version":"21.0.1","vendor":"Eclipse Adoptium",
//   "home":"D:\\...","label":"JDK 21.0.1","ok":true}, ...]
JSAPI char* js_jdk_list(void);

// 当前选中的 JDK home；没有 JDK 时 ok=false
JSAPI char* js_jdk_current(void);

// 切换当前 JDK。成功返回 1。
JSAPI int js_jdk_select(int major);

JSAPI void js_jdk_rescan(void);

// ---------------------------------------------------------------- 项目

// 打开一个项目目录。返回：
// {"ok":true,"name":"demo","tool":"maven","mainClass":"com.example.Main",
//  "libs":{"jars":[...],"ready":[...],"missing":[...]},
//  "tree":[{"name":"src","path":"...","dir":true,"children":[...]}]}
JSAPI char* js_open_project(const char* root);

// 只要目录树，不做别的事。
JSAPI char* js_dir_tree(const char* root);

// 校验项目名 / 包名。返回人话的错误说明；没问题返回空串。
JSAPI char* js_validate_project(const char* name, const char* group,
                                const char* artifact);

// 建项目。参数照 IntelliJ IDEA 的新建项目向导：
//   buildSystem : "maven" | "gradle" | "none"（IDEA 的 IntelliJ 模式）
//   jdkRelease  : "17" / "21" / "25"，写进 pom 的 maven.compiler.release
//   addSampleCode: 1=生成示例代码（IDEA 的 Add sample code），
//                  0=只生成目录骨架和构建文件，一个 .java 都不写
//   createGitRepo: 1=建完跑一次 git init（IDEA 的 Create Git repository）
// 返回 {"ok":true,"error":"","created":["src/main/java", ...]}
JSAPI char* js_create_project(const char* parentDir, const char* name,
                              const char* group, const char* artifact,
                              const char* version, const char* buildSystem,
                              const char* jdkRelease, int addSampleCode,
                              int createGitRepo);

// 新建项目的默认父目录（安装目录下的 item）。
JSAPI char* js_default_projects_dir(void);

// 猜主类。返回 "com.example.Main" 或空串。
JSAPI char* js_guess_main(const char* root);

// ---------------------------------------------------------------- 文件

// {"ok":true,"text":"...","error":""}
JSAPI char* js_read_file(const char* path);
// {"ok":true,"error":""}
JSAPI char* js_write_file(const char* path, const char* text);
// [{"name":"Main.java","path":"...","dir":false}, ...]
JSAPI char* js_list_dir(const char* path);

// ---------------------------------------------------------------- 构建

// classpath 用分号隔开（Windows 约定）。
// 返回 {"ok":true,"exitCode":0,"tool":"javac","output":"...",
//       "errorCount":0,"warnCount":0,"seconds":1.2,
//       "diags":[{"level":"error","file":"...","line":5,"col":9,
//                 "message":"..."}]}
JSAPI char* js_compile(const char* root, const char* jdkHome,
                       const char* classpath, int verbose);
JSAPI char* js_run(const char* root, const char* jdkHome, const char* mainClass,
                   const char* classpath);

// 只做语法/括号检查，不起编译器。
JSAPI char* js_check(const char* root);

// ---------------------------------------------------------------- 第三方库

// [{"id":"gson","name":"Gson","group":"com.google.code.gson",
//   "artifact":"gson","version":"2.11.0","desc":"...","category":"JSON",
//   "cached":true}, ...]
JSAPI char* js_libs_catalog(void);

// 项目里已经勾上的库 id，分号隔开
JSAPI char* js_libs_selection(const char* root);
JSAPI int js_libs_save(const char* root, const char* idsSemicolon);

// {"jars":[...],"ready":[...],"missing":[...]}
JSAPI char* js_libs_resolve(const char* root, const char* idsSemicolon);

// 下载一个库。{"ok":true,"path":"...","error":""}
// id 也可以是完整坐标（group:artifact:version）—— 目录表里没有的库
// 只能靠坐标下，见 jlibs::parseCoord。
JSAPI char* js_libs_download(const char* id);

// 带进度的版本。cb 会在下载过程中被同步调用多次：
//   done  = 已经写进文件的字节数
//   total = 服务器给的 Content-Length（0 = 没给，这时候别算百分比）
//   返回 0 → 中止下载（已经写下的半个文件会被删掉）
// 界面要这么做：放到后台线程调（同步会卡 UI），在 cb 里攒着节流后再回 UI 线程刷进度条。
typedef int (*js_download_progress)(unsigned long long done,
                                    unsigned long long total, void* user);
JSAPI char* js_libs_download_ex(const char* id, js_download_progress cb,
                                void* user);

// 搜 Maven 中央仓库。网络耗时，界面那边必须放到后台线程。
// {"ok":true,"error":"","hits":[{"group":"...","artifact":"...","version":"..."}]}
JSAPI char* js_libs_search(const char* query);

// 某个 artifact 在仓库上有哪些版本（新的在前）。
// {"ok":true,"error":"","versions":["3.5.3","3.5.2",...]}
JSAPI char* js_libs_versions(const char* group, const char* artifact);

// 本地缓存清单。{"items":[{"coord":"g:a:v","path":"...","size":12345}]}
// size 是字节。界面列出来给人看、给人删，光有个数量不够。
JSAPI char* js_libs_cache_list(void);

// 删掉缓存里的一个 jar（按坐标）。成功返回 1。
JSAPI int js_libs_cache_remove(const char* coord);

// 项目 pom.xml 里已经声明的依赖，["g:a:v", ...]
JSAPI char* js_libs_pom_coords(const char* root);

// 有没有 pom.xml。没有的话 pom_add / pom_remove 都会失败 ——
// IDE 不替用户把「纯 javac 工程」变成 Maven 工程。
JSAPI int js_libs_pom_exists(const char* root);

// 往 pom.xml 的 <dependencies> 里加一条（坐标形式）。
// 同一个 artifact 已经存在就先换掉，所以「换版本」走的也是这一条路。
JSAPI int js_libs_pom_add(const char* root, const char* coord);

// 从 pom.xml 里删一条，按 group + artifact 匹配（不看版本）。
JSAPI int js_libs_pom_remove(const char* root, const char* group,
                             const char* artifact);

// ---------------------------------------------------------------- jar

// {"ok":true,"path":"...","mainClass":"...","classCount":12,
//  "resourceCount":3,"javaVersion":"Java 17","error":"",
//  "entries":[{"name":"com/example/Main.class","size":123,"dir":false}]}
JSAPI char* js_jar_info(const char* path);

// ---------------------------------------------------------------- 图标
//
// 复用 C++ 里那个已经过验证的 SVG 解析器（177/177 全通过），
// 不打算在 C# 里重写一遍。返回的图形按 24x24 设计坐标系给出，
// C# 侧按需要缩放。
//
// {"coffee":[{"kind":"path","closed":false,"pts":[1,2,3,4]},
//            {"kind":"circle","cx":12,"cy":12,"r":5}, ...], ...}
JSAPI char* js_icons_all(const char* iconsDir);

// ---------------------------------------------------------------- 设置 / 最近项目

JSAPI char* js_settings_get(const char* key);
JSAPI void js_settings_set(const char* key, const char* value);

// 构建输出面板里我们自己拼的那几行提示说哪国话（javac/mvn 自己吐的原样透传）。
// C# 切语言时调：js_build_set_lang("en") / js_build_set_lang("zh")。
JSAPI void js_build_set_lang(const char* lang);

// ["D:\\projects\\demo", ...]
JSAPI char* js_recent(void);
JSAPI void js_recent_add(const char* root);
JSAPI void js_recent_remove(const char* root);
JSAPI void js_recent_clear(void);

// ---------------------------------------------------------------- 杂项

// 编进内核的那个版本号：{"version":"0.1.0-Preview.1"}
//
// 值是构建时 -DJS_VERSION 传进来的（mk.py 读包根的 VERSION 文件）。
// 「关于」页问内核要，而不是界面自己抄一份 —— 抄的那份升级之后就变成假的了。
JSAPI char* js_version(void);

// 某个盘的剩余字节数。安装程序选磁盘时要显示空间够不够。
JSAPI long long js_free_space(const char* path);

#ifdef __cplusplus
}
#endif
