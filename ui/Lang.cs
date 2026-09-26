// Lang.cs —— 中英文切换
//
// 做法很土但够用：一张 key → 文案 的表，切语言就是换一张表，
// 再把界面上所有文字重新刷一遍（菜单按 Tag 批量刷，主页整个重建）。
// 不做资源文件（.resx）是因为那些字符串散在 XAML 和后台代码两边，
// 用 resx 反而要把 XAML 里的 Header 全改成绑定，改动面更大。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
namespace JavaStudio;

internal static class Lang
{
    public const string ZH = "zh";
    public const string EN = "en";

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["app.name"] = "Java Studio",
        ["app.slogan"] = "Java 入门 IDE",

        // 主页五个版块
        ["home.new"] = "新建项目",
        ["home.new.hint"] = "建一个能直接跑的工程",
        ["home.list"] = "项目列表",
        ["home.list.hint"] = "接着上次的地方写",
        ["home.open"] = "打开项目",
        ["home.open.hint"] = "从磁盘挑一个目录",
        ["home.settings"] = "设置",
        ["home.settings.hint"] = "主题 · 语言 · Java",

        ["home.last"] = "上次打开",
        ["home.recent"] = "项目列表",
        ["home.recent.empty"] = "还没有项目。点上面的「新建项目」建一个。",
        ["home.continue"] = "继续",

        ["home.new.name"] = "项目名称",
        ["home.new.group"] = "GroupId",
        ["home.new.artifact"] = "ArtifactId",
        ["home.new.location"] = "位置",
        ["home.new.build"] = "构建系统",
        ["home.new.sample"] = "生成示例代码",
        ["home.new.git"] = "创建 Git 仓库",
        ["home.new.venv"] = "创建独立运行环境（venv）",
        ["home.new.create"] = "创建",
        ["home.new.bad"] = "项目名称只能由字母、数字、下划线、短横线组成",

        ["home.open.path"] = "项目目录",
        ["home.open.browse"] = "浏览",
        ["home.open.go"] = "打开",

        ["home.set.theme"] = "主题",
        ["home.set.theme.light"] = "浅色",
        ["home.set.theme.dark"] = "深色",
        ["home.set.lang"] = "语言",
        ["home.set.java"] = "外部 Java（JDK 目录）",
        ["home.set.java.browse"] = "浏览",
        ["home.set.java.use"] = "使用这个 Java",
        ["home.set.java.auto"] = "用自动检测到的 JDK",
        ["home.set.java.bad"] = "这个目录里没有 java.exe",
        ["home.set.java.ok"] = "外部 Java 已生效",

        ["home.venv.create"] = "创建运行环境",
        ["home.venv.run"] = "在虚拟环境中运行",
        ["home.venv.done"] = "运行环境已创建",

        // 菜单
        ["m.file"] = "文件",
        ["m.newProject"] = "新建项目...",
        ["m.openProject"] = "打开项目...",
        ["m.newFile"] = "新建文件",
        ["m.save"] = "保存",
        ["m.saveAll"] = "全部保存",
        ["m.closeProject"] = "关闭项目",
        ["m.exit"] = "退出",
        ["m.edit"] = "编辑",
        ["m.undo"] = "撤销",
        ["m.redo"] = "重做",
        ["m.revert"] = "回到上次更改前",
        ["m.cut"] = "剪切",
        ["m.copy"] = "复制",
        ["m.paste"] = "粘贴",
        ["m.selectAll"] = "全选",
        ["m.view"] = "视图",
        ["m.home"] = "主页",
        ["m.editor"] = "编辑器",
        ["m.tree"] = "项目工具窗口",
        ["m.bottom"] = "输出工具窗口",
        ["m.theme"] = "切换主题",
        ["m.settings"] = "设置",
        ["m.nav"] = "导航",
        ["m.main"] = "主类",
        ["m.run"] = "运行",
        ["m.compile"] = "编译",
        ["m.compileRun"] = "编译并运行",
        ["m.runOnly"] = "只运行",
        ["m.check"] = "代码检查",
        ["m.venv"] = "创建运行环境",
        ["m.venvRun"] = "在虚拟环境中运行",
        ["m.tools"] = "工具",
        ["m.libs"] = "第三方库...",
        ["m.icons"] = "图标库",
        ["m.help"] = "帮助",
        ["m.about"] = "关于 Java Studio",
        ["m.rename"] = "重命名",
        ["m.delete"] = "删除",
        ["m.importFile"] = "导入文件...",
        ["m.importDir"] = "导入文件夹...",
        ["m.copyPath"] = "复制路径",
        ["tree.title"] = "项目",

        // 工具栏提示
        ["tip.new"] = "新建项目",
        ["tip.open"] = "打开项目",
        ["tip.save"] = "保存",
        ["tip.compile"] = "编译",
        ["tip.run"] = "编译并运行",
        ["tip.check"] = "代码检查",
        ["tip.libs"] = "第三方库",
        ["tip.home"] = "主页",
        ["tip.tree"] = "项目工具窗口",
        ["tip.theme"] = "切换主题",
        ["tip.settings"] = "设置",
        ["tip.refresh"] = "刷新项目树",

        // 状态
        ["st.ready"] = "就绪",
        ["st.opened"] = "项目已打开：",
        ["st.noProject"] = "先打开一个项目",
        ["st.compiling"] = "编译中…",
        ["st.compiled"] = "编译通过",
        ["st.compileFail"] = "编译失败",
        ["st.running"] = "运行中…",
        ["st.runDone"] = "运行结束",
        ["st.noMain"] = "找不到主类",
        ["st.saved"] = "已保存",
        ["msg.ok"] = "确定",
        ["msg.cancel"] = "取消",
        ["msg.yes"] = "是",
        ["msg.no"] = "否",
        ["msg.error"] = "出错了",
        ["st.savedFile"] = "已保存 ",
        ["st.saveFail"] = "保存失败：",
        ["st.libs"] = "个已在 classpath 上",
        ["st.libs0"] = "第三方库 ",
        ["st.checking"] = "检查中…",
        ["st.noProblem"] = "没有发现问题",
        ["st.problems"] = " 个问题",

        // 预览
        ["m.preview"] = "预览",
        ["preview.of"] = "预览",
        ["preview.unsupported"] = "这个文件类型暂不支持预览。",
        ["preview.fail"] = "预览失败：",
        // md 在编辑器里打开时，右上角那个切换按钮的三种状态
        ["md.mode.edit"] = "编辑",
        ["md.mode.split"] = "拆分",
        ["md.mode.preview"] = "预览",
        ["md.mode.tip"] = "切换视图（编辑 / 拆分 / 预览）",
        // 拆分时那个「交换左右」按钮
        ["md.swap.tip"] = "交换左右两栏（编辑在左 / 在右）",

