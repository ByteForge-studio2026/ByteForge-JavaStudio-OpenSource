// text.cpp —— UTF-8 文字处理与编辑内核
//
// 编辑器要能正确改代码，一半的功夫在这个文件里。
//
// 三个坑：
//   1) 光标是「字节偏移」还是「字符偏移」？
//      这里统一用**字节偏移**，因为着色、切片、序列化全按字节走。
//      但移动光标必须按**码点**跳，否则删一个中文只删掉三分之一。
//      → utf8Prev / utf8Next 负责在两者之间换算。
//
//   2) 中文是 3 字节 1 个字。按字节算「列」会得到 3 倍的列号，
//      状态栏显示 12:37 很怪。→ displayColOf 数码点。
//
//   3) 光标画在哪一像素上？必须用 measureText 实测前缀宽度，
//      不能用「字符数 × 单字宽」。字体不是等宽的。
#include <algorithm>
#include <cstdio>
#include <cstring>

#include "app/app.h"

namespace app {

// ---------------------------------------------------------------- UTF-8

int utf8Len(unsigned char b) {
    if (b < 0x80) return 1;
    if ((b & 0xE0) == 0xC0) return 2;
    if ((b & 0xF0) == 0xE0) return 3;
    if ((b & 0xF8) == 0xF0) return 4;
    return 1;
}

// 往前退一个码点，返回新的字节偏移
int utf8Prev(const std::string& s, int bytePos) {
    if (bytePos <= 0) return 0;
    int i = bytePos - 1;
    // 跳过续字节（10xxxxxx）
    while (i > 0 && ((unsigned char)s[i] & 0xC0) == 0x80) i--;
    return i;
}

// 往后进一个码点
int utf8Next(const std::string& s, int bytePos) {
    if (bytePos >= (int)s.size()) return (int)s.size();
    return std::min((int)s.size(), bytePos + utf8Len((unsigned char)s[bytePos]));
}

// 把码点编成 UTF-8
std::string utf8Encode(unsigned cp) {
    std::string u;
    if (cp < 0x80) {
        u += (char)cp;
    } else if (cp < 0x800) {
        u += (char)(0xC0 | (cp >> 6));
        u += (char)(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        u += (char)(0xE0 | (cp >> 12));
        u += (char)(0x80 | ((cp >> 6) & 0x3F));
        u += (char)(0x80 | (cp & 0x3F));
    } else {
        u += (char)(0xF0 | (cp >> 18));
        u += (char)(0x80 | ((cp >> 12) & 0x3F));
        u += (char)(0x80 | ((cp >> 6) & 0x3F));
        u += (char)(0x80 | (cp & 0x3F));
    }
    return u;
}

// 第 col 个字节处的字符是不是「词字符」。
// 用来决定 Ctrl+方向键跳多远、双击选什么。
static bool isWordByte(unsigned char b) {
    return (b >= '0' && b <= '9') || (b >= 'a' && b <= 'z') ||
           (b >= 'A' && b <= 'Z') || b == '_' || b == '$' || b >= 0x80;
}

int displayColOf(const std::string& line, int byteCol) {
    int n = 0;
    for (int i = 0; i < byteCol && i < (int)line.size();) {
        i = utf8Next(line, i);
        n++;
    }
    return n;
}

int byteColOfDisplay(const std::string& line, int dispCol) {
    int i = 0;
    for (int n = 0; n < dispCol && i < (int)line.size(); n++)
        i = utf8Next(line, i);
    return i;
}

// ---------------------------------------------------------------- 文本宽度
//
// 行首缓存：把「字节偏移 -> 像素」整行算一遍，光标定位和点击定位
// 都在这个表上反查。一行最多几百字节，一次算完比每帧算便宜得多。

void ensureLineCache(App& a, OpenFile& f, int li, const TextStyle& st,
                     Canvas& c) {
    if (li < 0 || li >= (int)f.lines.size()) return;
    if (f.cachedLine == li) return;

    const std::string& ln = f.lines[li];
    f.cachedPos.assign(ln.size() + 1, 0);
    for (size_t i = 0; i < ln.size();) {
        int w = 0, h = 0;
        int n = utf8Len((unsigned char)ln[i]);
        if (i + n > ln.size()) n = (int)(ln.size() - i);
        measureText(c, ln.substr(i, n), st, &w, &h);
        for (int k = 1; k <= n && i + k <= ln.size(); k++)
            f.cachedPos[i + k] = f.cachedPos[i] + (k == n ? w : 0);
        // 中间那几个字节的宽度按比例均摊，避免表里出现 0 造成光标卡住
        if (n > 1) {
            for (int k = 1; k < n && i + k < ln.size(); k++)
                f.cachedPos[i + k] = f.cachedPos[i] + w * k / n;
        }
        i += n;
    }
    f.cachedLine = li;
    (void)a;
}

int colToX(App& a, OpenFile& f, int li, int col, Canvas& c,
           const TextStyle& st) {
    if (li < 0 || li >= (int)f.lines.size()) return 0;
    if (col > (int)f.lines[li].size()) col = (int)f.lines[li].size();
    if (col < 0) col = 0;
    ensureLineCache(a, f, li, st, c);
    if (col < (int)f.cachedPos.size()) return f.cachedPos[col];
    return f.cachedPos.empty() ? 0 : f.cachedPos.back();
}

int xToCol(App& a, OpenFile& f, int li, int x, Canvas& c,
           const TextStyle& st) {
    if (li < 0 || li >= (int)f.lines.size()) return 0;
    ensureLineCache(a, f, li, st, c);
    const std::string& ln = f.lines[li];
    int best = 0, bestD = 1 << 30;
    // 只在码点边界上比，不然光标会落到一个汉字的中间字节
    for (int i = 0; i <= (int)ln.size();) {
        int px = (i < (int)f.cachedPos.size()) ? f.cachedPos[i] : 0;
        int d = px > x ? px - x : x - px;
        if (d < bestD) {
            bestD = d;
            best = i;
        }
        if (i >= (int)ln.size()) break;
        i = utf8Next(ln, i);
    }
    return best;
}

// ---------------------------------------------------------------- 着色

void recolorAll(OpenFile& f) {
    f.spans.assign(f.lines.size(), {});
    f.inBlock.assign(f.lines.size(), 0);
    f.inText.assign(f.lines.size(), 0);

    if (!f.isJava) return;

    bool blk = false, txt = false;
    for (size_t i = 0; i < f.lines.size(); i++) {
        bool b2 = blk, t2 = txt;
        f.spans[i] = jlex::lexLine(f.lines[i], b2, t2);
        f.inBlock[i] = blk ? 1 : 0;
        f.inText[i] = txt ? 1 : 0;
        blk = b2;
        txt = t2;
    }
    f.cachedLine = -1;
}

void recolorLine(OpenFile& f, int li) {
    if (li < 0 || li >= (int)f.lines.size()) return;
    if (!f.isJava) return;

    // 起始状态看上一行结束时是什么
    bool blk = li > 0 && li - 1 < (int)f.inBlock.size() && f.inBlock[li - 1] != 0;
    bool txt = li > 0 && li - 1 < (int)f.inText.size() && f.inText[li - 1] != 0;

    bool b2 = blk, t2 = txt;
    f.spans[li] = jlex::lexLine(f.lines[li], b2, t2);
    f.inBlock[li] = blk ? 1 : 0;
    f.inText[li] = txt ? 1 : 0;

    // 这一行的结束状态变了，后面所有行都得重算 —— 否则一个 /*
    // 打上去，下面的代码还是彩色的。
    //
    // 这里不能无脑重算到底：一份 5000 行的文件，在最后一行敲一个
    // 普通字符也会走这条路。优化是「状态没变就停」——只要某一行
    // 的收尾状态和它原来记的一致，它后面的行就不受影响。
    if (li + 1 < (int)f.lines.size()) {
        bool cb = b2, ct = t2;
        for (int i = li + 1; i < (int)f.lines.size(); i++) {
            // 先把「原来记的状态」取出来，因为马上要覆盖
            bool oldB = f.inBlock[i] != 0;
            bool oldT = f.inText[i] != 0;

            bool nb = cb, nt = ct;
            f.spans[i] = jlex::lexLine(f.lines[i], nb, nt);
            f.inBlock[i] = cb ? 1 : 0;
            f.inText[i] = ct ? 1 : 0;

            bool newB = cb, newT = ct;
            // 状态和原来一样 → 这一行和它之后的行都不用再动了
            if (newB == oldB && newT == oldT) break;

            cb = nb;
            ct = nt;
        }
    }
    f.cachedLine = -1;
}

// ---------------------------------------------------------------- 光标 / 选区

void setCaret(App& a, int line, int col, bool extend) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;

    if (line < 0) line = 0;
    if (line >= (int)f.lines.size()) line = (int)f.lines.size() - 1;
    if (line < 0) line = 0;

    if (col < 0) col = 0;
    if (col > (int)f.lines[line].size()) col = (int)f.lines[line].size();
    // 不许停在码点中间
    while (col > 0 && col < (int)f.lines[line].size() &&
           ((unsigned char)f.lines[line][col] & 0xC0) == 0x80)
        col--;

    if (!extend) {
        f.selAnchorLine = -1;
        f.selAnchorCol = -1;
    } else if (f.selAnchorLine < 0) {
        f.selAnchorLine = f.caretLine;
        f.selAnchorCol = f.caretCol;
    }

    f.caretLine = line;
    f.caretCol = col;
}

void moveCaret(App& a, int dLine, int dCol, bool extend) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;

