// app.h —— Java Studio 的界面状态与共享类型
//
// 这里只放「界面自己」的东西：当前在哪个界面、开了哪些文件、
// 面板滚动到哪、对话框里填了什么。
//
// 真正的活（找 JDK、编译、读 jar）都在各自模块里，这里只调用。
#pragma once

#include <string>
#include <vector>

#include "build/javabuild.h"
#include "icons/iconlib.h"
#include "jar/jar.h"
#include "jdk/jdk.h"
#include "jlex/javalex.h"
#include "platform/platform.h"
#include "ui/theme.h"
#include "ui/widgets.h"

namespace app {

// platform 和 jar 都在别的命名空间里，进本命名空间时先拉进来。
// 注意必须写全 spp::plat —— 直接写 using plat:: 是找不到的，
// 因为 plat 是嵌在 spp 里面的。
using spp::plat::Canvas;
using spp::plat::Color;
using spp::plat::DirEntry;
using spp::plat::TextStyle;
using spp::plat::clipboardGet;
using spp::plat::clipboardSet;
using spp::plat::logWrite;

using spp::icons::Library;
using spp::ui::Input;
using spp::ui::LogLevel;
using spp::ui::LogLine;
using spp::ui::TreeNode;

// ---------------------------------------------------------------- 界面

enum class Screen {
    Home,      // 起始页：新建 / 打开 / 最近
    Editor,    // 工作区：项目树 + 编辑器 + 底部面板
    Jar,       // jar 查看器：直接打开一个 jar
    Settings,  // 设置：选默认 JDK
};

// 底部面板的页签
enum class BottomTab { Run, Problems, Build };

// 模态框
enum class Dialog { None, NewProject, NewFile, About, Libraries };

// 文件树里一个节点对应什么
enum class NodeKind { Dir, JavaFile, OtherFile, JarFile };

// 项目树节点。在 widgets::TreeNode 上挂了几个字段：
//   1) out 侧栏自己滚动的 key —— 面板级滚动用，和节点无关
//   2) collapsed —— 目录折叠状态。drawTree 只会展开/收起
//      （它靠自己那套推导），这里跟着同步一份，重新建树时用得上
struct Node {
    TreeNode ui;
    NodeKind kind = NodeKind::OtherFile;
    bool collapsed = false;
};

// ---------------------------------------------------------------- 打开的文件
//
// 一个文件 = 一份行数组 + 光标 + 滚动量。
//
// 为什么存行数组而不是一大段字符串：光标、滚动、着色都是按行来的，
// 每改一个字符就重新拆一整行太浪费。存行数组，改行就是改一个
// std::string，光标位置天然就是 (行, 列)。
struct OpenFile {
    std::string path;
    std::string title;
    std::vector<std::string> lines;

    // 编辑状态
    int caretLine = 0;
    int caretCol = 0;        // 字节偏移，落在行内
    int selAnchorLine = -1;  // 选区起点。和 caret 相同表示没有选区
    int selAnchorCol = -1;
    int scrollY = 0;
    int scrollX = 0;

    bool dirty = false;
    bool isJava = false;

    // 行内着色结果，和 lines 一一对应。改行只重算那一行。
    std::vector<std::vector<jlex::Span>> spans;
    // 逐行的解析状态，跨行块注释用。spans 和它配套。
    std::vector<char> inBlock;
    std::vector<char> inText;

    // 每行行首的（字节 -> 像素）映射，光标定位要反查。
    // 按需生成，存的是累积宽度表。
    int cachedLine = -1;
    std::vector<int> cachedPos;   // 尺寸 = 该行字节数 + 1

    std::string text() const {
        std::string s;
        for (size_t i = 0; i < lines.size(); i++) {
            s += lines[i];
            if (i + 1 < lines.size()) s += "\n";
        }
        return s;
    }
};

// ---------------------------------------------------------------- 新建项目表单

struct NewProjectForm {
    std::string parentDir;
    std::string name;
    std::string group = "com.example";
    std::string artifact;
    std::string jdkRelease = "17";   // maven.compiler.release
    int field = 0;           // 0=名字 1=包名 2=构件名
    int kind = 0;            // 对应 javaproj::Kind
    int tool = 1;            // 对应 javaproj::BuildTool（默认 Maven）
    std::string error;
};

// ---------------------------------------------------------------- 第三方库
//
// 勾选状态。界面只改这份「想要哪些」，真正的 jar 解析（本机有没有、
// 要不要下）在 syncLibs() 里统一做 —— 界面不碰磁盘。
struct LibPicker {
    std::string query;                 // 搜索框内容
    std::string category;              // 分类筛选，空 = 全部
    int scroll = 0;
    int hover = -1;
    std::vector<std::string> selected;   // 勾中的 id