        // 第三方库（LibsDialog：左「项目依赖」右「本地缓存」两页）
        ["lib.title"] = "第三方库",
        ["lib.nav.deps"] = "项目依赖",
        ["lib.nav.cache"] = "本地缓存",
        ["lib.cand"] = "添加依赖",
        ["lib.candCount"] = "内置目录 {1} 个，当前列出 {0} 个",
        ["lib.candEmpty"] = "没有匹配的库。换个关键词，或者点「搜索 Maven 仓库」到仓库里找。",
        ["lib.deps"] = "当前项目的依赖",
        ["lib.depCount"] = "共 {0} 个",
        ["lib.depsEmpty"] = "这个项目还没有依赖。左边挑一个，或者手输 groupId / artifactId / version 加进来。",
        ["lib.add"] = "添加",
        ["lib.added"] = "已添加",
        ["lib.remove"] = "移除",
        ["lib.download"] = "下载",
        ["lib.downloading"] = "正在下载…",
        ["lib.downloadFail"] = "下载失败",
        ["lib.progress"] = "已下载 {0}%",
        ["lib.progressBytes"] = "已下载 {0}",
        ["lib.ready"] = "已就绪",
        ["lib.missing"] = "未下载",
        ["lib.searchRepo"] = "搜索 Maven 仓库",
        ["lib.backCatalog"] = "返回内置目录",
        ["lib.searching"] = "正在搜索…（首次连仓库要几十秒，走代理会更慢）",
        ["lib.repoCount"] = "仓库返回 {0} 条",
        ["lib.repoEmpty"] = "没搜到。换个关键词，或者直接手输三段坐标。",
        ["lib.searchFail"] = "搜索失败：{0}",
        ["lib.searchTimeout"] = "等太久了，先不搜了。检查一下网络或代理设置，再点一次。",
        ["lib.group"] = "groupId",
        ["lib.artifact"] = "artifactId",
        ["lib.version"] = "version",
        ["lib.coordBad"] = "坐标要写成 groupId:artifactId:version 三段",
        ["lib.src.pom"] = "这个项目有 pom.xml，依赖会写进 <dependencies>。",
        ["lib.src.txt"] = "这个项目没有 pom.xml，依赖记在 .javastudio\\libs.txt（纯 javac 编译时靠它拼 classpath）。",
        ["lib.cache"] = "已下载到本机的 jar",
        ["lib.cache.open"] = "打开目录",
        ["lib.cache.total"] = "共 {0} 个 · {1}",
        ["lib.cache.empty"] = "缓存里还没有 jar。给项目加上依赖、点「下载」之后就会出现在这里。",
        ["lib.cache.deleted"] = "已删除 {0}",
        ["lib.apply"] = "应用",

        ["libs.noBuiltinJdk"] = "没找到内置 JDK",
        ["log.folderCreated"] = "已创建文件夹 {0}",
        ["log.itemCreated"] = "已创建 {0}",
        ["log.downLoaded"] = "已下载 {0}",
        ["log.downloadFail"] = "下载失败：",
        ["log.downloadError"] = "下载出错：",
        ["log.searchFail"] = "仓库搜索失败：",

        // 项目树右键菜单
        ["m.deleteProject"] = "删除项目",
        ["ctx.newJavaClass"] = "新建 Java 类",
        ["ctx.newJavaFile"] = "新建 Java 文件",
        ["ctx.newFolder"] = "新建文件夹",
        ["ctx.newXml"] = "新建 XML 文件",
        ["ctx.newCustom"] = "新建自定义后缀文件",
        ["ctx.importFile"] = "导入文件...",
        ["ctx.importDir"] = "导入文件夹...",
        ["ctx.rename"] = "重命名...",
        ["ctx.delete"] = "删除",
        ["ctx.copyPath"] = "复制路径",

        // 图标库：怎么用这套图标
        ["icons.usage"] = "怎么用这些图标",
        ["icons.copied"] = "已复制图标名 ",
        ["icons.t.kTitle"] = "1) 在这个 IDE 自己的界面里（C#）",
        ["icons.t.kBody"] = "Icons.Visual(\"coffee\", 22, Ui.B(\"Brush.Fg.Primary\"));",
        ["icons.t.pTitle"] = "2) 在项目里",
        ["icons.t.pBody"] = "每个新建项目的根目录都有一份 javastudio-icon/，\n" +
                            "里面是同一套 SVG 和一个 icons.json：\n" +
                            "    javastudio-icon/coffee.svg",
        ["icons.t.jTitle"] = "3) Java 里读它",
        ["icons.t.jBody"] = "// 先把目录挪进 src/main/resources/icons/\n" +
                            "InputStream in = Main.class.getResourceAsStream(\"/icons/coffee.svg\");\n" +
                            "// SVG 不是位图，ImageIO.read 读不了它，\n" +
                            "// 要用 jsvg / Batik 先渲染成 BufferedImage。",
        ["icons.t.tip"] = "图标名就是文件名去掉 .svg。点一下任意格子，名字会复制到剪贴板。",

        // 状态补充
        ["st.compileFailN"] = "编译失败（{0} 个错误）",
        ["st.exitCode"] = "退出码 {0}",
        ["st.problemsN"] = "{0} 个问题",

        // 预览补充
        ["preview.none"] = "先在项目树里选一个文件",
        ["preview.folder"] = "预览针对的是文件，不是文件夹",

        // 编辑器右键菜单
        ["ed.undo"] = "撤销",
        ["ed.redo"] = "重做",
        ["ed.cut"] = "剪切",
        ["ed.copy"] = "复制",
        ["ed.paste"] = "粘贴",
        ["ed.selectAll"] = "全选",
        ["ed.complete"] = "补全（Ctrl+Space）",
        ["ed.indent"] = "缩进 4 空格（Tab）",
        ["ed.save"] = "保存",

        // 工具栏 / 底部 / 树操作 / JDK
        ["tip.newFile"] = "新建",
        ["tip.openImport"] = "打开 / 导入",
        ["tip.newFileFolder"] = "新建文件 / 文件夹",
        ["tip.importFile"] = "导入文件",
        ["tip.jdk"] = "编译 / 运行用的 JDK（不挑就用创建项目时的版本）",
        ["tip.hideBottom"] = "隐藏工具窗口",
        ["bottom.output"] = "输出",
        ["bottom.log"] = "日志",
        ["bottom.problems"] = "问题",

        // 状态（即时消息）
        ["st.importDone"] = "已导入 {0} 个文件",
        ["st.importSkip"] = "（跳过 {0} 个已存在的同名文件）",
        ["st.importFail"] = "导入失败：",
        ["st.selectFolder"] = "先在项目树里选中一个文件夹",
        ["st.selectItem"] = "先在树上选中一项",
        ["st.created"] = "已创建 ",
        ["st.openFail"] = "打开失败：",
        ["st.renameDone"] = "已重命名",
        ["st.renameFail"] = "重命名失败：",
        ["st.deleteFail"] = "删除失败：",
        ["st.pathCopied"] = "路径已复制",
        ["st.deleteDone"] = "已删除 ",
        ["st.treeRefreshed"] = "项目树已刷新",
        ["st.noApiProject"] = "先打开一个项目才能用 To-code Studio Pro",
        ["st.needProject"] = "先打开一个项目",
        ["st.runFail"] = "运行失败",
        ["st.importFolderDone"] = "已导入文件夹 ",
        ["st.noEarlier"] = "这个文件还没有更早的版本",
        ["st.reverted"] = "已回到上次更改前",
        ["st.noSrc"] = "没找到 {0} 对应的源文件",
        ["st.cantLocate"] = "JAVA Studio 无法识别问题所在位置",
        ["st.mainClass"] = "主类 ",

        // 问题窗口
        ["prob.summary"] = "错误 {0} 个 · 警告 {1} 个",
        ["prob.unknownFile"] = "未知文件",
        ["prob.cantLocateFile"] = "JAVA Studio 无法识别问题所在位置（{0}）",

        // 自绘选择器标题
        ["pk.importFileTo"] = "导入文件到 {0}",
        ["pk.importFolderTo"] = "导入文件夹到 {0}",
        ["pk.desktop"] = "桌面",
        ["pk.documents"] = "文档",
        ["pk.downloads"] = "下载",
        ["pk.thisPc"] = "此电脑",
        ["pk.empty"] = "（空）",
        ["pk.up"] = "上一级",
        ["pk.selectFolder"] = "选择此文件夹",
        ["pk.open"] = "打开",
        ["pk.fileType"] = "文件类型",
        ["pk.fileTypeAll"] = "所有文件",

