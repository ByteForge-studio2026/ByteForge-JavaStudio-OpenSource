// win32_internal.h —— win32.cpp / splash.cpp 共用的东西
//
// 启动画面和主窗口的绘制走的是同一套 GDI 工具：
// 建字体、转宽字符、0xRRGGBB 转 COLORREF。这些本来在 win32.cpp
// 的匿名命名空间里，启动画面单独成一个文件后就得拿出来共享。
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <string>

#include "platform/platform.h"

namespace spp {
namespace plat {
namespace w32 {

// 0xRRGGBB → COLORREF(0x00BBGGRR)
COLORREF toRef(Color c);

// UTF-8 <-> UTF-16
std::wstring toWide(const std::string& utf8);
std::string toUtf8(const std::wstring& w);

// 字体缓存。CreateFont 很贵，同一个字号/粗细只建一次。
// st.monospace 走 Consolas，否则走微软雅黑。
HFONT getFont(const TextStyle& st);

// 用当前字体度量一段文字（不绘制）
void measureTextW(HDC dc, const std::wstring& w, HFONT f, int* outW, int* outH);

// 画一段 UTF-8 文本，返回实际宽度。透明背景。
int drawTextW(HDC dc, int x, int y, const std::string& utf8,
              const TextStyle& st);

// 软件抗锯齿：圆角矩形填充 / 描边、圆
void aaFillRoundRect(HDC dc, int x, int y, int w, int h, int r, Color col);
void aaStrokeRoundRect(HDC dc, int x, int y, int w, int h, int r, Color col,
                       int thickness);
void aaFillCircle(HDC dc, int cx, int cy, int rad, Color col);

// 在指定 DC 上按 0xRRGGBB 填充一个硬边矩形
void fillRectOn(HDC dc, int x, int y, int w, int h, Color col);

}  // namespace w32
}  // namespace plat
}  // namespace spp