    int line = f.caretLine;
    int col = f.caretCol;

    if (dLine != 0) {
        line += dLine;
        if (line < 0) line = 0;
        if (line >= (int)f.lines.size()) line = (int)f.lines.size() - 1;
        // 竖着走要保持「视觉列」，不然从长行走到短行再走回来列就丢了
        // 这里用简单的夹取，配合下面的 clamp 已经够用
        if (col > (int)f.lines[line].size()) col = (int)f.lines[line].size();
    }

    if (dCol != 0) {
        if (dCol < 0) {
            if (col == 0 && line > 0) {
                // 行首再往左 = 接上一行行尾
                line--;
                col = (int)f.lines[line].size();
            } else {
                col = utf8Prev(f.lines[line], col);
            }
        } else {
            if (col >= (int)f.lines[line].size() && line + 1 < (int)f.lines.size()) {
                line++;
                col = 0;
            } else {
                col = utf8Next(f.lines[line], col);
            }
        }
    }

    setCaret(a, line, col, extend);
}

// 行首 / 行尾（Home / End）
void moveLineEdge(App& a, bool toEnd, bool extend) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;
    setCaret(a, f.caretLine,
             toEnd ? (int)f.lines[f.caretLine].size() : 0, extend);
}

// 一个词的边界（Ctrl+左右）
void moveWord(App& a, bool forward, bool extend) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;
    int line = f.caretLine;
    int col = f.caretCol;

    if (forward) {
        const std::string& ln = f.lines[line];
        if (col >= (int)ln.size()) {
            if (line + 1 < (int)f.lines.size()) {
                setCaret(a, line + 1, 0, extend);
            }
            return;
        }
        // 先跳过当前这个词
        while (col < (int)ln.size() &&
               isWordByte((unsigned char)ln[col]))
            col = utf8Next(ln, col);
        // 再跳过空白
        while (col < (int)ln.size() &&
               !isWordByte((unsigned char)ln[col]))
            col = utf8Next(ln, col);
    } else {
        const std::string& ln = f.lines[line];
        if (col == 0) {
            if (line > 0) setCaret(a, line - 1,
                                   (int)f.lines[line - 1].size(), extend);
            return;
        }
        while (col > 0) {
            int p = utf8Prev(ln, col);
            if (isWordByte((unsigned char)ln[p])) break;
            col = p;
        }
        while (col > 0) {
            int p = utf8Prev(ln, col);
            if (!isWordByte((unsigned char)ln[p])) break;
            col = p;
        }
    }
    setCaret(a, line, col, extend);
}

