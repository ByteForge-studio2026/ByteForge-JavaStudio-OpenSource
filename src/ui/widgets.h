// 控件
//
// 所有控件都是「立即模式」：给一个矩形和一个状态，画出来，
// 返回有没有被点到。不做控件树、不做事件分发 ——
// IDE 的界面结构是固定的几块，立即模式代码少得多。
#pragma once

#include <string>
#include <vector>

#include "icons/iconlib.h"
#include "platform/platform.h"
#include "ui/theme.h"

namespace spp {
namespace ui {

using plat::Canvas;
using plat::Color;
using plat::TextStyle;

// ---------------------------------------------------------------- 输入状态

struct Input {
    int mouseX = -1;
    int mouseY = -1;
    bool mouseDown = false;      // 左键按下中
    bool clicked = false;        // 这一帧发生了左键「按下」
    int scroll = 0;              // 这一帧的滚轮格数
    int clickX = -1;             // 点击位置
    int clickY = -1;
    bool ctrl = false;
    bool shift = false;

    // 一帧处理完要清掉瞬时状态
    void endFrame() {
        clicked = false;
        scroll = 0;
    }
};

// 命中测试
inline bool hit(int x, int y, int w, int h, int px, int py) {
    return px >= x && px < x + w && py >= y && py < y + h;
}

// ---------------------------------------------------------------- 按钮

// 顶栏按钮的两种形状。用户要求：
// 「小正方形」和「圆角横长方形」，**高度必须一样**。
enum class BtnShape {
    Square,     // 正方形：只有图标，无文字
    Wide,       // 圆角横长方形：图标 + 文字
};

struct ButtonResult {
    bool clicked = false;
    bool hovered = false;
};

// 画一个按钮。
//
// 正方形时 w 自动取 h；横长方形时按文字宽度算，但 h 始终等于
// metrics().btnSize —— 这样两种按钮在一行里高度天然对齐。
ButtonResult drawButton(Canvas& c, const Input& in, const icons::Library* icons,
                        const std::string& iconName, const std::string& label,
                        BtnShape shape, int x, int y, int h, bool active,
                        bool enabled = true);

// 预估横长方形按钮的宽度，用于布局
int buttonWidth(Canvas& c, const std::string& label, BtnShape shape, int h);

// ---------------------------------------------------------------- 标签页

struct Tab {
    std::string title;
    bool dirty = false;      // 未保存，标题后加圆点
    std::string iconName;
};

// 画标签栏，返回被点的下标，-1 表示没点
int drawTabBar(Canvas& c, const Input& in, const std::vector<Tab>& tabs,
               int active, int x, int y, int w, int h);

// ---------------------------------------------------------------- 项目树

struct TreeNode {
    std::string name;
    std::string iconName;
    int depth = 0;
    bool isDir = false;
    bool expanded = false;
    bool isFile = false;
    std::string path;
    Color tint = 0;          // 0 表示用默认色
    bool hasError = false;   // 诊断有错，图标用红
};

// 画树，返回被点的节点下标，-1 表示没点，-2 表示点了展开箭头
int drawTree(Canvas& c, const Input& in, const icons::Library* icons,
             const std::vector<TreeNode>& nodes, int selected, int x, int y,
             int w, int h, int* scroll);

// ---------------------------------------------------------------- 日志

enum class LogLevel { Info, Warn, Error, Ok };

struct LogLine {
    LogLevel level = LogLevel::Info;
    std::string text;
};

// 画日志面板。wheel 是本帧滚轮格数，scroll 是持久滚动位置。
void drawLog(Canvas& c, const std::vector<LogLine>& lines, int x, int y, int w,
             int h, int* scroll, int wheel);

// ---------------------------------------------------------------- 状态栏

void drawStatusBar(Canvas& c, const std::string& left, const std::string& mid,
                   const std::string& right, int x, int y, int w, int h);

// ---------------------------------------------------------------- 面板

// 带标题的面板容器
void drawPanel(Canvas& c, const std::string& title, int x, int y, int w, int h);

// 水平/垂直分隔线
void drawDividerH(Canvas& c, int x, int y, int w);
void drawDividerV(Canvas& c, int x, int y, int h);

// ---------------------------------------------------------------- 文本工具

// 按可用宽度把一行文本切开（中文按字符，英文按词），
// 返回实际画了几行。编辑器软换行和日志换行都用它。
int wrapText(Canvas& c, const std::string& text, const TextStyle& st, int maxW,
             std::vector<std::string>& out);

// 省略号截断
std::string ellipsize(Canvas& c, const std::string& text, const TextStyle& st,
                      int maxW);

}  // namespace ui
}  // namespace spp