        // 回收站
        ["rec.emptyPath"] = "路径为空",
        ["rec.noPath"] = "路径不存在：",
        ["rec.fail"] = "回收站操作失败（错误码 0x{0}）",
        ["rec.aborted"] = "操作被取消",
        ["rec.stillThere"] = "文件仍存在（可能被占用或只读）",

        // 图标
        ["icons.credit"] = "图标来自 Morphicons（MIT License） · 24×24 描边款",
        ["log.iconReadFail"] = "图标没读到：",
        ["log.iconDirEmpty"] = "图标目录是空的：",
        ["log.iconParseFail"] = "图标解析失败：",

        // 日志窗口（用户可见）
        ["log.noProject"] = "没有打开项目",
        ["log.noMain"] = "找不到主类（没有 public static void main）",
        ["log.saveFail"] = "保存失败：",
        ["log.reverted"] = "回到上次更改前：",
        ["log.shotFail"] = "截图失败：",
        ["log.libsMissing"] = "还差 {0} 没下载",
        ["log.projectOpened"] = "打开项目 {0}（{1}）",
        ["log.projectCreated"] = "已创建项目 {0}（{1}，{2} 项）",
        ["log.noSample"] = "，不生成示例代码",
        ["log.fileCreated"] = "已创建 {0}.java",
        ["log.libsOnCp"] = "第三方库 {0} 个已在 classpath 上",
        ["st.projectOpened"] = "项目已打开：{0}",
        ["ask.recycle"] = "把「{0}」送进回收站？",
        ["ask.recycleProject"] = "把项目「{0}」整个目录送进回收站？\n\n{1}",
        ["log.autoSaved"] = "自动保存 {0}",
        ["log.savedFile"] = "已保存 {0}",
        ["st.editorInfo"] = "无 JDK  ·  UTF-8  ·  LF  ·  4",
        ["pk.pickDir"] = "选择目录",

        // 资源管理器右键菜单
        ["shell.unregistered"] = "已移除 {0} 项资源管理器右键菜单",
        ["shell.unregisterFail"] = "清理右键菜单失败：",

        // 启动期错误
        ["err.dllLoadFail"] = "核心模块 javastudio_core.dll 加载失败：",
        ["err.dllHint"] = "请确认它和 studio-64.exe 在同一个目录下。",
        ["err.bootTitle"] = "Java Studio 启动失败",
        ["err.bootBody"] = "程序在启动阶段崩溃，完整错误已写入：",
        ["err.seeLog"] = "…（后续见日志文件）",

        // JDK 下拉
        ["jdk.followProject"] = "跟随项目",
        ["jdk.projectJdk"] = "项目 JDK {0}",
        // 「位置」一栏对内嵌 JDK 显示什么。路径是安装目录里的，写出来没意义。
        ["jdk.builtin"] = "内置",

        // 对话框通用
        ["dlg.newProject"] = "新建项目",
        ["dlg.name"] = "名称",
        ["dlg.location"] = "位置",
        ["dlg.browse"] = "浏览…",
        ["dlg.build"] = "构建系统",
        ["dlg.jdk"] = "JDK",
        ["dlg.advanced"] = "高级设置（Maven 坐标）",
        ["dlg.group"] = "GroupId（包名）",
        ["dlg.artifact"] = "ArtifactId（留空就用项目名）",
        ["dlg.version"] = "Version",
        ["dlg.sample"] = "添加示例代码（生成一个带 main 的类）",
        ["dlg.git"] = "创建 Git 仓库（git init）",
        ["dlg.create"] = "创建",
        ["dlg.nameBad"] = "项目名只能包含字母和数字",
        ["dlg.groupBad"] = "包名必须以字母开头，且只能包含字母、数字、下划线和点",
        ["dlg.enterChars"] = "请输入字符",
        ["dlg.available"] = "可用",
        ["dlg.pickLocation"] = "选一个位置",
        ["dlg.bMaven"] = "Maven",
        ["dlg.bGradle"] = "Gradle",
        ["dlg.bNone"] = "无构建系统",
        ["dlg.newJavaFile"] = "新建 Java 文件",
        ["dlg.className"] = "类名（不用写 .java）",
        ["dlg.writeName"] = "写个类名",
        ["dlg.nameLetter"] = "类名得用字母开头",
        ["dlg.exists"] = "这个文件已经有了",
        ["dlg.settings"] = "设置",
        ["dlg.theme"] = "主题",
        ["dlg.language"] = "语言",
        ["dlg.extJdk"] = "选择本地 JDK（JDK 目录，填了就用它编译和运行）",
        ["dlg.noJavaExe"] = "这个目录里没有 bin\\java.exe，没保存",
        ["dlg.newItem"] = "新建",
        ["dlg.type"] = "类型",
        ["ni.javaClass"] = "Java 类",
        ["ni.javaFile"] = "Java 文件",
        ["ni.folder"] = "文件夹",
        ["ni.xml"] = "XML 文件",
        ["ni.custom"] = "自定义后缀文件",
        ["ni.empty"] = "名字不能为空",
        ["ni.badChars"] = "名字里有不能用的字符",
        ["ni.fileExists"] = "已经有一个同名文件了",
        ["ni.needExt"] = "自定义后缀要自己写全，比如 data.json、run.sh",
        ["ni.willCreate"] = "将创建：{0}",
        ["dlg.recent"] = "最近打开的文件",
        ["dlg.light"] = "浅色",
        ["dlg.dark"] = "深色",
        ["dlg.browsePlain"] = "浏览",
        ["dlg.defJdk"] = "默认 JDK 版本（内置）",
        ["dlg.save"] = "保存",
        ["dlg.about"] = "关于",
        ["about.jdk"] = "JDK",
        ["about.libs"] = "内置库",
        ["about.ok"] = "确定",
        ["about.core"] = "内核",
        ["about.config"] = "配置目录",
        ["about.icons.unit"] = "个",
        ["about.license.unknown"] = "见官方授权",
        ["about.version"] = "版本 {0}",
        ["about.version.unknown"] = "未标注",
        // 组件表里「运行时」那一行：值是 Environment.Version，运行时问出来的
        ["about.runtime"] = ".NET 运行时",
        ["about.intro"] = "用 C++ 内核 + C# / WPF 界面写的 Java 入门 IDE：建工程、写代码、编译运行、"
                        + "拉 Maven 依赖、探测 JDK，都在一个窗口里。",
        ["about.sec.components"] = "开源组件",
        ["about.sec.team"] = "开发",
        ["about.sec.env"] = "运行环境",
        ["about.col.component"] = "组件",
        ["about.col.version"] = "版本",
        ["about.col.license"] = "许可",
        ["about.own"] = "内核 javastudio_core.dll 与界面 studio-64.exe 都是本项目自有代码，除上表所列"
                      + "没有引入其它第三方库；界面里的图标来自 Morphicons，许可以其官方授权为准。",
        ["about.team"] = "开发团队",
        ["about.stack"] = "技术栈",
        // {0} 是运行时的 .NET 版本（Environment.Version）。C++ 20 是编译开关，
        // 运行时问不出来，所以只有这一段能实时取，写死就会过期。
        ["about.stack.value"] = "C++ 20 · C# / .NET {0} · WPF",
        ["about.copy"] = "复制信息",
        ["about.copied"] = "已复制",