    bool isSelected(const std::string& id) const {
        for (const auto& s : selected)
            if (s == id) return true;
        return false;
    }

    void toggle(const std::string& id) {
        for (size_t i = 0; i < selected.size(); i++) {
            if (selected[i] == id) {
                selected.erase(selected.begin() + i);
                return;
            }
        }
        selected.push_back(id);
    }
};

// ---------------------------------------------------------------- 应用

struct App {
    Screen screen = Screen::Home;
    Library* icons = nullptr;
    Input in;

    int winW = 1280;
    int winH = 800;

    // ---- 顶栏 / 菜单
    int openMenu = -1;          // 展开的一级菜单，-1 = 没展开
    bool menuWasOpen = false;   // 上一帧是不是展开着（用来吃掉那次点击）

    // ---- 侧栏
    bool sidebarOpen = true;
    int sidebarW = 260;
    bool sidebarDragging = false;
    std::vector<Node> tree;
    std::vector<int> treeParent;    // 父子关系，折叠时整棵子树一起藏
    int treeSelected = -1;
    int treeScroll = 0;
    bool treeFoldersOpen = true;

    // ---- 编辑器
    std::vector<OpenFile> files;
    int activeTab = -1;

    // ---- 底部
    // 默认停在「运行日志」。以前默认「构建输出」，可打开项目时压根没编译过，
    // 那一页是空的，用户就以为「打开项目没有日志」。
    BottomTab bottom = BottomTab::Run;
    bool bottomOpen = true;
    int bottomH = 170;
    int bottomDragging = false;
    int logScroll = 0;
    int buildScroll = 0;
    int problemScroll = 0;
    std::vector<LogLine> logs;
    std::vector<LogLine> buildOut;
    std::vector<jbuild::Diagnostic> diags;

    // ---- 起始页
    std::vector<std::string> recent;
    int recentHover = -1;
    int recentScroll = 0;
    std::string curDir;         // 文件浏览器当前目录
    std::vector<DirEntry> curEntries;
    int fileScroll = 0;
    int fileHover = -1;

    // ---- jar 查看器
    jarpack::Info jar;
    int jarScroll = 0;
    int jarSelected = -1;

    // ---- 设置页
    std::vector<jdk::Info> jdks;
    int jdkScroll = 0;
    int jdkHover = -1;

    // ---- 对话框
    Dialog dlg = Dialog::None;
    NewProjectForm np;
    std::string newFileName;
    int newFileCaret = 0;

    // ---- 项目
    std::string projectRoot;
    std::string projectName;

    // ---- 第三方库
    LibPicker libs;
    std::vector<std::string> libJars;      // 已经解析出来的 jar 全路径
    std::vector<std::string> libMissing;   // 还没下到本机的
    std::string libMsg;                    // 「已就绪 3 个」这类一句话
    int libHoverId = -1;                   // 列表里鼠标停在第几条
    double libLastSync = 0;                // 上次同步的时刻（毫秒）

    // ---- 状态栏
    std::string statusLeft;
    std::string statusMid;
    std::string statusRight = "Java Studio";

    // ---- 编译产物 / 主类
    std::string lastMainClass;
    int lastExitCode = 0;

    // ---- 鼠标位置感知滚动用
    // 记录上一位光标在哪个面板，滚轮事件按它分发。
    enum class Pane { None, Sidebar, Editor, Bottom, Recent, Jar, Settings };
    Pane hoverPane = Pane::None;
    Pane scrollPane = Pane::None;

    // ---- 运行耗时统计
    double lastBuildSeconds = 0;

    int activeIndex() const {
        return (activeTab >= 0 && activeTab < (int)files.size()) ? activeTab : -1;
    }

    OpenFile* active() {
        int i = activeIndex();
        return i < 0 ? nullptr : &files[i];
    }

    void log(LogLevel lv, const std::string& s) {
        logs.push_back({lv, s});
        if (logs.size() > 3000) logs.erase(logs.begin(), logs.begin() + 800);
        const char* tag = lv == LogLevel::Error   ? "ERROR"
                          : lv == LogLevel::Warn  ? "WARN"
                          : lv == LogLevel::Ok    ? "OK"
                                                  : "INFO";
        logWrite(tag, s);
    }

