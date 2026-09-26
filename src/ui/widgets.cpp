#include "ui/widgets.h"

#include <algorithm>

namespace spp {
namespace ui {

// ---------------------------------------------------------------- 宽度估算
//
// 中文按整字宽算，英文按 0.55 字宽。这个估算只用于布局，
// 真实宽度还要问平台层。估算是为了避免每画一个按钮都量一次文本。

namespace {

int estimateWidth(Canvas& c, const std::string& s, const TextStyle& st) {
    int cw = plat::charWidth(c, st);
    int w = 0;
    for (size_t i = 0; i < s.size();) {
        unsigned char ch = (unsigned char)s[i];
        if (ch < 0x80) {
            w += (int)(cw * 0.55f);
            i++;
        } else {
            // UTF-8 多字节：按字符算一个全角宽
            int len = (ch >= 0xF0) ? 4 : (ch >= 0xE0) ? 3 : 2;
            w += cw;
            i += len;
        }
    }
    return w;
}

}  // namespace

int buttonWidth(Canvas& c, const std::string& label, BtnShape shape, int h) {
    // 正方形：宽 = 高，这是硬要求
    if (shape == BtnShape::Square) return h;

    TextStyle st;
    st.size = metrics().fontSize - 1;
    int tw = label.empty() ? 0 : estimateWidth(c, label, st);

    // 图标 + 间距 + 文字 + 左右留白
    if (label.empty()) return h;
    int iconW = (int)(h * 0.5f);
    int gap = 6;
    return iconW + gap + tw + 18;
}

// ---------------------------------------------------------------- 按钮

ButtonResult drawButton(Canvas& c, const Input& in, const icons::Library* icons,
                        const std::string& iconName, const std::string& label,
                        BtnShape shape, int x, int y, int h, bool active,
                        bool enabled) {
    const Palette& p = current();
    ButtonResult r;

    // 正方形的高度即宽度。两种形状共用 h，所以顶栏永远对齐。
    int w = buttonWidth(c, label, shape, h);

    r.hovered = hit(x, y, w, h, in.mouseX, in.mouseY);
    if (r.hovered && in.clicked && enabled) r.clicked = true;

    // 底色：选中 > 悬停 > 透明
    Color bg = 0;
    if (active) bg = p.bgActive;
    else if (r.hovered && enabled) bg = p.bgHover;

    Color fg = enabled ? p.fgPrimary : p.fgDim;

    if (bg != 0) {
        // 用户要求「圆角横长方形」，正方形也给同样的圆角
        plat::fillRoundRect(c, x, y, w, h, metrics().btnRadius, bg);
    } else if (active) {
        // 选中但没底色时，用一条亮线标示（IDEA 的做法）
        plat::fillRoundRect(c, x, y, w, h, metrics().btnRadius, p.bgActive);
    }

    // 图标 + 文字整体居中
    TextStyle st;
    st.size = metrics().fontSize - 1;
    st.color = fg;

    int iconSize = (int)(h * 0.55f);
    int tw = label.empty() ? 0 : estimateWidth(c, label, st);
    int gap = (icons && !iconName.empty() && !label.empty()) ? 6 : 0;
    int totalW = (iconName.empty() ? 0 : iconSize) + gap + tw;
    int startX = x + (w - totalW) / 2;

    if (icons && !iconName.empty()) {
        const auto& sh = icons->shapes(iconName);
        icons::draw(c, sh, startX, y + (h - iconSize) / 2, iconSize, fg);
        startX += iconSize + gap;
    }

    if (!label.empty()) {
        int th = 0;
        plat::measureText(c, label, st, nullptr, &th);
        plat::drawText(c, startX, y + (h - th) / 2, label, st);
    }

    return r;
}

// ---------------------------------------------------------------- 标签页

int drawTabBar(Canvas& c, const Input& in, const std::vector<Tab>& tabs,
               int active, int x, int y, int w, int h) {
    const Palette& p = current();
    int clicked = -1;

    TextStyle st;
    st.size = metrics().fontSize - 1;

    int cx = x;
    for (size_t i = 0; i < tabs.size(); i++) {
        const auto& t = tabs[i];
        int tw = estimateWidth(c, t.title, st) + 44;
        if (tw > 200) tw = 200;

        if (cx + tw > x + w) break;   // 放不下了

        bool hov = hit(cx, y, tw, h, in.mouseX, in.mouseY);
        bool act = ((int)i == active);

        // 现代风的标签：不画通栏底色，画一个圆角胶囊。
        // 活动标签是浅底 + 亮字，非活动只有悬停才出底色。
        int pad = 3;
        if (act) {
            plat::fillRoundRect(c, cx + pad, y + pad, tw - pad * 2, h - pad * 2,
                                metrics().rowRadius, p.bgBase);
        } else if (hov) {
            plat::fillRoundRect(c, cx + pad, y + pad, tw - pad * 2, h - pad * 2,
                                metrics().rowRadius, p.bgHover);
        }

        // 图标 + 标题
        int tx = cx + 12;
        if (!t.iconName.empty()) {
            // 标签页图标暂时不画，标题文字已经够区分
        }

        st.color = act ? p.fgPrimary : p.fgMuted;
        int th = 0;
        plat::measureText(c, t.title, st, nullptr, &th);
        plat::drawText(c, tx, y + (h - th) / 2, t.title, st);

        // 未保存圆点
        if (t.dirty) {
            plat::fillCircle(c, cx + tw - 16, y + h / 2, 3, p.fgMuted);
        }

        if (hov && in.clicked) clicked = (int)i;
        cx += tw;
    }

    // 标签栏下边线
    drawDividerH(c, x, y + h - 1, w);
    return clicked;
}

// ---------------------------------------------------------------- 树

int drawTree(Canvas& c, const Input& in, const icons::Library* icons,
             const std::vector<TreeNode>& nodes, int selected, int x, int y,
             int w, int h, int* scroll) {
    const Palette& p = current();
    int result = -1;
    int lineH = metrics().lineH;

    if (scroll) {
        int maxScroll = (int)nodes.size() * lineH - h;
        if (maxScroll < 0) maxScroll = 0;
        *scroll -= in.scroll * lineH * 3;
        if (*scroll < 0) *scroll = 0;
        if (*scroll > maxScroll) *scroll = maxScroll;
    }
    int sc = scroll ? *scroll : 0;

    plat::pushClip(c, x, y, w, h);

    TextStyle st;
    st.size = metrics().fontSize - 1;

    for (size_t i = 0; i < nodes.size(); i++) {
        int ny = y + (int)i * lineH - sc;
        if (ny + lineH < y || ny > y + h) continue;   // 不可见就跳过

        const auto& n = nodes[i];
        bool hov = hit(x, ny, w, lineH, in.mouseX, in.mouseY);
        bool sel = ((int)i == selected);

        // 选中/悬停都是圆角行，不是通栏色块 —— 现代风的做法
        if (sel) {
            plat::fillRoundRect(c, x + 4, ny + 1, w - 8, lineH - 2,
                                metrics().rowRadius, p.bgActive);
        } else if (hov) {
            plat::fillRoundRect(c, x + 4, ny + 1, w - 8, lineH - 2,
                                metrics().rowRadius, p.bgHover);
        }

        int indent = 8 + n.depth * 14;
        int tx = x + indent;

        // 目录的展开箭头
        if (n.isDir) {
            const char* arrow = n.expanded ? "chevron-down" : "chevron-right";
            if (icons) {
                icons::draw(c, icons->shapes(arrow), tx, ny + 4, 14, p.fgMuted);
            }
            tx += 18;
        }

        // 文件/目录图标
        int iconSize = 16;
        Color ic = p.fgMuted;
        if (n.hasError) ic = p.error;
        else if (n.tint != 0) ic = n.tint;

        if (icons && !n.iconName.empty()) {
            icons::draw(c, icons->shapes(n.iconName), tx, ny + (lineH - iconSize) / 2,
                        iconSize, ic);
        }
        tx += iconSize + 6;

        st.color = sel ? p.fgPrimary : (n.isDir ? p.fgPrimary : p.fgMuted);
        int th = 0;
        plat::measureText(c, n.name, st, nullptr, &th);

        int availW = x + w - tx - 8;
        std::string label = n.name;
        if (estimateWidth(c, label, st) > availW) {
            label = ellipsize(c, label, st, availW);
        }
        plat::drawText(c, tx, ny + (lineH - th) / 2, label, st);

        if (hov && in.clicked) {
            // 点左边 20px 内算点箭头
            if (n.isDir && in.mouseX < x + indent + 20) result = -2;
            else result = (int)i;
            // 记下点了哪一行，-2 时调用方需要知道行号
            if (result == -2) {
                // 用 -2 - i 编码，避免再加出参
                result = -2 - (int)i;
            }
        }
    }

    plat::popClip(c);
    return result;
}

// ---------------------------------------------------------------- 日志

void drawLog(Canvas& c, const std::vector<LogLine>& lines, int x, int y, int w,
             int h, int* scroll, int wheel) {
    const Palette& p = current();
    int lineH = metrics().lineH - 2;

    if (scroll) {
        int maxScroll = (int)lines.size() * lineH - h;
        if (maxScroll < 0) maxScroll = 0;
        *scroll -= wheel * lineH * 3;
        if (*scroll < 0) *scroll = 0;
        if (*scroll > maxScroll) *scroll = maxScroll;
    }
    int sc = scroll ? *scroll : 0;

    plat::pushClip(c, x, y, w, h);
    plat::fillRect(c, x, y, w, h, p.bgDeepest);

    TextStyle st;
    st.size = metrics().fontSmall;
    st.monospace = true;

    for (size_t i = 0; i < lines.size(); i++) {
        int ly = y + (int)i * lineH - sc;
        if (ly + lineH < y || ly > y + h) continue;

        const auto& l = lines[i];
        switch (l.level) {
            case LogLevel::Error: st.color = p.logError; break;
            case LogLevel::Ok: st.color = p.logOk; break;
            case LogLevel::Warn: st.color = p.logWarn; break;
            default: st.color = p.logInfo; break;
        }
        plat::drawText(c, x + 8, ly + 1, l.text, st);
    }

    plat::popClip(c);
}

// ---------------------------------------------------------------- 状态栏

void drawStatusBar(Canvas& c, const std::string& left, const std::string& mid,
                   const std::string& right, int x, int y, int w, int h) {
    const Palette& p = current();
    plat::fillRect(c, x, y, w, h, p.bgPanel);
    plat::drawLine(c, x, y, x + w, y, p.divider, 1);

    TextStyle st;
    st.size = metrics().fontSmall;
    st.color = p.fgMuted;

    int th = 0;
    plat::measureText(c, "M", st, nullptr, &th);

    if (!left.empty()) plat::drawText(c, x + 10, y + (h - th) / 2, left, st);
    if (!mid.empty()) {
        int mw = 0;
        plat::measureText(c, mid, st, &mw, nullptr);
        plat::drawText(c, x + (w - mw) / 2, y + (h - th) / 2, mid, st);
    }
    if (!right.empty()) {
        int rw = 0;
        plat::measureText(c, right, st, &rw, nullptr);
        plat::drawText(c, x + w - rw - 10, y + (h - th) / 2, right, st);
    }
}

// ---------------------------------------------------------------- 面板

void drawPanel(Canvas& c, const std::string& title, int x, int y, int w, int h) {
    const Palette& p = current();
    plat::fillRect(c, x, y, w, h, p.bgPanel);

    if (!title.empty()) {
        int headerH = 28;
        TextStyle st;
        st.size = metrics().fontSmall;
        st.color = p.fgMuted;
        st.bold = true;
        int th = 0;
        plat::measureText(c, title, st, nullptr, &th);
        plat::drawText(c, x + 10, y + (headerH - th) / 2, title, st);
        plat::drawLine(c, x, y + headerH, x + w, y + headerH, p.divider, 1);
    }
}

void drawDividerH(Canvas& c, int x, int y, int w) {
    plat::fillRect(c, x, y, w, 1, current().divider);
}

void drawDividerV(Canvas& c, int x, int y, int h) {
    plat::fillRect(c, x, y, 1, h, current().divider);
}

// ---------------------------------------------------------------- 文本

int wrapText(Canvas& c, const std::string& text, const TextStyle& st, int maxW,
             std::vector<std::string>& out) {
    out.clear();
    if (text.empty() || maxW <= 0) {
        out.push_back(text);
        return 1;
    }

    std::string line;
    int lineW = 0;
    int cw = plat::charWidth(c, st);

    size_t i = 0;
    while (i < text.size()) {
        unsigned char ch = (unsigned char)text[i];
        int len = (ch < 0x80) ? 1 : (ch >= 0xF0) ? 4 : (ch >= 0xE0) ? 3 : 2;
        std::string glyph = text.substr(i, len);

        int gw;
        if (len == 1) {
            // 英文按字符算，遇空格可断行
            gw = (int)(cw * 0.55f);
        } else {
            gw = cw;   // 中文整字，全角
        }

        if (lineW + gw > maxW && !line.empty()) {
            out.push_back(line);
            line.clear();
            lineW = 0;
        }

        line += glyph;
        lineW += gw;
        i += len;
    }
    if (!line.empty()) out.push_back(line);
    if (out.empty()) out.push_back("");
    return (int)out.size();
}

std::string ellipsize(Canvas& c, const std::string& text, const TextStyle& st,
                      int maxW) {
    int w = 0;
    plat::measureText(c, text, st, &w, nullptr);
    if (w <= maxW) return text;

    int ew = 0;
    plat::measureText(c, "...", st, &ew, nullptr);
    int budget = maxW - ew;
    if (budget <= 0) return "...";

    // 从尾部往前砍，保证 UTF-8 边界
    std::string s = text;
    while (!s.empty()) {
        unsigned char last = (unsigned char)s.back();
        // 砍到字符边界
        while (!s.empty() && (last & 0xC0) == 0x80) {
            s.pop_back();
            if (s.empty()) break;
            last = (unsigned char)s.back();
        }
        if (!s.empty()) s.pop_back();

        int cw2 = 0;
        plat::measureText(c, s, st, &cw2, nullptr);
        if (cw2 <= budget) break;
    }
    return s + "...";
}

}  // namespace ui
}  // namespace spp