        // Agent 界面（LLM 提示词/工具说明保持中文；只做界面）
        ["gate.title"] = "先填 API",
        ["gate.tip"] = "To-code Studio Pro 用的是你自己的 API，没有内置密钥；\n会保存在数据目录，下次不用重填。",
        ["gate.baseUrl"] = "Base URL",
        ["gate.apiKey"] = "API Key",
        ["gate.model"] = "模型",
        ["gate.ok"] = "保存并进入",
        ["gate.needBoth"] = "Base URL 和 API Key 都要填",
        ["agent.settings"] = "To-code Studio Pro · API 设置",
        ["agent.required"] = "要用 To-code Studio Pro 必须先填 API，没有内置密钥；\n进入前需要同时填好 Base URL 和 API Key。",
        ["agent.tip"] = "API 由你提供。默认是 DeepSeek；\n任何兼容 OpenAI 的接口（自建/代理/其他厂商）都能用，改 Base URL 即可。",
        ["agent.model"] = "模型",
        ["agent.fetchModels"] = "拉取模型列表",
        ["agent.temp"] = "温度（0–2，越小越稳）",
        ["agent.rounds"] = "每个提示词最多工具轮数",
        ["agent.confirm"] = "高危命令先询问（推荐）",
        ["agent.savedAt"] = "设置已保存于：",
        ["agent.save"] = "保存",
        ["agent.needBoth"] = "Base URL 和 API Key 都要填。",
        ["agent.fetchNeedBoth"] = "拉取模型列表前先把 Base URL 和 API Key 填好。",
        ["agent.fetching"] = "拉取中…",
        ["agent.fetchEmpty"] = "接口没返回任何模型——或者直接在下拉里输入模型名。",
        ["agent.fetchOk"] = "已拉取 {0} 个模型到下拉框。",
        ["agent.fetchFail"] = "拉取失败：",
        ["agent.fetchFailHint"] = "（或者直接在下拉里输入模型名）",
        ["agent.cfgTitle"] = "配置 API",
        ["agent.collapse"] = "收起面板",
        ["agent.newChat"] = "新会话（历史留在磁盘）",
        ["agent.gear"] = "Agent 设置",
        ["agent.send"] = "发送（Enter）",
        ["agent.attach"] = "附上文件（把内容发给模型）",
        ["agent.hint"] = "回车发送 · Shift+回车换行 · 只在当前项目内操作",
        ["agent.thinkingNow"] = "正在深度思考…",
        ["agent.thoughtDone"] = "已深度思考（用时 {0} 秒）",
        ["agent.thoughtPlain"] = "已深度思考",
        ["agent.thinkingTip"] = "模型的思考过程，点击展开/收起",
        ["agent.allowed"] = "已允许",
        ["agent.deniedNow"] = "已拒绝",
        ["agent.jumpBottom"] = "回到最新",
        ["agent.welcome"] = "要做什么",
        ["agent.welcomeSub"] = "描述你想要什么；我会读、改、运行你项目里的命令。\n高风险动作会先跟你确认。",
        ["agent.tool"] = "工具",
        ["agent.noApi"] = "没填 API——To-code Studio Pro 起不来。",
        ["agent.stopped"] = "已停止。",
        ["agent.attachTitle"] = "选一个要附加的文件",
        ["agent.confirmNote"] = "这个动作只在当前项目里生效。选拒绝的话我会换种方式。",
        ["agent.deny"] = "拒绝",
        ["agent.allow"] = "允许执行",
        ["agent.confirmTitle"] = "确认",
        ["agent.fileBlock"] = "\n\n【文件 {0}】\n```\n",
        ["agent.truncated"] = "\n…（太长已截断）",
        ["agent.readFail"] = "（读不出来：",
        ["agent.confirmDanger"] = "高危操作，确认执行？",
        ["agent.confirmCmd"] = "确认执行这条命令？",
        ["agent.cmdPrefix"] = "命令：",
        ["agent.delPrefix"] = "删除文件：",
        ["agent.denied"] = "用户拒绝了这次操作。",

        // ---- 新手引导：首启向导 + 界面巡礼 + 首页上手清单
        ["m.guide"] = "新手引导",
        ["guide.title"] = "新手引导",
        ["guide.next"] = "下一步",
        ["guide.prev"] = "上一步",
        ["guide.skip"] = "跳过",
        ["guide.stepOf"] = "第 {0} / {1} 步",

        ["guide.w.welcome.t"] = "欢迎使用 Java Studio",
        ["guide.w.welcome.d"] = "花一分钟做几项设置，之后就能直接写代码了。随时可以点「跳过」。",
        ["guide.w.theme.t"] = "挑个顺眼的外观",
        ["guide.w.theme.d"] = "之后在「视图 → 切换主题」里随时能改。",
        ["guide.w.theme.light"] = "浅色",
        ["guide.w.theme.dark"] = "深色",
        ["guide.w.jdk.t"] = "选一个 JDK",
        ["guide.w.jdk.d"] = "没检测到也别急，可以先跳过，之后到「设置」里指一下安装目录就行。",
        ["guide.w.jdk.none"] = "没有检测到 JDK",
        ["guide.w.project.t"] = "先建一个项目",
        ["guide.w.project.d"] = "下一步会建一个能直接编译运行的 Hello World。空着手进主界面没东西可看，先有个项目垫着，后面认界面也认得明白。",
        ["guide.w.project.yes"] = "创建示例项目",
        ["guide.w.project.fail"] = "示例项目没建起来，你可以自己点「新建项目」建一个。",
        ["guide.w.ready.t"] = "都齐了",
        ["guide.w.ready.d"] = "点「开始使用」，我们会带你认一遍界面各处是干嘛的。",
        ["guide.w.start"] = "开始使用",

        ["guide.t.toolbar.t"] = "顶部这一排",
        ["guide.t.toolbar.d"] = "新建、打开项目，编译运行这几个按钮都在这儿。左边还有 JDK 版本下拉。",
        ["guide.t.tree.t"] = "左边是项目树",
        ["guide.t.tree.d"] = "项目里的文件都列在这儿，双击打开。想增删文件就右键。",
        ["guide.t.editor.t"] = "中间是写代码的地方",
        ["guide.t.editor.d"] = "改过的文件和没保存的都会在这排在标签上标出来，Ctrl+S 保存。",
        ["guide.t.bottom.t"] = "底下这块看输出",
        ["guide.t.bottom.d"] = "输出是编译器说的话，日志是 IDE 自己的记录，问题专收报错，点一条能跳到那一行。",
        ["guide.t.status.t"] = "最底下的状态栏",
        ["guide.t.status.d"] = "编译进度、结果都在这儿报。出问题先看这行字。",
        ["guide.t.finish"] = "完成导览",

        ["guide.check.t"] = "快速上手",
        ["guide.check.sub"] = "{0} / {1} 已完成",
        ["guide.check.allDone"] = "全部完成，开始写代码吧",
        ["guide.check.hide"] = "收起",
        ["guide.check.show"] = "展开",
        ["guide.check.replay"] = "重新观看引导",
        ["guide.check.jdk"] = "配置 JDK",
        ["guide.check.project"] = "新建或打开一个项目",
        ["guide.check.build"] = "编译并运行一次",
        ["guide.check.guide"] = "看完界面导览",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["app.name"] = "Java Studio",
        ["app.slogan"] = "A Java IDE for beginners",

        ["home.new"] = "New Project",
        ["home.new.hint"] = "Scaffold a runnable project",
        ["home.list"] = "Projects",
        ["home.list.hint"] = "Pick up where you left off",
        ["home.open"] = "Open Project",
        ["home.open.hint"] = "Choose a folder on disk",
        ["home.settings"] = "Settings",
        ["home.settings.hint"] = "Theme · Language · Java",

        ["home.last"] = "Last opened",
        ["home.recent"] = "Projects",
        ["home.recent.empty"] = "No projects yet. Create one with New Project.",
        ["home.continue"] = "Continue",

        ["home.new.name"] = "Project name",
        ["home.new.group"] = "GroupId",
        ["home.new.artifact"] = "ArtifactId",
        ["home.new.location"] = "Location",
        ["home.new.build"] = "Build system",
        ["home.new.sample"] = "Add sample code",
        ["home.new.git"] = "Create Git repository",
        ["home.new.venv"] = "Create isolated runtime (venv)",
        ["home.new.create"] = "Create",
        ["home.new.bad"] = "Name may only contain letters, digits, _ and -",

