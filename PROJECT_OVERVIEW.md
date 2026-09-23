# TradosToolkit 项目文档

## 项目简介

TradosToolkit 是一个 **Trados Studio 2019 翻译增强插件**，使用 C# / .NET Framework 4.8 / WPF 开发，产出单个 `.sdlplugin` 包，面向内网离线环境。提供 TM→LLM 级联机器翻译、工作台浏览器、编辑器内 LLM 对话改译、QA 批处理、收件箱自动产出三件套和本地 HTTP 自动化 API。

## 技术栈

| 维度 | 选型 |
|---|---|
| 语言 | C# |
| 框架 | .NET Framework 4.8（SDK 风格 csproj） |
| UI 框架 | WPF + HandyControl 3.5.1 |
| 平台目标 | x86（兼容 Studio 2019 32 位） |
| 浏览器内核 | Microsoft.Web.WebView2 1.0.4191.47 |
| 插件 SDK | Sdl.Core.PluginFramework 2.1.0 |
| Trados API | 引用本机 Studio15 DLL（HintPath，不随包分发） |
| 数据库 | System.Data.SQLite（复用 Studio 自带版本） |
| 构建工具 | MSBuild (VS 2022 BuildTools)，离线 NuGet |

## 目录结构

```
trados-plugin/
├── TradosToolkit.sln               VS 解决方案
├── TradosToolkit/                  插件主源码
│   ├── TradosToolkit.csproj        SDK 风格工程文件
│   ├── pluginpackage.manifest.xml  插件清单（兼容 Studio 15.0–19.9）
│   ├── ToolkitConfig.cs            配置中心（%APPDATA%\TradosToolkit\config.json）
│   ├── UiText.cs                   国际化资源读取
│   ├── Action/                     Ribbon 按钮入口（8 个）
│   ├── TranslationProvider/        级联翻译提供程序（TM 优先 → LLM 兜底）
│   ├── Workbench/                  工作台与翻译中心
│   ├── EditorPanel/                编辑器扩展面板（LLM 改译 + 文档预览）
│   ├── Server/                     本地 HTTP API（端口 53902）
│   ├── BatchTasks/                 QA 批处理任务
│   ├── Glossaries/                 术语库数据层（SQLite）
│   ├── TerminologySource/          原生术语源（ITerminologyProvider）
│   ├── TranslationMemories/        翻译记忆库管理
│   ├── Inbox/                      收件箱自动产出
│   ├── Convert/                    文件转换（任意文件 → sdlxliff）
│   ├── Common/                     通用工具库
│   └── Diagnostics/                诊断与日志
├── docs/                           文档（API、签名、离线部署、路线图）
├── third_party/nuget/              vendored NuGet 包（离线 restore）
├── tools/                          构建辅助脚本（签名、图标、测试）
├── release/                        签名 .sdlplugin（免构建分发）
└── references/                     参考资料（不参与构建）
```

## 核心模块

| 模块 | 职责 |
|---|---|
| `Action/` | 8 个 Ribbon 按钮，`TradosToolkitRibbonGroup` 构造函数自启 API/收件箱/TM 索引 |
| `TranslationProvider/` | 级联翻译：TM 优先 → LLM 兜底（OpenAI 兼容，`127.0.0.1:8765`），带段去重和磁盘缓存 |
| `Workbench/` | 主配置窗口 + 翻译中心（WebView2 浏览器 + Chrome 式书签树） |
| `EditorPanel/` | 编辑器右侧 LLM 对话改译面板 + 文档预览 |
| `Server/` | 本地 HTTP API（端口 53902，令牌鉴权），`ProjectApi.cs`(107KB) 是最大文件 |
| `Glossaries/` | SQLite 术语库数据层 |
| `TerminologySource/` | 原生 `ITerminologyProvider` 实现 |
| `TranslationMemories/` | 本地 TM 扫描/索引/定时重建 |
| `Inbox/` | 目录监视 → 自动产出三件套（分析报告+.sdlppx+.sdltm） |
| `Convert/` | 任意文件 → sdlxliff 转换 |
| `BatchTasks/` | QA 批处理（术语/一致性检查） |

## 架构特点

1. **单包离线**：依赖全 vendor 进 `third_party/nuget`，NuGet 不联网；SQLite 复用 Studio 自带版本避免 native 冲突
2. **Ribbon 自启**：Studio 启动即通过 `TradosToolkitRibbonGroup` 拉起 HTTP API、收件箱监视、TM 索引调度三大后台服务
3. **双控制面**：人工用 Ribbon/UI，自动化用本地 HTTP API（端口 53902，令牌鉴权）
4. **级联翻译**：TM 优先 + LLM 兜底，LLM 走 OpenAI 兼容协议
5. **多版本兼容**：编译期引用 Studio15 SDK，manifest 声明 15.0–19.9，一套代码兼容 2019–2026
6. **国际化**：中英双语 resx，文件型资源按 UI 文化自动切换

## 构建与部署

**构建前提**：构建机已安装 Trados Studio 2019（csproj 通过 HintPath 引用其 DLL）。

**构建命令**：
```bash
MSBuild.exe TradosToolkit/TradosToolkit.csproj -t:Rebuild -p:Configuration=Release
```

**自动部署**：构建后自动部署到 `%APPDATA%\SDL\SDL Trados Studio\15\Plugins\Packages\` 并自签。

**免构建安装**：把 `release/TradosToolkit.sdlplugin` 拷到目标机 Packages 目录，启动 Studio 自动解包。

## 关键入口

- 插件标识：`TradosToolkit/Properties/PluginProperties.cs` → `[assembly: Plugin("Plugin_Name")]`
- 主入口：`TradosToolkit/Action/TradosToolkitRibbonGroup.cs`（Studio 启动时实例化）
- 配置中心：`TradosToolkit/ToolkitConfig.cs`（持久化到 `%APPDATA%\TradosToolkit\config.json`）
- 插件清单：`TradosToolkit/pluginpackage.manifest.xml`
- 发布包：`release/TradosToolkit.sdlplugin`（3MB，已签名）