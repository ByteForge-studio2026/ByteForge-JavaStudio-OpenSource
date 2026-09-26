// 平台抽象层
//
// S++ 一套源码要同时出 EXE 和 APK，差别只在这里：
// 窗口、绘制、输入、生命周期。上面所有东西（语言、资源解析、
// IDE 界面）完全共用。
//
// 这里是最小接口。不追求「一套渲染抽象走天下」——
// 那要写一大堆代码去统一两个差异很大的东西。
// 实际情况是：Windows 走 GDI/D2D，Android 走 ANativeWindow，
// 各自实现这几个函数就够了。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace spp {
namespace plat {
// ---------------------------------------------------------------- 基础

enum class OS { Windows, Android, Unknown };

OS os();
const char* osName();

// 毫秒级单调时钟，动画和帧计时用
uint64_t nowMs();

// 睡眠
void sleepMs(unsigned ms);

// ---------------------------------------------------------------- 窗口

struct WindowDesc {
    std::string title;      // 窗口标题 / Activity 标签
    int width = 1280;
    int height = 800;
    int minWidth = 720;
    int minHeight = 480;
    bool resizable = true;
    bool frameless = false;   // 无边框（自绘标题栏，仿 IDEA 的新 UI）
    bool center = true;
};

// 不需要用户实现的回调，由 UI 层提供函数指针
struct Canvas;   // 前向声明，定义在下面

struct WindowCallbacks {
    // 每帧绘制。canvas 由平台层准备好，
    // 里面已经绑好双缓冲和 HDC / ANativeWindow。
    void (*onPaint)(Canvas* canvas, void* ctx) = nullptr;
    void (*onResize)(int w, int h, void* ctx) = nullptr;
    void (*onMouse)(int x, int y, int buttons, void* ctx) = nullptr;
    void (*onScroll)(int x, int y, int delta, void* ctx) = nullptr;
    void (*onKey)(int vk, bool down, bool ctrl, bool shift, bool alt,
                  void* ctx) = nullptr;
    // Android 返回键 / Windows Alt+F4
    void (*onClose)(void* ctx) = nullptr;
    void* ctx = nullptr;
};

// 创建窗口。返回不透明句柄，失败返回 nullptr。
void* createWindow(const WindowDesc& desc, const WindowCallbacks& cbs);

// ---------------------------------------------------------------- 启动画面
//
// IDEA 那种「小圆角窗口 + 进度条」。
//
// 为什么单独开一个 API 而不是复用 createWindow：
//   1) 尺寸和主窗口差一个数量级（560x340 对 1280x800）
//   2) 它要**分层窗口**（WS_EX_LAYERED + UpdateLayeredWindow），
//      这样四角才是真圆角、能投阴影；普通窗口的圆角外面
//      会露出一块方的白底。
//   3) 它跑完就销毁，主窗口另开。两个窗口的生命周期不同。
//
// 实现上是同步的：showSplash 自己开窗口、画、推上去然后返回，
// 不启动独立消息循环 —— 调用方（main）想什么时候画下一帧
// 就什么时候调 splashStep，中间可以放心地去做耗时的初始化。
void* createSplash(int w, int h);

// 更新启动画面。progress 0..1，stage 是当前在干什么。
// 会自己 Pump 一遍消息，所以拖动窗口不会假死。
void splashStep(void* splash, float progress, const std::string& stage);

// 给启动画面设品牌图标。传的是 SVG 源码 —— 调用方从用户的
// 图标库（icons/ 下那 177 个）里取，比如 iconLib.svgOf("coffee")。
//
// 这里不接受「画几个矩形拼一个图标」这种写法：图标是图标库的事，
// 启动画面只负责把它光栅化到自己的 ARGB 画布上。
// 传空串或者传了解析不出图形的内容，就只留一个空的强调色方块。
void splashSetBrandIcon(const std::string& svg);

// 进入「准备就绪」并等用户点按钮。
//
// 调用后窗口不再自己消失，而是停在 100% 的状态上，显示「准备就绪」
// 和底部两个按钮。返回用户选的那个：
//     1 = 立即进入
//     2 = 退出
//
// 内部一直抽消息直到点击发生，所以调用方不需要（也不能）再自己 pump。
// 窗口被直接关掉时按「立即进入」处理，避免主线程卡死在等待里。
int splashReady(void* splash);

// 销毁启动画面
void destroySplash(void* splash);

// 进入消息循环。返回时程序应当退出。
int runLoop();

// 请求重绘（Android 上是 invalidate）
void requestPaint(void* win);

// 主动退出消息循环
void quitLoop();

// 让一次绘制失效的区域，避免整屏重画
void invalidate(void* win, int x, int y, int w, int h);

// ---------------------------------------------------------------- 绘制

// 不透明绘图上下文。具体是什么，由平台决定。
struct Canvas {
    void* handle = nullptr;   // HDC / ANativeWindow_Buffer*
    int width = 0;
    int height = 0;
    float scale = 1.0f;       // DPI 缩放
};

// 颜色统一用 0xRRGGBB（不含 alpha），比 COLORREF 的 BGR 顺序好记。
// 平台层负责转换。
using Color = uint32_t;

constexpr Color rgb(uint32_t r, uint32_t g, uint32_t b) {
    return ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
}
constexpr uint32_t red(Color c) { return (c >> 16) & 0xFF; }
constexpr uint32_t green(Color c) { return (c >> 8) & 0xFF; }
constexpr uint32_t blue(Color c) { return c & 0xFF; }

// 基础绘制。坐标系原点在左上，单位是像素。
//
// 圆角矩形和斜线都走**软件抗锯齿**：按 4x4 子采样算覆盖率，
// 再和底层像素做 alpha 混合。GDI 自带的 RoundRect 是硬边缘，
// 圆角和斜线会看到明显台阶，现代风的界面不能用。
void fillRect(Canvas& c, int x, int y, int w, int h, Color col);
void fillRoundRect(Canvas& c, int x, int y, int w, int h, int radius, Color col);
void strokeRoundRect(Canvas& c, int x, int y, int w, int h, int radius,
                     Color col, int thickness);
void drawLine(Canvas& c, int x1, int y1, int x2, int y2, Color col, int thickness);

// ---------------------------------------------------------------- 浮点版本
//
// 图标绘制必须用这两个。
//
// 原因：图标是按 24x24 设计稿缩放的。一个 16px 的图标 k≈0.67，
// 把路径端点各自取整到像素格，相邻两点常常落进同一个整数格，
// 线段退化成长度 0 —— 图标就不见了（「图标消失」的根因）。
//
// 浮点版本内部走的是同一套 4x4 子采样光栅化，本来就在浮点域
// 工作，所以传浮点进去是「正常用法」，不是精度妥协。
// thickness 也接受浮点：1.25px 的线在小图标上比 1px 更接近
// 设计稿的观感。
void drawLineF(Canvas& c, float x1, float y1, float x2, float y2, Color col,
               float thickness);
void strokeRoundRectF(Canvas& c, float x, float y, float w, float h,
                      float radius, Color col, float thickness);
void fillRoundRectF(Canvas& c, float x, float y, float w, float h, float radius,
                    Color col);

// 任意半径的圆（图标里的圆点、状态灯用）
void fillCircle(Canvas& c, int cx, int cy, int r, Color col);

// 带 alpha 的填充。alpha 0..255，用于做柔和的高光/阴影。
void fillRoundRectA(Canvas& c, int x, int y, int w, int h, int radius,
                    Color col, int alpha);
void fillRectA(Canvas& c, int x, int y, int w, int h, Color col, int alpha);

// 圆角描边，只在某一侧画（分隔线用细一点的，视觉更干净）
void strokeRoundRectTop(Canvas& c, int x, int y, int w, int h, int radius,
                        Color col, int thickness);

// 文本
struct TextStyle {
    int size = 14;              // 逻辑像素
    Color color = rgb(230, 230, 235);
    bool bold = false;
    bool italic = false;
    bool monospace = false;     // 编辑器用等宽字体
};

// 画一行文本，返回实际宽度
int drawText(Canvas& c, int x, int y, const std::string& utf8,
             const TextStyle& st);

// 测量文本尺寸，不绘制
void measureText(Canvas& c, const std::string& utf8, const TextStyle& st,
                 int* outW, int* outH);

// 每个字形的宽度（等宽字体下都一样，中文编辑器对齐要用）
int charWidth(Canvas& c, const TextStyle& st);

// 文本行高
int lineHeight(Canvas& c, const TextStyle& st);

// 裁剪区
void pushClip(Canvas& c, int x, int y, int w, int h);
void popClip(Canvas& c);

// 清空裁剪栈。每帧开画之前调一次。
//
// 存在的意义是兜底：裁剪栈是全局的，谁要是 pushClip 之后走了
// 提前 return、漏了 popClip，那层裁剪就会一直压着，之后每一帧
// 都只刷新那一小块 —— 表现成「界面错乱、一半是上一个页面的残像」。
// 这类 bug 极难定位，所以每帧强制归零，让它最多只影响出问题的那一帧。
void resetClips(Canvas& c);

// ---------------------------------------------------------------- 文件

// 读整个文件。失败返回空串。
std::string readFile(const std::string& path);

// 写整个文件。成功返回 true。
bool writeFile(const std::string& path, const std::string& data);

bool fileExists(const std::string& path);
bool dirExists(const std::string& path);

// 目录列表
struct DirEntry {
    std::string name;
    bool isDir = false;
    uint64_t size = 0;
};
std::vector<DirEntry> listDir(const std::string& path);

// 只要子目录名，排好序
std::vector<std::string> listDirs(const std::string& path);

// 创建目录（含中间层）
bool makeDirs(const std::string& path);

// 删一个文件。不存在也算成功（调用方要的是「它不在了」，不是「我删到了」）。
bool removeFile(const std::string& path);

// 删一个**空**目录。非空或有东西占着就失败，正好是想要的行为：
// 清缓存时只想收掉那些变空的目录，里面还有东西的必须留着。
bool removeDir(const std::string& path);

// 文件大小（字节）。不存在 / 取不到返回 0。
uint64_t fileSize(const std::string& path);

// 路径拼接，自动处理分隔符
std::string joinPath(const std::string& a, const std::string& b);

// 把路径整理成统一写法：/ 换成 \、连续分隔符折叠、结尾分隔符去掉。
//
// 为什么需要它：项目路径在不同地方拼出来长得不一样（C# 给的是 D:\a\b，
// 内核自己拼的可能是 D:/a/b，命令行传进来的还可能变成 D://a//b）。
// 而 session.txt 的「最近项目」去重是逐字节比字符串的，不规范化就会出现
// 同一个项目存两条、首页列表里重复、删掉一条另一条还在的问题。
std::string normalizePath(const std::string& path);

// 上一级目录。已经是根就返回自己。
std::string parentDir(const std::string& path);

// 路径最后一段。"D:\a\b\Demo" -> "Demo"。空路径返回空串。
std::string baseName(const std::string& path);

// exe 所在目录。注意这是「宿主进程」的目录，不一定是 core DLL 的目录 ——
// 宿主换人（别的程序加载本 DLL）这里就会跟着变。
std::string executableDir();

// 本 DLL 自己所在目录。用 GetModuleHandleEx(FROM_ADDRESS) 反查，
// 所以不用把 DLL 文件名写死在代码里。找不到时返回空串。
std::string dllDir();

// 读环境变量。没有返回空串。
std::string getEnv(const std::string& name);

// 在 PATH 里找一个可执行文件，返回全路径。没有返回空串。
std::string whichExe(const std::string& name);

// 应用数据目录（存配置、日志）
std::string appDataDir(const std::string& appName);

// 覆盖上面那个目录。C# 启动时通过 js_init 把「该往哪写」传进来（D:\S++\.javastudio），
// 之后平台层所有调用点都返回这个目录。
//
// 为什么必须做成全局覆盖而不是逐个调用点改：
//   appDataDir() 在内核里有好几处调用 —— 最近项目(session.txt)、JDK 偏好(settings.txt)、
//   第三方库缓存(libs/)。它们全都散布在各个模块里，默认都落 %APPDATA%（也就是 C 盘）。
//   而用户明确要求「C 盘不留任何文件」，C# 那边的数据目录已经是 D:\S++\.javastudio 了。
//   两边不一致的直接后果就是：C++ 打开项目时把「最近项目」写进 C 盘那份 session.txt，
//   C# 首页却去读 D 盘那份 —— 永远是空的，于是「明明有项目，首页却提示暂无项目」。
//   在这里统一覆盖一次，所有调用点自动跟着走，不会再有第二份文件。
void setAppDataOverride(const std::string& dir);

// 让用户选一个目录。取消返回空串。
// Windows 走 SHBrowseForFolder，Android 走 SAF。
std::string pickFolder(const std::string& title,
                       const std::string& startDir = "");

// 查一个磁盘/分区还剩多少字节。-1 表示查不到。
int64_t freeSpaceOf(const std::string& path);

// 用户主目录。拿不到返回空串。
std::string homeDir();

// ---------------------------------------------------------------- 网络
//
// 只有一个能力：把一个 https 地址抓成文件。
//
// 为什么用 WinHTTP 而不是 WinINet：WinHTTP 走系统代理设置但**不碰**
// IE 的 cookie / 缓存，也不弹任何认证框。后台悄悄拉一个 jar 的时候，
// WinINet 那些行为都是麻烦。
//
// 失败时 err 里带一句人话（HTTP 404 / 连不上 / 写不进盘），
// 界面直接把这句话显示出来，比「下载失败(1)」有用得多。
// 下载进度回调：done = 已经写到盘上的字节数，total = 服务器给的总长度。
//
// total 可能是 0 —— Content-Length 不是必须的，分块传输就没有。
// 收到 0 表示「不知道总共多大」，界面那边别去算百分比，显示已下载字节就好。
//
// 返回 false 表示「别下了」：httpGetToFile 会把已经写下的半个文件删掉再返回失败。
// 留一个截断的 jar 在缓存里，下次会被当成「已经下载好了」，然后编译报一堆莫名其妙的错。
using HttpProgress = bool (*)(unsigned long long done, unsigned long long total,
                              void* user);

bool httpGetToFile(const std::string& url, const std::string& destPath,
                   std::string* err, HttpProgress prog = nullptr,
                   void* progUser = nullptr);

// 把一个 https 地址抓成字符串。
//
// 给「仓库搜索」「maven-metadata.xml」这类小响应用的 —— 它们只有几 KB，
// 没必要落成文件再读一遍。上限 8 MB，超了直接失败：
// 这些接口本来就不该返回大东西，真超了说明 URL 搞错了，
// 与其把几百 MB 读进内存不如早报错。
bool httpGet(const std::string& url, std::string* out, std::string* err);

// ---------------------------------------------------------------- 剪贴板
//
// 走系统剪贴板而不是进程内的变量 —— 用户会从浏览器、从别的编辑器
// 复制代码进来，必须是同一块。UTF-8 进、UTF-8 出。
void clipboardSet(const std::string& utf8);
std::string clipboardGet();

// ---------------------------------------------------------------- 进程

// 运行命令并等它结束，返回退出码。output 收 stdout+stderr。
int runCommand(const std::string& cmd, const std::vector<std::string>& args,
               const std::string& workDir, std::string* output);

// 跑一条命令字符串，把 stdout+stderr 一起返回。
// 给「问一下版本号」这类场景用，不需要退出码。
std::string runCapture(const std::string& cmd);

// ---------------------------------------------------------------- 日志

// 日志同时写文件和控制台（Android 走 logcat）。
// GUI 程序没有 stdout，所以必须有文件兜底。
void logInit(const std::string& file);
void logWrite(const char* level, const std::string& msg);

// 装一个日志回调。除了写文件，每条日志也会同步调它。
// C# 界面靠这个把 C++ 侧发生的事实时显示到「运行日志」面板上。
// cb 签名：void (*)(const char* level, const char* msg, void* ud)
void setLogSink(void (*cb)(const char* level, const char* msg, void* ud),
                void* ud);

#define SPP_LOG_INFO(msg) ::spp::plat::logWrite("INFO", (msg))
#define SPP_LOG_WARN(msg) ::spp::plat::logWrite("WARN", (msg))
#define SPP_LOG_ERR(msg) ::spp::plat::logWrite("ERROR", (msg))

}  // namespace plat
}  // namespace spp
