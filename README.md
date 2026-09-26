# Java Studio

面向 Java 入门的轻量 IDE。项目管理、代码编辑、编译运行、Maven 依赖、内置 Agent 面板，装完就能用。

版本：**v0.1.0-Preview.1**（版本号的唯一来源是仓库根的 `VERSION` 文件）

### 普通用户请通过官方网站下载(具体详情请查看官方抖音社交账号:ByteForge2026)，并非此压缩包（开发源码包），并且此包不包含JDK

---

## 它能做什么

| | |
| --- | --- |
| **项目管理** | Maven / Gradle / 纯 javac 三种骨架；新建向导照 IntelliJ IDEA 的参数（包名、JDK release、示例代码、git init） |
| **编辑器** | AvalonEdit，Java 语法高亮、行号、括号匹配、自动缩进 |
| **编译运行** | 调 `javac` / `mvn`，诊断信息（文件、行、列、级别）结构化回传，点一下跳到出错那行 |
| **JDK** | 自动探测安装目录自带的 JDK 和系统里装过的 JDK，可切换 |
| **第三方库** | 内置常用库目录表（坐标逐个核对过），Maven 中央仓库搜索、版本列表、下载带进度、本地缓存可查看可删；也能直接改 `pom.xml` 的依赖 |
| **jar** | 解析 jar，看主类、class 数、编译版本、条目列表 |
| **界面** | 深/浅主题、中/英双语、Markdown 预览窗口 |
| **Agent 面板** | 接 OpenAI 兼容的聊天接口（BaseUrl / Key / Model 全由用户自己填，程序不内置任何密钥），带工具调用；文件操作锁在项目目录内，高危命令要先确认 |

---

## 目录结构

```
Java-Studio-v0.1.0/
├── VERSION              # 版本号，唯一来源
├── LICENSE              # MIT
├── icon.ico             # 应用图标
├── src/                 # C++ 内核
│   ├── platform/        #   Win32 平台层（进程、文件、目录、HTTP 下载）
│   ├── icons/           #   SVG 解析 + 图标库
│   ├── jlex/            #   Java 词法分析
│   ├── jdk/             #   JDK 探测与选择
│   ├── jar/             #   jar 解析
│   ├── libs/            #   Maven 依赖：目录表、搜索、下载、缓存、pom 编辑
│   ├── project/         #   项目骨架生成
│   ├── build/           #   javac / mvn 的编译与运行
│   ├── core/            #   对外 C 接口（api.cpp / api.h）
│   ├── ui/              #   早期 C++ 自绘界面（Win32 + GDI，C# 接手后不再进内核 DLL）
│   └── app/             #   早期 C++ 界面应用层（同上）
├── ui/                  # C# / WPF 界面层
│   ├── MainWindow.xaml(.cs)   主窗口
│   ├── Native.cs              P/Invoke 封装
│   ├── Core.cs                Native 之上的薄封装（JSON → 对象）
│   ├── Theme.cs / Lang.cs     主题 / 中英双语
│   ├── Config.cs              用户设置持久化
│   ├── EditorEx.cs            AvalonEdit 增强
│   ├── Agent/                 Agent 面板（客户端、核心、工具、界面）
│   ├── Guide/                 新手引导（清单、状态、向导、巡览）
│   ├── Syntax/Java.xshd       Java 高亮定义（内嵌资源）
│   └── Themes/                深/浅/控件样式三本资源字典
└── assets/icons/        # 179 个 SVG 图标 + icons.json 索引 + references/ 分册文档
```

---

## 架构

两层，一条 C 接口隔开：

```
ui/  (C# / WPF, net9.0-windows)          ← 只管画界面
        │  P/Invoke  (ui/Native.cs)
        ▼
src/ (C++, 编成 javastudio_core.dll)     ← 只管干活
```

**为什么中间是 C 而不是 C++**：P/Invoke 只能封送 C 的东西。`std::string`、`std::vector`、异常、名字修饰跨过去全是坑；接口定成 C 的之后两边可以各自演进，不用同步改 ABI。

**参数方向**：进的方向收扁平参数，不收 JSON —— 省掉在 C++ 里写解析器，几个字符串的封送本来就直白。出的方向统一 JSON —— 返回的往往是列表或树，一次说清楚比一堆 `out` 参数好维护。

**内存约定**：所有 `js_*` 返回的 `char*` 都由 DLL 内部 `new[]` 出来，调用方用完必须 `js_free`。

---

## 内核模块

| 模块 | 职责 |
| --- | --- |
| `src/platform/win32.cpp` | 只依赖 `windows.h`，不引第三方库。进程、文件、目录、注册表、`winhttp` 下载 |
| `src/icons/svg.cpp` | SVG path 解析。连弧线转折线、`1.35.09` 这种紧凑数字写法都处理了，177/177 全通过 |
| `src/icons/library.cpp` | 读 `icons.json` 索引 + 绘制。图形按 24×24 设计坐标系给出 |
| `src/jlex/javalex.cpp` | Java 关键字与词法，给高亮和括号检查用 |
| `src/jdk/jdk.cpp` | 扫 `<exe目录>\jdk\*`（认 `bin\javac.exe`）、注册表、环境变量里已装的 JDK |
| `src/jar/jar.cpp` | 直接读 zip 结构解析 jar |
| `src/libs/jlibs.cpp` | 库目录表（Maven 中央仓库 HEAD 请求逐个核对过）、坐标解析、下载、缓存（照 `~/.m2` 的目录约定排，两边能互相认）、`pom.xml` 增删依赖 |
| `src/project/javaproject.cpp` | 生成目录骨架、`pom.xml` / `build.gradle`、示例代码 |
| `src/build/javabuild.cpp` | 拼 classpath、跑 `javac` / `mvn`、把输出解析成结构化诊断 |
| `src/core/api.cpp` | 导出 `js_*` 这一组 C 函数 |

