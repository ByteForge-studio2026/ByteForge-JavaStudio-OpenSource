// 配色
//
// 用户的硬要求：**只用黑、灰、白，加红和绿**。
// 红=报错，绿=正确。不要花哨的蓝紫色，那是 IDE 的常见毛病。
//
// 深色为主（编辑器长时间看不累），浅色作为可选主题。
#pragma once

#include <cstdint>

#include "platform/platform.h"

namespace spp {
namespace ui {

using plat::Color;
using plat::rgb;

// ---------------------------------------------------------------- 深色（默认）

struct Palette {
    // 背景由深到浅的层次。IDEA 新 UI 就是靠这几层区分区域，
    // 不靠边框线，所以这里层差要够明显。
    Color bgDeepest;     // 最底：编辑器外的空隙
    Color bgBase;        // 主背景：编辑区、树
    Color bgPanel;       // 面板：工具栏、侧栏
    Color bgRaised;      // 浮起：下拉、对话框
    Color bgHover;       // 悬停
    Color bgActive;      // 选中/按下
    Color bgSelection;   // 文本选区

    // 文字
    Color fgPrimary;     // 正文
    Color fgMuted;       // 次要说明
    Color fgDim;         // 行号、占位
    Color fgInvert;      // 反色文字（在亮底上）

    // 分隔
    Color border;
    Color borderFocus;
    Color divider;

    // 语义色：全项目只有这两个彩色
    Color error;         // 红
    Color ok;            // 绿
    Color errorBg;       // 红底（诊断行高亮）
    Color okBg;

    // 语法高亮：靠灰阶拉开层次，不引入新色相
    Color synKeyword;
    Color synString;
    Color synNumber;
    Color synComment;
    Color synFunction;
    Color synType;
    Color synOperator;
    Color synVariable;
    Color synColorRef;   // @primary 这类颜色引用
    Color synDirective;  // //@page

    // 日志级别
    Color logInfo;
    Color logWarn;
    Color logError;
    Color logOk;

    bool dark = true;
};

// 深色主题
const Palette& dark();
// 浅色主题
const Palette& light();

// 当前主题（默认深色）
const Palette& current();
void setDark(bool d);

// ---------------------------------------------------------------- 尺寸
//
// 现代风的两个要点：**留白够大**、**圆角够圆**。
// 圆角半径普遍比 IDEA 大一圈：按钮 8、卡片 12、面板 14。
// 全圆角的意思是「凡是矩形，边上都有圆角」，没有直角。

struct Metrics {
    int menuH = 30;           // 菜单栏高度（文件 / 编辑 / 视图 …）
    int toolbarH = 44;        // 工具栏高度
    int statusH = 26;         // 底部状态栏
    int sidebarW = 260;       // 左侧项目树
    int tabH = 36;            // 标签页高度
    int btnSize = 30;         // 正方形按钮边长
    int btnMinW = 30;         // 圆角横条按钮最小宽度
    int btnGap = 6;           // 按钮间距
    int btnRadius = 8;        // 按钮圆角
    int cardRadius = 12;      // 卡片圆角
    int panelRadius = 14;     // 大面板/对话框圆角
    int rowRadius = 8;        // 列表行圆角
    int chipRadius = 999;     // 胶囊（完全圆头）

    int padX = 12;            // 水平内边距
    int padY = 8;
    int lineH = 24;           // 编辑器/树的行高

    int fontSize = 14;
    int fontSmall = 12;
    int fontEditor = 14;
    int gutterW = 56;         // 行号栏宽度
};

const Metrics& metrics();

}  // namespace ui
}  // namespace spp