    // 编译输出单独一栏。不硬抢视线 —— 只有用户本来就在看这栏时才切过去。
    void build(LogLevel lv, const std::string& s) {
        buildOut.push_back({lv, s});
        if (buildOut.size() > 3000)
            buildOut.erase(buildOut.begin(), buildOut.begin() + 800);
    }

    void problem(LogLevel lv, const std::string& s) {
        logs.push_back({lv, s});
    }
};

App& get();

// 找资源目录（图标库要）
std::string findAssetsDir();

// 项目默认放在哪：安装目录下的 item 文件夹。
// 目录不存在时会建出来；装到写不进的地方就退回用户目录。
std::string projectsDir();

// 把一个路径加进「最近打开」，去重 + 置顶
void pushRecent(App& a, const std::string& path);
void saveSession(App& a);
void loadSession(App& a);

// 打开项目 / 打开单个文件 / 打开 jar
bool openProject(App& a, const std::string& root);
void openPath(App& a, const std::string& path);   // 按扩展名自己判断
void openJar(App& a, const std::string& path);
bool loadFile(App& a, const std::string& path);
bool saveFile(App& a, int idx);
bool saveAll(App& a);

// 重新建项目树
void rebuildTree(App& a);

// 编译 / 运行 / 检查
void doCompile(App& a, bool thenRun);
void doRun(App& a);
void doCheck(App& a);

// 打开新建项目对话框
void beginNewProject(App& a);

// ---------------------------------------------------------------- 第三方库

// 打开「第三方库」对话框。会把项目里已选的库读进来。
void openLibraryDialog(App& a);

// 把 a.libs.selected 落盘，并重新解析成 classpath。
// verbose=true 时把过程写进「构建输出」，给用户一个交代。
void syncLibs(App& a, bool verbose);

// 下载某一个库到本机缓存。成功返回 true。
// 会自己更新 a.libMissing / a.libJars，并往日志里写一行。
bool fetchLib(App& a, const std::string& id);

// 把当前 classpath 拼成一个用 ';' 连起来的字符串，给 java -cp 用。
std::string libClasspathString(App& a);

// 真正执行新建（对话框里点「创建」时调）
void createProjectNow(App& a);
void createFileNow(App& a);

// ---------------------------------------------------------------- 文本工具

// UTF-8 与「字符列」的换算。
//
// 光标在内部是**字节偏移**（切片、着色、序列化都按字节走），
// 但状态栏显示的「列」必须是**码点列** —— 否则一个中文占 3 列，
// 状态栏会显示成 12:37 这种莫名其妙的数字。
int utf8Len(unsigned char b);
int utf8Prev(const std::string& s, int bytePos);
int utf8Next(const std::string& s, int bytePos);
std::string utf8Encode(unsigned cp);
int displayColOf(const std::string& line, int byteCol);
int byteColOfDisplay(const std::string& line, int dispCol);

// ---------------------------------------------------------------- 编辑操作

void recolorAll(OpenFile& f);
void recolorLine(OpenFile& f, int lineIndex);
void ensureLineCache(App& a, OpenFile& f, int lineIndex, const TextStyle& st,
                     Canvas& c);
int colToX(App& a, OpenFile& f, int lineIndex, int col, Canvas& c,
           const TextStyle& st);
int xToCol(App& a, OpenFile& f, int lineIndex, int x, Canvas& c,
           const TextStyle& st);

void setCaret(App& a, int line, int col, bool extend);
void moveCaret(App& a, int dLine, int dCol, bool extend);
void moveLineEdge(App& a, bool toEnd, bool extend);
void moveWord(App& a, bool forward, bool extend);
void selectAll(App& a);
bool hasSelection(App& a);
std::string selectedText(App& a);

void insertText(App& a, const std::string& utf8);
void insertNewline(App& a);
void backspace(App& a);
void deleteForward(App& a);
void indentSelection(App& a, bool out);

void cutSelection(App& a);
void copySelection(App& a);
bool pasteFromClipboard(App& a);

void scrollCaretIntoView(App& a, Canvas& c, const TextStyle& st, int bodyY,
                         int bodyH);

// ---------------------------------------------------------------- 入口

// 程序主体。WinMain 只负责把命令行转成 UTF-8 参数数组，然后调它。
//
// 之所以拆开：WinMain 必须留在**全局作用域**（写在命名空间里链接器
// 找不到，报 undefined symbol: WinMain）；而主体里要调一堆内部
// static 函数，待在 app 命名空间里最省事。
//
// args[0] 是程序名，真正的参数从 args[1] 开始。
int appRun(const std::vector<std::string>& args);

}  // namespace app
