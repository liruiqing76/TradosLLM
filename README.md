# TradosToolkit

Trados Studio 2019（Studio15）翻译插件：**单个 .sdlplugin 兼容内网离线环境**，提供 TM→LLM 级联机器翻译、工作台浏览器（翻译中心）、编辑器内 LLM 对话改译、QA 批处理和本地 HTTP 自动化 API。

## 功能一览

| 模块 | 说明 | 代码位置 |
|---|---|---|
| 级联翻译提供程序 | TM 优先，无匹配/低匹配时走本地 LLM 网关（OpenAI 兼容，`127.0.0.1:8765`），作为单一 MT provider 挂入 Studio | `TradosToolkit/TranslationProvider/` |
| 工作台（Workbench） | Home Ribbon「Workbench」按钮 + 图标，打开配置窗口（连接、密钥、级联阈值等，按页签拆分，改动即持久化） | `TradosToolkit/Workbench/` |
| 翻译中心（Translation Center） | 左导航 View：内嵌 WebView2 浏览器 + Chrome 式书签树；无 WebView2 运行时优雅降级为纯书签树 | `TradosToolkit/Workbench/` |
| 编辑器 LLM 助手 | 编辑器右侧 ViewPart：对当前段对话改译并写回 | `TradosToolkit/EditorPanel/` |
| QA 批处理 | 批量检查任务（术语/一致性等） | `TradosToolkit/BatchTasks/` |
| 本地 HTTP API | 端口 `53902`，从外部脚本读写项目段、触发预翻译等 | `TradosToolkit/Server/`，文档 [docs/API.md](docs/API.md) |
| 术语库 | SQLite（用 Studio 自带 sqlite 程序集，避免 native 版本冲突） | `TradosToolkit/Glossaries/` |

界面国际化：中性资源 = 英文，`PluginResources.zh-CN.resx` = 中文，Studio 按界面语言自动切换（图标键仅存在于中性资源，中文环境自动回退）。

## 构建

前提：构建机已安装 **Trados Studio 2019**（csproj 通过 HintPath 引用其 DLL，无法脱离 Studio 构建）。

```bash
# 仓库根目录；依赖 NuGet 包已 vendor 在 third_party/nuget/，nuget.config 只指向本地源，全程离线
"/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" \
  TradosToolkit/TradosToolkit.csproj -t:Rebuild -p:Configuration=Release
```

- 默认 `DeployPluginPackage=true`：构建后自动部署到 `%APPDATA%\SDL\SDL Trados Studio\15\Plugins\Packages\` 并清空 Unpacked（下次启动 Studio 自动重解包）。**部署前须关闭 Studio**。
- 只构建不部署：加 `-p:DeployPluginPackage=false`。
- 每次构建都会自动完成：生成清单 → 用自签证书签名 .sdlplugin（消除启动时"未注册插件"Yes/No 弹窗）→ 刷新 `release/TradosToolkit.sdlplugin`。

## 安装到目标机（免构建）

1. 关闭 Studio。
2. 把 `release/TradosToolkit.sdlplugin` 拷进 `%APPDATA%\SDL\SDL Trados Studio\15\Plugins\Packages\`。
3. 启动 Studio，首次会从包自动解包。详见 [release/README.md](release/README.md)。

内网离线部署（含 Studio 版本差异、排障）见 [docs/OFFLINE_DEPLOY.md](docs/OFFLINE_DEPLOY.md)。

## 文档

- [docs/API.md](docs/API.md) — 本地 HTTP API 完整说明
- [docs/SDL_PLUGIN_SIGNING.md](docs/SDL_PLUGIN_SIGNING.md) — 自签 .sdlplugin 消除未注册弹窗的完整原理与做法（含反编译证据链，可复用到其他 SDL 插件）
- [docs/ROADMAP.md](docs/ROADMAP.md) — 优化路线图（术语库升级为原生 ITermbaseProvider 的可行性 + 体验优化提案，均为计划未实施）
- [docs/OFFLINE_DEPLOY.md](docs/OFFLINE_DEPLOY.md) — 离线构建与部署
- [release/README.md](release/README.md) — 现成包安装说明

## 仓库结构

```
TradosToolkit/          插件源码（SDK 风格 csproj，net48）
tools/                sign-sdlplugin.ps1（构建内自签）
third_party/nuget/    vendored NuGet 依赖（离线 restore）
nuget.config          本地源 + clear（不联网）
docs/                 API / 签名免弹窗 / 离线部署文档 + 图标源图
release/              每次构建自动刷新的签名 .sdlplugin（免构建直接分发）
references/           参考资料：Sdl-Community、Studio API 文档等（不参与构建）
```

## 已知边界

- 免签弹窗利用 Studio 2019 `ValidateSignatures` 的 fail-open 缺陷，**2021+ 版本未验证**。
- 推送远程仓库走 `http://127.0.0.1:7892` 代理，代理未启动时 `git push` 会失败。