void selectAll(App& a) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;
    f.selAnchorLine = 0;
    f.selAnchorCol = 0;
    f.caretLine = (int)f.lines.size() - 1;
    f.caretCol = f.lines.empty() ? 0 : (int)f.lines.back().size();
}

// 选区归一化成 (起行,起列,止行,止列)
static bool selRange(const OpenFile& f, int* l0, int* c0, int* l1, int* c1) {
    if (f.selAnchorLine < 0) return false;
    int al = f.selAnchorLine, ac = f.selAnchorCol;
    int bl = f.caretLine, bc = f.caretCol;
    bool aFirst = (al < bl) || (al == bl && ac <= bc);
    *l0 = aFirst ? al : bl;
    *c0 = aFirst ? ac : bc;
    *l1 = aFirst ? bl : al;
    *c1 = aFirst ? bc : ac;
    return !(*l0 == *l1 && *c0 == *c1);
}

bool hasSelection(App& a) {
    OpenFile* f = a.active();
    if (!f) return false;
    int l0, c0, l1, c1;
    return selRange(*f, &l0, &c0, &l1, &c1);
}

std::string selectedText(App& a) {
    OpenFile* fp = a.active();
    if (!fp) return "";
    OpenFile& f = *fp;
    int l0, c0, l1, c1;
    if (!selRange(f, &l0, &c0, &l1, &c1)) return "";

    if (l0 == l1) return f.lines[l0].substr(c0, c1 - c0);

    std::string s = f.lines[l0].substr(c0);
    for (int i = l0 + 1; i < l1; i++) {
        s += "\n";
        s += f.lines[i];
    }
    s += "\n";
    s += f.lines[l1].substr(0, c1);
    return s;
}