`src/ui/`、`src/app/`、`src/platform/splash.cpp` 是 C++ 自绘界面那一版的遗留，只参与带窗口 exe 的构建，**不进** `javastudio_core.dll`。

---

## 内核 C API 一览

完整签名和返回结构见 `src/core/api.h`（每个函数上面都写了返回 JSON 长什么样）。按组列一下：

- **生命周期**：`js_init` `js_shutdown` `js_free` `js_set_log_sink`
- **JDK**：`js_jdk_list` `js_jdk_current` `js_jdk_select` `js_jdk_rescan`
- **项目**：`js_open_project` `js_dir_tree` `js_validate_project` `js_create_project` `js_default_projects_dir` `js_guess_main`
- **文件**：`js_read_file` `js_write_file` `js_list_dir`
- **构建**：`js_compile` `js_run` `js_check`
- **第三方库**：`js_libs_catalog` `js_libs_selection` `js_libs_save` `js_libs_resolve` `js_libs_download` `js_libs_download_ex`（带进度回调）`js_libs_search` `js_libs_versions` `js_libs_cache_list` `js_libs_cache_remove` `js_libs_pom_coords` `js_libs_pom_exists` `js_libs_pom_add` `js_libs_pom_remove`
- **jar**：`js_jar_info`
- **图标**：`js_icons_all`
- **设置 / 最近项目**：`js_settings_get` `js_settings_set` `js_build_set_lang` `js_recent` `js_recent_add` `js_recent_remove` `js_recent_clear`
- **杂项**：`js_version` `js_free_space`

---

## 图标

`assets/icons/` 下 179 个 SVG，加一份 `icons.json` 索引；`references/` 是按分类拆的图标速查文档。

解析走 C++ 里那个已经过验证的 SVG 解析器（`js_icons_all`），C# 侧只做「图形 → WPF `Geometry`」—— 不打算在 C# 里重写一遍。

图标原作是 **Morphicons**（MIT），描边风格改成 24×24 后用在 Java Studio 里。

---

## 构建

Windows x64，需要 **llvm-mingw**（`clang++`，编 C++ 侧）和 **.NET 9 SDK**（编界面侧）。

**1. 内核 DLL**

```bat
clang++ -std=c++20 -Isrc -O2 -shared -DJS_VERSION="0.1.0-Preview.1" ^
  src\platform\win32.cpp src\icons\svg.cpp src\icons\library.cpp ^
  src\jlex\javalex.cpp src\jdk\jdk.cpp src\jar\jar.cpp src\libs\jlibs.cpp ^
  src\project\javaproject.cpp src\build\javabuild.cpp src\core\api.cpp ^
  -o javastudio_core.dll ^
  -static-libstdc++ -Wl,-Bstatic -lunwind -Wl,-Bdynamic ^
  -lgdi32 -luser32 -lshell32 -lole32 -lcomdlg32 -lshlwapi -ladvapi32 -lwinhttp
```

`-static-libstdc++` 是必须的：不静态链的话产物会吊着 `libc++.dll` / `libunwind.dll`，而这两个只在装了 llvm-mingw 且 `bin` 在 PATH 里的机器上找得到，拷到别的机器上直接「找不到模块」。

产物 `javastudio_core.dll` 放到仓库根（`ui/JavaStudio.csproj` 以 `..\javastudio_core.dll` 引用它，发布时跟着走）。

**2. 界面**

```bat
dotnet publish ui/JavaStudio.csproj -c Release -r win-x64 --self-contained true -o ui\bin\publish
```

发布目录里应该出现 `studio-64.exe`、`studio-64.dll`、`studio-64.deps.json`、`studio-64.runtimeconfig.json`，加上 `javastudio_core.dll`、`assets/icons/`、`icon.ico`。

**3. JDK（运行前提）**

本仓库**不含 JDK**（自带 JDK 是安装包装的，920 MB，不进源码仓库）。自己跑的话二选一：

- 把 JDK 解压到发布目录下的 `jdk\jdk-17` / `jdk-21` / `jdk-25`（内核认子目录里有没有 `bin\javac.exe`）；
- 或者让内核去注册表 / `JAVA_HOME` 里找系统上已经装好的 JDK。

---

## 第三方

| | |
| --- | --- |
| [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) 6.3.1.120 | 代码编辑器控件，MIT |
| [Morphicons](https://github.com/morphicons/morphicons) 派生图标 | 179 个 SVG，MIT |
| JDK 17 / 21 / 25 | 发行包自带，各家 JDK 发行商的许可，本仓库不包含 |

---

## 许可证

[MIT](LICENSE) — Copyright (c) 2026 ByteForge