        ["home.open.path"] = "Project folder",
        ["home.open.browse"] = "Browse",
        ["home.open.go"] = "Open",


        ["home.set.theme"] = "Theme",
        ["home.set.theme.light"] = "Light",
        ["home.set.theme.dark"] = "Dark",
        ["home.set.lang"] = "Language",
        ["home.set.java"] = "External Java (JDK folder)",
        ["home.set.java.browse"] = "Browse",
        ["home.set.java.use"] = "Use this Java",
        ["home.set.java.auto"] = "Use auto-detected JDK",
        ["home.set.java.bad"] = "No java.exe in that folder",
        ["home.set.java.ok"] = "External Java is in use",

        ["home.venv.create"] = "Create runtime",
        ["home.venv.run"] = "Run in the virtual environment",
        ["home.venv.done"] = "Runtime created",

        ["m.file"] = "File",
        ["m.newProject"] = "New Project...",
        ["m.openProject"] = "Open Project...",
        ["m.newFile"] = "New File",
        ["m.save"] = "Save",
        ["m.saveAll"] = "Save All",
        ["m.closeProject"] = "Close Project",
        ["m.exit"] = "Exit",
        ["m.edit"] = "Edit",
        ["m.undo"] = "Undo",
        ["m.redo"] = "Redo",
        ["m.revert"] = "Back to before last change",
        ["m.cut"] = "Cut",
        ["m.copy"] = "Copy",
        ["m.paste"] = "Paste",
        ["m.selectAll"] = "Select All",
        ["m.view"] = "View",
        ["m.home"] = "Home",
        ["m.editor"] = "Editor",
        ["m.tree"] = "Project Tool Window",
        ["m.bottom"] = "Output Tool Window",
        ["m.theme"] = "Toggle Theme",
        ["m.settings"] = "Settings",
        ["m.nav"] = "Navigate",
        ["m.main"] = "Main Class",
        ["m.run"] = "Run",
        ["m.compile"] = "Compile",
        ["m.compileRun"] = "Compile and Run",
        ["m.runOnly"] = "Run Only",
        ["m.check"] = "Inspect Code",
        ["m.venv"] = "Create Runtime",
        ["m.venvRun"] = "Run in Virtual Environment",
        ["m.tools"] = "Tools",
        ["m.libs"] = "Libraries...",
        ["m.icons"] = "Icon Library",
        ["m.help"] = "Help",
        ["m.about"] = "About Java Studio",
        ["m.rename"] = "Rename",
        ["m.delete"] = "Delete",
        ["m.importFile"] = "Import files...",
        ["m.importDir"] = "Import folder...",
        ["m.copyPath"] = "Copy path",
        ["tree.title"] = "Project",

        ["tip.new"] = "New Project",
        ["tip.open"] = "Open Project",
        ["tip.save"] = "Save",
        ["tip.compile"] = "Compile",
        ["tip.run"] = "Compile and Run",
        ["tip.check"] = "Inspect Code",
        ["tip.libs"] = "Libraries",
        ["tip.home"] = "Home",
        ["tip.tree"] = "Project Tool Window",
        ["tip.theme"] = "Toggle Theme",
        ["tip.settings"] = "Settings",
        ["tip.refresh"] = "Refresh project tree",

        ["st.ready"] = "Ready",
        ["st.opened"] = "Project opened: ",
        ["st.noProject"] = "Open a project first",
        ["st.compiling"] = "Compiling…",
        ["st.compiled"] = "Compiled",
        ["st.compileFail"] = "Compile failed",
        ["st.running"] = "Running…",
        ["st.runDone"] = "Finished",
        ["st.noMain"] = "No main class found",
        ["st.saved"] = "Saved",
        ["msg.ok"] = "OK",
        ["msg.cancel"] = "Cancel",
        ["msg.yes"] = "Yes",
        ["msg.no"] = "No",
        ["msg.error"] = "Error",
        ["st.savedFile"] = "Saved ",
        ["st.saveFail"] = "Save failed: ",
        ["st.libs"] = " on the classpath",
        ["st.libs0"] = "Libraries: ",
        ["st.checking"] = "Inspecting…",
        ["st.noProblem"] = "No problems found",
        ["st.problems"] = " problems",
        ["st.compileFailN"] = "Compile failed ({0} errors)",
        ["st.exitCode"] = "Exit code {0}",
        ["st.problemsN"] = "{0} problems",

        // Preview
        ["m.preview"] = "Preview",
        ["preview.of"] = "Preview",
        ["preview.unsupported"] = "This file type cannot be previewed yet.",
        ["preview.fail"] = "Preview failed: ",
        ["preview.none"] = "Select a file in the project tree first",
        ["preview.folder"] = "Preview works on files, not folders",
        ["md.mode.edit"] = "Edit",
        ["md.mode.split"] = "Split",
        ["md.mode.preview"] = "Preview",
        ["md.mode.tip"] = "Switch view (edit / split / preview)",
        ["md.swap.tip"] = "Swap the two panes (editor left / right)",

        // Libraries (LibsDialog: "Project deps" / "Local cache" pages)
        ["lib.title"] = "Libraries",
        ["lib.nav.deps"] = "Project deps",
        ["lib.nav.cache"] = "Local cache",
        ["lib.cand"] = "Add dependency",
        ["lib.candCount"] = "{0} of {1} catalog entries listed",
        ["lib.candEmpty"] = "Nothing matches. Try another keyword, or hit \"Search Maven\" to look in the repository.",
        ["lib.deps"] = "Dependencies of this project",
        ["lib.depCount"] = "{0} total",
        ["lib.depsEmpty"] = "This project has no dependencies yet. Pick one on the left, or type groupId / artifactId / version by hand.",
        ["lib.add"] = "Add",
        ["lib.added"] = "Added",
        ["lib.remove"] = "Remove",
        ["lib.download"] = "Download",
        ["lib.downloading"] = "Downloading…",
        ["lib.downloadFail"] = "Download failed",
        ["lib.progress"] = "{0}% downloaded",
        ["lib.progressBytes"] = "{0} downloaded",
        ["lib.ready"] = "Ready",
        ["lib.missing"] = "Not downloaded",
        ["lib.searchRepo"] = "Search Maven",
        ["lib.backCatalog"] = "Back to catalog",
        ["lib.searching"] = "Searching… the first call can take a while (longer through a proxy)",
        ["lib.repoCount"] = "{0} hits from the repository",
        ["lib.repoEmpty"] = "No hits. Try another keyword, or just type the three coordinates.",
        ["lib.searchFail"] = "Search failed: {0}",
        ["lib.searchTimeout"] = "Too slow, gave up. Check your network or proxy, then try again.",
        ["lib.group"] = "groupId",
        ["lib.artifact"] = "artifactId",
        ["lib.version"] = "version",
        ["lib.coordBad"] = "A coordinate is groupId:artifactId:version",
        ["lib.src.pom"] = "This project has a pom.xml, so dependencies go into <dependencies>.",
        ["lib.src.txt"] = "No pom.xml here — deps are kept in .javastudio\\libs.txt, which builds the classpath for plain javac builds.",
        ["lib.cache"] = "Jars already on this machine",
        ["lib.cache.open"] = "Open folder",
        ["lib.cache.total"] = "{0} jars · {1}",
        ["lib.cache.empty"] = "The cache is empty. Add a dependency and hit \"Download\", then it shows up here.",
        ["lib.cache.deleted"] = "Deleted {0}",
        ["lib.apply"] = "Apply",