// ---------------------------------------------------------------- 编辑
//
// 所有改动都要：
//   a) 改行数组
//   b) 重算受影响行的着色
//   c) 把光标放到新位置
//   d) dirty = true
// 少任何一步都会出现「字进去了但颜色不对」或「保存按钮不亮」。

// 删掉当前选区，返回是否删了东西
static bool deleteSelection(OpenFile& f) {
    int l0, c0, l1, c1;
    if (!selRange(f, &l0, &c0, &l1, &c1)) {
        f.selAnchorLine = -1;
        f.selAnchorCol = -1;
        return false;
    }

    if (l0 == l1) {
        f.lines[l0].erase(c0, c1 - c0);
    } else {
        f.lines[l0] = f.lines[l0].substr(0, c0) + f.lines[l1].substr(c1);
        f.lines.erase(f.lines.begin() + l0 + 1, f.lines.begin() + l1 + 1);
    }
    f.caretLine = l0;
    f.caretCol = c0;
    f.selAnchorLine = -1;
    f.selAnchorCol = -1;
    return true;
}

void insertText(App& a, const std::string& utf8) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;
    if (utf8.empty()) return;

    deleteSelection(f);

    std::string& ln = f.lines[f.caretLine];
    ln.insert((size_t)f.caretCol, utf8);
    f.caretCol += (int)utf8.size();
    f.dirty = true;
    recolorLine(f, f.caretLine);
}

