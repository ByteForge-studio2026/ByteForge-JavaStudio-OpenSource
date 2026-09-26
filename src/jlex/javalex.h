// javalex.h —— Java 词法着色
//
// 只做一件事：把一行 Java 源码切成带类别的小段，交给编辑器上色。
//
// 为什么不复用 S++ 那套 lexer：那套产出的是完整 token 流（带 AST 用途的
// 各种字段），而且 S++ 和 Java 的词法规则差得远（Java 没有 ??=，
// 没有反引号模板，但有注解、有泛型、有 Javadoc）。
// 单独写一个轻量的更清楚，也不怕改坏语言那套。
#pragma once

#include <string>
#include <vector>

namespace jlex {

// 上色类别。和 Palette 里的语法色一一对应。
enum class Cat {
    Plain,        // 普通
    Keyword,      // public / class / if / return ...
    Type,         // String / List / MyClass（首字母大写的标识符）
    StringLit,    // "..." 和 text block
    CharLit,      // 'a'
    NumberLit,    // 123 / 0xFF / 1.5e10 / 10L
    Annotation,   // @Override
    Comment,      // // 和 /* */
    Javadoc,      // /** */ 和 /// 风格
    Constant,     // 全大写的常量 SNAKE_CASE
    Punct,        // 运算符和括号
};

struct Span {
    int start = 0;     // 在行内的字节偏移
    int len = 0;
    Cat cat = Cat::Plain;
};

// 切一行。行内不跨行，跨行的块注释要传 inBlock 状态。
//
// inBlockComment: 传进来时这行是不是已经在 /* ... */ 里面；
//                 返回时这行结束时是不是没闭合。
std::vector<Span> lexLine(const std::string& line, bool& inBlockComment,
                          bool& inTextBlock);

// 这个名字是不是 Java 关键字
bool isKeyword(const std::string& s);

// 全大写常量（SNAKE_CASE）
bool isConstantName(const std::string& s);

}  // namespace jlex