        ["libs.noBuiltinJdk"] = "No built-in JDK found",
        ["log.folderCreated"] = "Created folder {0}",
        ["log.itemCreated"] = "Created {0}",
        ["log.downLoaded"] = "Downloaded {0}",
        ["log.downloadFail"] = "Download failed: ",
        ["log.downloadError"] = "Download error: ",
        ["log.searchFail"] = "Repository search failed: ",

        // Project tree context menu
        ["m.deleteProject"] = "Delete Project",
        ["ctx.newJavaClass"] = "New Java Class",
        ["ctx.newJavaFile"] = "New Java File",
        ["ctx.newFolder"] = "New Folder",
        ["ctx.newXml"] = "New XML File",
        ["ctx.newCustom"] = "New Custom-Extension File",
        ["ctx.importFile"] = "Import File...",
        ["ctx.importDir"] = "Import Folder...",
        ["ctx.rename"] = "Rename...",
        ["ctx.delete"] = "Delete",
        ["ctx.copyPath"] = "Copy Path",

        // Editor context menu
        ["ed.undo"] = "Undo",
        ["ed.redo"] = "Redo",
        ["ed.cut"] = "Cut",
        ["ed.copy"] = "Copy",
        ["ed.paste"] = "Paste",
        ["ed.selectAll"] = "Select All",
        ["ed.complete"] = "Complete (Ctrl+Space)",
        ["ed.indent"] = "Indent 4 spaces (Tab)",
        ["ed.save"] = "Save",

        // Icon library: how to use this icon set
        ["icons.usage"] = "How to use these icons",
        ["icons.copied"] = "Icon name copied: ",
        ["icons.t.kTitle"] = "1) Inside this IDE's own UI (C#)",
        ["icons.t.kBody"] = "Icons.Visual(\"coffee\", 22, Ui.B(\"Brush.Fg.Primary\"));",
        ["icons.t.pTitle"] = "2) Inside your project",
        ["icons.t.pBody"] = "Every new project ships the same set under javastudio-icon/\n" +
                            "at its root, plus an icons.json:\n" +
                            "    javastudio-icon/coffee.svg",
        ["icons.t.jTitle"] = "3) Reading it from Java",
        ["icons.t.jBody"] = "// move the folder into src/main/resources/icons/ first\n" +
                            "InputStream in = Main.class.getResourceAsStream(\"/icons/coffee.svg\");\n" +
                            "// SVG is not a bitmap: ImageIO.read cannot decode it.\n" +
                            "// Render it with jsvg / Batik into a BufferedImage.",
        ["icons.t.tip"] = "An icon's name is its filename without .svg. Click any tile to copy the name.",

        // Agent UI strings (LLM prompt/tool descriptions stay Chinese; UI only)
        ["gate.title"] = "Enter API first",
        ["gate.tip"] = "To-code Studio Pro uses your own API. No key is built in;\nit's saved in the data folder so you won't retype it next time.",
        ["gate.baseUrl"] = "Base URL",
        ["gate.apiKey"] = "API Key",
        ["gate.model"] = "Model",
        ["gate.ok"] = "Save & Enter",
        ["gate.needBoth"] = "Both Base URL and API Key are required",
        ["agent.settings"] = "To-code Studio Pro · API Settings",
        ["agent.required"] = "To use To-code Studio Pro you must enter an API first — no key is built in;\nboth Base URL and API Key are required before you can start.",
        ["agent.tip"] = "You supply the API. The default is DeepSeek;\nany OpenAI-compatible endpoint (self-hosted / proxy / other vendor) works by changing the Base URL.",
        ["agent.model"] = "Model",
        ["agent.fetchModels"] = "Fetch Model List",
        ["agent.temp"] = "Temperature (0–2, lower = steadier)",
        ["agent.rounds"] = "Max tool rounds per prompt",
        ["agent.confirm"] = "Ask before high-risk commands (recommended)",
        ["agent.savedAt"] = "Settings saved at:",
        ["agent.save"] = "Save",
        ["agent.needBoth"] = "Both Base URL and API Key are required.",
        ["agent.fetchNeedBoth"] = "Fill in Base URL and API Key before fetching the model list.",
        ["agent.fetching"] = "Fetching…",
        ["agent.fetchEmpty"] = "The endpoint returned no models — or just type a model name in the dropdown.",
        ["agent.fetchOk"] = "Fetched {0} models into the dropdown.",
        ["agent.fetchFail"] = "Fetch failed: ",
        ["agent.fetchFailHint"] = " (or just type a model name in the dropdown)",
        ["agent.cfgTitle"] = "Configure API",
        ["agent.collapse"] = "Collapse panel",
        ["agent.newChat"] = "New chat (history stays on disk)",
        ["agent.gear"] = "Agent Settings",
        ["agent.send"] = "Send (Enter)",
        ["agent.attach"] = "Attach file (send its contents to the model)",
        ["agent.hint"] = "Enter to send · Shift+Enter for newline · only operates within the current project",
        ["agent.thinkingNow"] = "Thinking deeply…",
        ["agent.thoughtDone"] = "Thought deeply ({0}s)",
        ["agent.thoughtPlain"] = "Thought deeply",
        ["agent.thinkingTip"] = "Model's reasoning — click to expand or collapse",
        ["agent.allowed"] = "Allowed",
        ["agent.deniedNow"] = "Denied",
        ["agent.jumpBottom"] = "Jump to latest",
        ["agent.welcome"] = "What should we build",
        ["agent.welcomeSub"] = "Describe what you want; I'll read, edit, and run commands in your project.\nHigh-risk actions are confirmed with you first.",
        ["agent.tool"] = "Tool",
        ["agent.noApi"] = "No API entered — To-code Studio Pro can't start.",
        ["agent.stopped"] = "Stopped.",
        ["agent.attachTitle"] = "Choose a file to attach",
        ["agent.confirmNote"] = "This action only takes effect inside the current project. Choose Deny and I'll try another way.",
        ["agent.deny"] = "Deny",
        ["agent.allow"] = "Allow",
        ["agent.confirmTitle"] = "Confirm",
        ["agent.fileBlock"] = "\n\n[File {0}]\n```\n",
        ["agent.truncated"] = "\n… (truncated, too long)",
        ["agent.readFail"] = "(can't read: ",
        ["agent.confirmDanger"] = "High-risk action — confirm execution?",
        ["agent.confirmCmd"] = "Run this command?",
        ["agent.cmdPrefix"] = "Command: ",
        ["agent.delPrefix"] = "Delete file: ",
        ["agent.denied"] = "The user denied this action.",

        // Toolbar / bottom / tree / JDK
        ["tip.newFile"] = "New",
        ["tip.openImport"] = "Open / Import",
        ["tip.newFileFolder"] = "New File / Folder",
        ["tip.importFile"] = "Import File",
        ["tip.jdk"] = "JDK for compile/run (blank = project's version)",
        ["tip.hideBottom"] = "Hide Tool Window",
        ["bottom.output"] = "Output",
        ["bottom.log"] = "Log",
        ["bottom.problems"] = "Problems",

        // Status (transient)
        ["st.importDone"] = "Imported {0} file(s)",
        ["st.importSkip"] = " (skipped {0} existing)",
        ["st.importFail"] = "Import failed: ",
        ["st.selectFolder"] = "Select a folder in the project tree first",
        ["st.selectItem"] = "Select an item in the tree first",
        ["st.created"] = "Created ",
        ["st.openFail"] = "Open failed: ",
        ["st.renameDone"] = "Renamed",
        ["st.renameFail"] = "Rename failed: ",
        ["st.deleteFail"] = "Delete failed: ",
        ["st.pathCopied"] = "Path copied",
        ["st.deleteDone"] = "Deleted ",
        ["st.treeRefreshed"] = "Project tree refreshed",
        ["st.noApiProject"] = "Open a project before using To-code Studio Pro",
        ["st.needProject"] = "Open a project first",
        ["st.runFail"] = "Run failed",
        ["st.importFolderDone"] = "Imported folder ",
        ["st.noEarlier"] = "No earlier version of this file",
        ["st.reverted"] = "Reverted to last saved",
        ["st.noSrc"] = "Source file for {0} not found",
        ["st.cantLocate"] = "JAVA Studio can't locate the problem",
        ["st.mainClass"] = "Main class ",