// 回车。会自动带上缩进 —— 写 Java 时这个体验差别很大。
void insertNewline(App& a) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;

    deleteSelection(f);

    std::string& ln = f.lines[f.caretLine];
    std::string head = ln.substr(0, f.caretCol);
    std::string tail = ln.substr(f.caretCol);

    // 数一下行首缩进
    std::string indent;
    for (char ch : head) {
        if (ch == ' ' || ch == '\t') indent += ch;
        else break;
    }
    // 上一行以 { 结尾就多缩一层（4 空格）
    std::string t = head;
    while (!t.empty() && (t.back() == ' ' || t.back() == '\t')) t.pop_back();
    if (!t.empty() && t.back() == '{') indent += "    ";

    ln = head;
    f.lines.insert(f.lines.begin() + f.caretLine + 1, indent + tail);

    f.caretLine++;
    f.caretCol = (int)indent.size();
    f.dirty = true;

    // 行数变了，spans 要跟着插一行
    if (f.caretLine - 1 < (int)f.spans.size())
        f.spans.insert(f.spans.begin() + f.caretLine, {});
    if (f.caretLine - 1 < (int)f.inBlock.size())
        f.inBlock.insert(f.inBlock.begin() + f.caretLine, 0);
    if (f.caretLine - 1 < (int)f.inText.size())
        f.inText.insert(f.inText.begin() + f.caretLine, 0);

    recolorLine(f, f.caretLine - 1);
    recolorLine(f, f.caretLine);
}

void backspace(App& a) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;

    if (deleteSelection(f)) {
        // 合并了行的话 spans 要重建
        recolorAll(f);
        f.dirty = true;
        return;
    }

    if (f.caretCol > 0) {
        std::string& ln = f.lines[f.caretLine];
        int p = utf8Prev(ln, f.caretCol);
        ln.erase(p, f.caretCol - p);
        f.caretCol = p;
        recolorLine(f, f.caretLine);
    } else if (f.caretLine > 0) {
        // 接到上一行行尾
        int prevLen = (int)f.lines[f.caretLine - 1].size();
        f.lines[f.caretLine - 1] += f.lines[f.caretLine];
        f.lines.erase(f.lines.begin() + f.caretLine);
        if (f.caretLine < (int)f.spans.size())
            f.spans.erase(f.spans.begin() + f.caretLine);
        if (f.caretLine < (int)f.inBlock.size())
            f.inBlock.erase(f.inBlock.begin() + f.caretLine);
        if (f.caretLine < (int)f.inText.size())
            f.inText.erase(f.inText.begin() + f.caretLine);
        f.caretLine--;
        f.caretCol = prevLen;
        recolorAll(f);
    } else {
        return;   // 文件开头，无事可做
    }
    f.dirty = true;
}

void deleteForward(App& a) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;

    if (deleteSelection(f)) {
        recolorAll(f);
        f.dirty = true;
        return;
    }

    const std::string& ln = f.lines[f.caretLine];
    if (f.caretCol < (int)ln.size()) {
        int n = utf8Next(ln, f.caretCol);
        f.lines[f.caretLine].erase(f.caretCol, n - f.caretCol);
        recolorLine(f, f.caretLine);
    } else if (f.caretLine + 1 < (int)f.lines.size()) {
        f.lines[f.caretLine] += f.lines[f.caretLine + 1];
        f.lines.erase(f.lines.begin() + f.caretLine + 1);
        if (f.caretLine + 1 < (int)f.spans.size())
            f.spans.erase(f.spans.begin() + f.caretLine + 1);
        if (f.caretLine + 1 < (int)f.inBlock.size())
            f.inBlock.erase(f.inBlock.begin() + f.caretLine + 1);
        if (f.caretLine + 1 < (int)f.inText.size())
            f.inText.erase(f.inText.begin() + f.caretLine + 1);
        recolorAll(f);
    } else {
        return;
    }
    f.dirty = true;
}

// 整行缩进 / 反缩进（Tab / Shift+Tab 无选区时）
void indentSelection(App& a, bool out) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;

    int l0, c0, l1, c1;
    bool has = selRange(f, &l0, &c0, &l1, &c1);
    if (!has) {
        l0 = l1 = f.caretLine;
        c0 = c1 = f.caretCol;
    }

    for (int i = l0; i <= l1 && i < (int)f.lines.size(); i++) {
        if (out) {
            std::string& ln = f.lines[i];
            int k = 0;
            while (k < 4 && k < (int)ln.size() && ln[k] == ' ') k++;
            if (k > 0) ln.erase(0, k);
        } else {
            f.lines[i].insert(0, "    ");
        }
        recolorLine(f, i);
    }

    // 有选区要把选区跟着挪，不然缩进完选区就跑偏了
    if (has) {
        f.selAnchorCol = f.selAnchorLine == l0 ? c0 + (out ? 0 : 4) : f.selAnchorCol;
        f.caretCol = f.caretLine == l1 ? f.caretCol + (out ? 0 : 4) : f.caretCol;
    } else {
        f.caretCol += out ? 0 : 4;
    }
    f.dirty = true;
}

