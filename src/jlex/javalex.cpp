#include "jlex/javalex.h"

#include <cctype>
#include <cstring>
#include <unordered_set>

namespace jlex {

// ---------------------------------------------------------------- 关键字

static const std::unordered_set<std::string>& keywords() {
    static const std::unordered_set<std::string> k = {
        // 修饰符
        "public", "private", "protected", "static", "final", "abstract",
        "synchronized", "volatile", "transient", "native", "strictfp",
        "sealed", "permits", "default",
        // 类型
        "class", "interface", "enum", "record", "extends", "implements",
        "void",
        // 基本类型
        "boolean", "byte", "char", "short", "int", "long", "float", "double",
        // 语句
        "if", "else", "switch", "case", "while", "do", "for", "break",
        "continue", "return", "yield", "throw", "throws", "try", "catch",
        "finally", "assert",
        // 值
        "true", "false", "null",
        // 包 / 引用
        "package", "import", "this", "super", "new", "instanceof",
        // 模块（Java 9+）
        "module", "requires", "exports", "opens", "uses", "provides",
        "to", "with", "transitive",
        // 上下文关键字
        "var",
    };
    return k;
}

bool isKeyword(const std::string& s) { return keywords().count(s) > 0; }

// 全大写常量：至少两个字符，只有大写/数字/下划线，且含至少一个大写字母。
// 否则 "_" "123" 之类也会被当成常量。
bool isConstantName(const std::string& s) {
    if (s.size() < 2) return false;
    bool hasUpper = false;
    for (char c : s) {
        if (c >= 'A' && c <= 'Z') {
            hasUpper = true;
        } else if (c == '_' || (c >= '0' && c <= '9')) {
            // ok
        } else {
            return false;
        }
    }
    return hasUpper;
}

// ---------------------------------------------------------------- 字符判定

static bool identStart(unsigned char c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' ||
           c == '$' || c >= 0x80;
}

static bool identBody(unsigned char c) {
    return identStart(c) || (c >= '0' && c <= '9');
}

static bool isDigitCh(unsigned char c) { return c >= '0' && c <= '9'; }

static bool isHexCh(unsigned char c) {
    return isDigitCh(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

static bool isPunct(unsigned char c) {
    switch (c) {
        case '(': case ')': case '{': case '}': case '[': case ']':
        case '<': case '>': case '=': case '+': case '-': case '*':
        case '/': case '%': case '&': case '|': case '^': case '!':
        case '~': case '?': case ':': case ';': case ',': case '.':
            return true;
        default:
            return false;
    }
}

// ---------------------------------------------------------------- 主体

std::vector<Span> lexLine(const std::string& line, bool& inBlockComment,
                          bool& inTextBlock) {
    std::vector<Span> out;
    const int n = (int)line.size();
    int i = 0;

    auto push = [&](int s, int len, Cat c) {
        if (len > 0) out.push_back(Span{s, len, c});
    };

    // 在 [from, n) 里找三引号收尾。closed 说出来找没找到。
    // 返回收尾位置（含三引号之后）。
    auto scanTriple = [&](int from, bool& closed) -> int {
        int p = from;
        closed = false;
        while (p < n) {
            if (line[p] == '\\') { p += 2; continue; }
            if (p + 3 <= n && line[p] == '"' && line[p + 1] == '"' &&
                line[p + 2] == '"') {
                closed = true;
                return p + 3;
            }
            p++;
        }
        return n;
    };

    while (i < n) {
        char c = line[i];

        // ---- 已经在 text block 里：整段都是字符串
        if (inTextBlock) {
            bool closed = false;
            int e = scanTriple(i, closed);
            push(i, e - i, Cat::StringLit);
            i = e;
            if (closed) inTextBlock = false;
            continue;
        }

        // ---- 已经在块注释里
        if (inBlockComment) {
            int e = i;
            bool closed = false;
            while (e + 1 < n) {
                if (line[e] == '*' && line[e + 1] == '/') { closed = true; break; }
                e++;
            }
            if (closed) {
                push(i, e + 2 - i, Cat::Comment);
                i = e + 2;
                inBlockComment = false;
            } else {
                push(i, n - i, Cat::Comment);
                i = n;
            }
            continue;
        }

        // ---- 空白：跳过，不产 span（编辑器按原文推进，
        //      空格靠列号补，不需要单独上色）
        if (c == ' ' || c == '\t' || c == '\r') {
            i++;
            continue;
        }

        // ---- 注释
        if (c == '/' && i + 1 < n && line[i + 1] == '/') {
            // /// 或 //! 当文档注释，颜色浅一点
            bool doc = (i + 2 < n && (line[i + 2] == '/' || line[i + 2] == '!'));
            push(i, n - i, doc ? Cat::Javadoc : Cat::Comment);
            i = n;
            continue;
        }
        if (c == '/' && i + 1 < n && line[i + 1] == '*') {
            bool doc = (i + 2 < n && line[i + 2] == '*');
            int e = i + 2;
            bool closed = false;
            while (e + 1 < n) {
                if (line[e] == '*' && line[e + 1] == '/') { closed = true; break; }
                e++;
            }
            if (closed) {
                push(i, e + 2 - i, doc ? Cat::Javadoc : Cat::Comment);
                i = e + 2;
            } else {
                push(i, n - i, doc ? Cat::Javadoc : Cat::Comment);
                inBlockComment = true;
                i = n;
            }
            continue;
        }

        // ---- 字符串 / text block
        if (c == '"') {
            if (i + 2 < n && line[i + 1] == '"' && line[i + 2] == '"') {
                // text block 起手
                bool closed = false;
                int e = scanTriple(i + 3, closed);
                push(i, e - i, Cat::StringLit);
                i = e;
                if (!closed) inTextBlock = true;
                continue;
            }
            int p = i + 1;
            bool closed = false;
            while (p < n) {
                if (line[p] == '\\') { p += 2; continue; }
                if (line[p] == '"') { closed = true; p++; break; }
                p++;
            }
            push(i, (closed ? p : n) - i, Cat::StringLit);
            i = closed ? p : n;
            continue;
        }

        // ---- 字符
        if (c == '\'') {
            int p = i + 1;
            bool closed = false;
            while (p < n) {
                if (line[p] == '\\') { p += 2; continue; }
                if (line[p] == '\'') { closed = true; p++; break; }
                p++;
            }
            push(i, (closed ? p : n) - i, Cat::CharLit);
            i = closed ? p : n;
            continue;
        }

        // ---- 注解
        if (c == '@') {
            int e = i + 1;
            while (e < n && identBody((unsigned char)line[e])) e++;
            push(i, e - i, Cat::Annotation);
            i = e;
            continue;
        }

        // ---- 数字
        if (isDigitCh((unsigned char)c)) {
            int e = i;
            if (c == '0' && e + 1 < n &&
                (line[e + 1] == 'x' || line[e + 1] == 'X' ||
                 line[e + 1] == 'b' || line[e + 1] == 'B' ||
                 line[e + 1] == 'o' || line[e + 1] == 'O')) {
                e += 2;
                while (e < n && (isHexCh((unsigned char)line[e]) ||
                                 line[e] == '_')) e++;
            } else {
                while (e < n && (isDigitCh((unsigned char)line[e]) ||
                                 line[e] == '_' || line[e] == '.')) e++;
                if (e < n && (line[e] == 'e' || line[e] == 'E')) {
                    e++;
                    if (e < n && (line[e] == '+' || line[e] == '-')) e++;
                    while (e < n && isDigitCh((unsigned char)line[e])) e++;
                }
            }
            if (e < n && strchr("fFdDlL", line[e])) e++;
            push(i, e - i, Cat::NumberLit);
            i = e;
            continue;
        }

        // ---- 标识符 / 关键字 / 类型
        if (identStart((unsigned char)c)) {
            int e = i;
            while (e < n && identBody((unsigned char)line[e])) e++;
            std::string word = line.substr(i, e - i);

            Cat cat = Cat::Plain;
            if (isKeyword(word)) {
                cat = Cat::Keyword;
            } else if (isConstantName(word)) {
                cat = Cat::Constant;
            } else if (word[0] >= 'A' && word[0] <= 'Z') {
                // 首字母大写当类型名。这是命名惯例不是语法规则，
                // 但所有 Java IDE 都这么做，看着最舒服。
                cat = Cat::Type;
            }
            push(i, e - i, cat);
            i = e;
            continue;
        }

        // ---- 标点
        if (isPunct((unsigned char)c)) {
            push(i, 1, Cat::Punct);
            i++;
            continue;
        }

        // ---- 其它（中文标点、emoji 之类）
        push(i, 1, Cat::Plain);
        i++;
    }

    return out;
}

}  // namespace jlex