        // Problems window
        ["prob.summary"] = "{0} errors · {1} warnings",
        ["prob.unknownFile"] = "Unknown file",
        ["prob.cantLocateFile"] = "JAVA Studio can't locate the problem ({0})",

        // Custom pickers
        ["pk.importFileTo"] = "Import files to {0}",
        ["pk.importFolderTo"] = "Import folder to {0}",
        ["pk.desktop"] = "Desktop",
        ["pk.documents"] = "Documents",
        ["pk.downloads"] = "Downloads",
        ["pk.thisPc"] = "This PC",
        ["pk.empty"] = "(empty)",
        ["pk.up"] = "Up one level",
        ["pk.selectFolder"] = "Select this folder",
        ["pk.open"] = "Open",
        ["pk.fileType"] = "File type",
        ["pk.fileTypeAll"] = "All files",

        // Recycle bin
        ["rec.emptyPath"] = "Path is empty",
        ["rec.noPath"] = "Path does not exist: ",
        ["rec.fail"] = "Recycle bin operation failed (error 0x{0})",
        ["rec.aborted"] = "Operation cancelled",
        ["rec.stillThere"] = "File still exists (may be in use or read-only)",

        // Icons
        ["icons.credit"] = "Icons by Morphicons (MIT License) · 24x24 outline",
        ["log.iconReadFail"] = "Could not read icons: ",
        ["log.iconDirEmpty"] = "Icon directory is empty: ",
        ["log.iconParseFail"] = "Icon parse failed: ",

        // Log window (user visible)
        ["log.noProject"] = "No project open",
        ["log.noMain"] = "No main class found (no public static void main)",
        ["log.saveFail"] = "Save failed: ",
        ["log.reverted"] = "Reverted to last saved: ",
        ["log.shotFail"] = "Screenshot failed: ",
        ["log.libsMissing"] = "Still missing: {0}",
        ["log.projectOpened"] = "Opened project {0} ({1})",
        ["log.projectCreated"] = "Created project {0} ({1}, {2} items)",
        ["log.noSample"] = ", no sample code",
        ["log.fileCreated"] = "Created {0}.java",
        ["log.libsOnCp"] = "{0} libraries on classpath",
        ["st.projectOpened"] = "Project opened: {0}",
        ["ask.recycle"] = "Send '{0}' to the Recycle Bin?",
        ["ask.recycleProject"] = "Send the whole project '{0}' directory to the Recycle Bin?\n\n{1}",
        ["log.autoSaved"] = "Auto-saved {0}",
        ["log.savedFile"] = "Saved {0}",
        ["st.editorInfo"] = "No JDK  ·  UTF-8  ·  LF  ·  4",
        ["pk.pickDir"] = "Select a directory",

        // Explorer context menu
        ["shell.unregistered"] = "Removed {0} Explorer context menu entries",
        ["shell.unregisterFail"] = "Failed to clean up context menu: ",

        // Startup errors
        ["err.dllLoadFail"] = "Failed to load core module javastudio_core.dll: ",
        ["err.dllHint"] = "Make sure it sits in the same directory as studio-64.exe.",
        ["err.bootTitle"] = "Java Studio failed to start",
        ["err.bootBody"] = "The app crashed during startup. Full error written to: ",
        ["err.seeLog"] = "… (see the log file for the rest)",

        // JDK combo
        ["jdk.followProject"] = "Follow project",
        ["jdk.projectJdk"] = "Project JDK {0}",
        ["jdk.builtin"] = "Built-in",

        // Dialogs
        ["dlg.newProject"] = "New Project",
        ["dlg.name"] = "Name",
        ["dlg.location"] = "Location",
        ["dlg.browse"] = "Browse…",
        ["dlg.build"] = "Build system",
        ["dlg.jdk"] = "JDK",
        ["dlg.advanced"] = "Advanced (Maven coordinates)",
        ["dlg.group"] = "GroupId (package)",
        ["dlg.artifact"] = "ArtifactId (blank = project name)",
        ["dlg.version"] = "Version",
        ["dlg.sample"] = "Add sample code (a class with main)",
        ["dlg.git"] = "Create Git repository (git init)",
        ["dlg.create"] = "Create",
        ["dlg.nameBad"] = "Name may only contain letters and digits",
        ["dlg.groupBad"] = "Package must start with a letter and contain only letters, digits, _ and .",
        ["dlg.enterChars"] = "Please enter characters",
        ["dlg.available"] = "OK",
        ["dlg.pickLocation"] = "Pick a location",
        ["dlg.bMaven"] = "Maven",
        ["dlg.bGradle"] = "Gradle",
        ["dlg.bNone"] = "No build system",
        ["dlg.newJavaFile"] = "New Java File",
        ["dlg.className"] = "Class name (no .java)",
        ["dlg.writeName"] = "Type a class name",
        ["dlg.nameLetter"] = "Class name must start with a letter",
        ["dlg.exists"] = "File already exists",
        ["dlg.settings"] = "Settings",
        ["dlg.theme"] = "Theme",
        ["dlg.language"] = "Language",
        ["dlg.extJdk"] = "Local JDK (a JDK folder; used to compile and run)",
        ["dlg.noJavaExe"] = "No bin\\java.exe in this folder; not saved",
        ["dlg.newItem"] = "New",
        ["dlg.type"] = "Type",
        ["ni.javaClass"] = "Java Class",
        ["ni.javaFile"] = "Java File",
        ["ni.folder"] = "Folder",
        ["ni.xml"] = "XML File",
        ["ni.custom"] = "Custom-extension File",
        ["ni.empty"] = "Name cannot be empty",
        ["ni.badChars"] = "Name contains invalid characters",
        ["ni.fileExists"] = "A file with this name already exists",
        ["ni.needExt"] = "Type the extension yourself, e.g. data.json or run.sh",
        ["ni.willCreate"] = "Will create: {0}",
        ["dlg.recent"] = "Recent Files",
        ["dlg.light"] = "Light",
        ["dlg.dark"] = "Dark",
        ["dlg.browsePlain"] = "Browse",
        ["dlg.defJdk"] = "Default JDK version (built-in)",
        ["dlg.save"] = "Save",
        ["dlg.about"] = "About",
        ["about.jdk"] = "JDK",
        ["about.libs"] = "Built-in libraries",
        ["about.ok"] = "OK",
        ["about.core"] = "Core",
        ["about.config"] = "Config folder",
        ["about.icons.unit"] = "icons",
        ["about.license.unknown"] = "See license",
        ["about.version"] = "Version {0}",
        ["about.version.unknown"] = "unversioned",
        ["about.runtime"] = ".NET runtime",
        ["about.intro"] = "A beginner-friendly Java IDE built on a C++ core with a C# / WPF interface: "
                        + "create projects, write code, build and run, pull Maven dependencies and "
                        + "detect JDKs — all in one window.",
        ["about.sec.components"] = "Open-source components",
        ["about.sec.team"] = "Development",
        ["about.sec.env"] = "Environment",
        ["about.col.component"] = "Component",
        ["about.col.version"] = "Version",
        ["about.col.license"] = "License",
        ["about.own"] = "The javastudio_core.dll core and the studio-64.exe interface are both our own "
                      + "code; apart from the components listed above, no third-party libraries are used. "
                      + "The icons in the UI come from Morphicons, licensed under its upstream terms.",
        ["about.team"] = "Team",
        ["about.stack"] = "Stack",
        ["about.stack.value"] = "C++ 20 · C# / .NET {0} · WPF",
        ["about.copy"] = "Copy info",
        ["about.copied"] = "Copied",