// ---------------------------------------------------------------- 剪贴板
//
// 走 Win32 的剪贴板。为什么不用自己的全局变量：用户会从浏览器、
// 从别的编辑器复制过来，必须和系统剪贴板是同一个。

void copySelection(App& a) {
    std::string s = selectedText(a);
    if (s.empty()) return;
    clipboardSet(s);
}

void cutSelection(App& a) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;
    std::string s = selectedText(a);
    if (s.empty()) return;
    clipboardSet(s);
    if (deleteSelection(f)) {
        recolorAll(f);
        f.dirty = true;
    }
}

bool pasteFromClipboard(App& a) {
    OpenFile* fp = a.active();
    if (!fp) return false;
    std::string s = clipboardGet();
    if (s.empty()) return false;

    // 剪贴板里可能是多行的，要按行拆开塞进去
    OpenFile& f = *fp;
    deleteSelection(f);

    size_t start = 0;
    bool multi = s.find('\n') != std::string::npos;
    if (!multi) {
        insertText(a, s);
        return true;
    }

    // 逐行插入
    while (start <= s.size()) {
        size_t nl = s.find('\n', start);
        std::string seg = (nl == std::string::npos)
                              ? s.substr(start)
                              : s.substr(start, nl - start);
        if (!seg.empty() && seg.back() == '\r') seg.pop_back();

        std::string& ln = f.lines[f.caretLine];
        ln.insert((size_t)f.caretCol, seg);
        f.caretCol += (int)seg.size();
        recolorLine(f, f.caretLine);

        if (nl == std::string::npos) break;
        insertNewline(a);
        // insertNewline 会带缩进，粘过来的内容不该被改，去掉
        std::string& l2 = f.lines[f.caretLine];
        size_t k = 0;
        while (k < l2.size() && (l2[k] == ' ' || l2[k] == '\t')) k++;
        if (k > 0 && f.caretCol == (int)k) {
            l2.erase(0, k);
            f.caretCol = 0;
        }
        start = nl + 1;
    }
    f.dirty = true;
    return true;
}

// ---------------------------------------------------------------- 滚动跟随

void scrollCaretIntoView(App& a, Canvas& c, const TextStyle& st,
                         int bodyY, int bodyH) {
    OpenFile* fp = a.active();
    if (!fp) return;
    OpenFile& f = *fp;

    int lh = lineHeight(c, st);
    if (lh <= 0) lh = 18;

    int caretTop = f.caretLine * lh;
    int caretBottom = caretTop + lh;

    // 上边沿留一行余量，不然光标贴在最上面看不见上下文
    if (caretTop - lh < f.scrollY) {
        f.scrollY = caretTop - lh;
        if (f.scrollY < 0) f.scrollY = 0;
    }
    // 下边沿
    if (caretBottom + lh > f.scrollY + bodyH) {
        f.scrollY = caretBottom + lh - bodyH;
    }
    if (f.scrollY < 0) f.scrollY = 0;

    // 横向：光标超出右边界就滚
    int cx = colToX(a, f, f.caretLine, f.caretCol, c, st);
    int visW = a.winW - a.sidebarW - 56;
    if (cx + 40 > f.scrollX + visW) f.scrollX = cx + 40 - visW;
    if (cx < f.scrollX) f.scrollX = cx > 20 ? cx - 20 : 0;
    if (f.scrollX < 0) f.scrollX = 0;
    (void)bodyY;
}

}  // namespace app