        // ---- Getting started: first-run wizard + UI tour + home checklist
        ["m.guide"] = "Getting Started",
        ["guide.title"] = "Getting Started",
        ["guide.next"] = "Next",
        ["guide.prev"] = "Back",
        ["guide.skip"] = "Skip",
        ["guide.stepOf"] = "Step {0} of {1}",

        ["guide.w.welcome.t"] = "Welcome to Java Studio",
        ["guide.w.welcome.d"] = "A minute of setup and you're ready to write code. You can skip at any point.",
        ["guide.w.theme.t"] = "Pick a look",
        ["guide.w.theme.d"] = "Change it any time later under View → Toggle Theme.",
        ["guide.w.theme.light"] = "Light",
        ["guide.w.theme.dark"] = "Dark",
        ["guide.w.jdk.t"] = "Choose a JDK",
        ["guide.w.jdk.d"] = "Nothing detected? Skip this — you can point to the install folder later in Settings.",
        ["guide.w.jdk.none"] = "No JDK found",
        ["guide.w.project.t"] = "Create a project",
        ["guide.w.project.d"] = "Next we create a ready-to-compile Hello World. Arriving at an empty workspace shows nothing, and the tour needs something real to point at.",
        ["guide.w.project.yes"] = "Create sample project",
        ["guide.w.project.fail"] = "Couldn't create the sample project — use New Project to make one yourself.",
        ["guide.w.ready.t"] = "You're all set",
        ["guide.w.ready.d"] = "Click Get Started and we'll show you around the interface.",
        ["guide.w.start"] = "Get started",

        ["guide.t.toolbar.t"] = "The toolbar up here",
        ["guide.t.toolbar.d"] = "New and Open project, plus Build and Run. The JDK picker sits on the left.",
        ["guide.t.tree.t"] = "Project tree on the left",
        ["guide.t.tree.d"] = "Every file in the project, double-click to open. Right-click to add or remove files.",
        ["guide.t.editor.t"] = "Editor in the middle",
        ["guide.t.editor.d"] = "Tabs show which files changed or have unsaved edits. Ctrl+S to save.",
        ["guide.t.bottom.t"] = "Output along the bottom",
        ["guide.t.bottom.d"] = "Build output from the compiler, IDE log, and Problems — click a problem to jump to that line.",
        ["guide.t.status.t"] = "Status bar at the very bottom",
        ["guide.t.status.d"] = "Build progress and results report here. Check this line first when something fails.",
        ["guide.t.finish"] = "Finish tour",

        ["guide.check.t"] = "Quick start",
        ["guide.check.sub"] = "{0} of {1} done",
        ["guide.check.allDone"] = "All done — time to write some code",
        ["guide.check.hide"] = "Hide",
        ["guide.check.show"] = "Show",
        ["guide.check.replay"] = "Replay guide",
        ["guide.check.jdk"] = "Set up JDK",
        ["guide.check.project"] = "Create or open a project",
        ["guide.check.build"] = "Build and run once",
        ["guide.check.guide"] = "Take the interface tour",
    };

    private static string _lang = ZH;

    public static string Current => _lang;

    public static string T(string key)
    {
        var d = _lang == EN ? En : Zh;
        if (d.TryGetValue(key, out var v)) return v;
        Zh.TryGetValue(key, out var zh);
        return zh ?? key;
    }

    public static void Load()
    {
        // App 启动时已经 Config.Load()，这里优先用 Configuration.json 里的值
        var s = !string.IsNullOrEmpty(Config.Language) ? Config.Language : Core.Setting("lang");
        _lang = s == EN ? EN : ZH;
        PushToCore();
    }

    public static void Set(string lang)
    {
        _lang = lang == EN ? EN : ZH;
        Core.SetSetting("lang", _lang);
        Config.Language = _lang;
        Config.Save();
        PushToCore();
    }

    /// <summary>
    /// 把语言同步给 C++ 内核。
    /// 内核的「构建输出」面板里有一部分行是它自己拼的（「编译完成」「产物目录」…），
    /// 不告诉它切换的话，界面全英文了、输出区还是一条中文。
    /// </summary>
    private static void PushToCore()
    {
        try { Native.BuildSetLang(_lang == EN ? EN : ZH); }
        catch { /* 内核还没 Init，或者旧 DLL 没这个导出：忽略，退回中文 */ }
    }

    /// <summary>遍历菜单，把 Header 换成当前语言的文案（key 写在 Tag 上）。</summary>
    public static void ApplyMenu(ItemsControl menu)
    {
        foreach (var o in menu.Items)
        {
            if (o is not MenuItem mi) continue;
            if (mi.Tag is string key && !string.IsNullOrEmpty(key))
                mi.Header = T(key);
            ApplyMenu(mi);
        }
    }

    /// <summary>
    /// 给控件打「这是哪个 key」的戳。打完之后 Translate 就能把它刷成当前语言。
    /// 弹窗是构造时 New 出来就不动了，光靠构造那一刻的 T() 锁死了语言。
    /// </summary>
    public static void Tag(FrameworkElement el, string key)
    {
        if (el != null) el.Tag = key;
    }

    /// <summary>
    /// 把一整棵控件树里打过戳的文字全部换成当前语言。
    ///
    /// 认四种戳：
    ///   - Tag 直接是 key（标题/标签/按钮/复选框）
    ///   - ToolTip 是 key
    ///   - ItemsControl 里 Content 是 key（下拉选项）
    ///   - Tag 形如 "fmt:key|值" —— 「标签: 值」这种半动态的行，只换标签那半截
    /// 没打戳的一律不碰——比如用户自己的项目名、路径，那些不该被翻译。
    /// </summary>
    public static void Translate(DependencyObject node)
    {
        if (node == null) return;

        if (node is FrameworkElement fe)
        {
            if (fe.Tag is string raw && !string.IsNullOrEmpty(raw))
            {
                // "fmt:key|值" → 用 key 取文案再把值填进去
                if (raw.StartsWith("fmt:", StringComparison.Ordinal))
                {
                    int bar = raw.IndexOf('|');
                    if (bar > 4 && fe is TextBlock ft)
                    {
                        string k = raw.Substring(4, bar - 4);
                        string v = raw.Substring(bar + 1);
                        ft.Text = string.Format(T(k), v);
                    }
                }
                else
                {
                    switch (fe)
                    {
                        case TextBlock tb: tb.Text = T(raw); break;
                        case Button b: b.Content = T(raw); break;
                        case MenuItem mi: mi.Header = T(raw); break;
                        case Window w: w.Title = T(raw); break;
                        case Label l: l.Content = T(raw); break;
                        case CheckBox cb: cb.Content = T(raw); break;
                        case RadioButton rb: rb.Content = T(raw); break;
                        case Expander ex: ex.Header = T(raw); break;
                    }
                }
            }

            if (fe.ToolTip is string k2 && !string.IsNullOrEmpty(k2))
                fe.ToolTip = T(k2);
        }

        // 下拉/列表里的选项：Content 存的是 key，不是控件，单独换
        if (node is ItemsControl ic)
        {
            for (int i = 0; i < ic.Items.Count; i++)
            {
                if (ic.Items[i] is not string s || string.IsNullOrEmpty(s)) continue;
                string tr = T(s);
                if (tr != s) ic.Items[i] = tr;
            }
        }

        // 往下走。逻辑树优先（弹窗是代码拼的，逻辑子节点最全）；
        // 逻辑树接不上（XAML 里带模板的）再退回可视树。
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            Translate(child as DependencyObject);
    }
}
