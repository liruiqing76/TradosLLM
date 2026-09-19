# Trados Studio 插件开发计划

## 目标
开发一个兼容 **Trados 2019 ~ 2026** 的多功能插件，发布到 RWS AppStore 收费。

## 核心功能（三大块）
1. **翻译提供程序（MT/TM 接入）** — 接入外部机器翻译服务
2. **批处理/QA 检查任务** — 术语一致性、漏译检测等
3. **一键预翻译/自动化任务** — Autocompletetask + ProjectAutomation API

## 技术栈
- C# + .NET Framework 4.8
- Visual Studio 2022 Community
- Trados SDK（引用最低版本 Studio15 DLL，向上兼容）
- NuGet: Sdl.Core.PluginFramework (2.1.0) + Sdl.Core.PluginFramework.Build (18.0.1)

## 多版本兼容策略
```
一套代码
  ├─ x86 编译 → 2019/2021/2022/2024 (Studio15~18, 32位)
  └─ x64 编译 → 2026 (Studio19, 64位)
```
- 编译期引用最低版本 SDK（Studio15）
- 只用 2019 就有的旧 API
- manifest RequiredProduct: minversion=15.0 maxversion=19.9

## 关键路径（模板架构）
```
pluginpackage.manifest.xml  ← 打包清单(必带, RequiredProduct版本范围)
PluginProperties.cs         ← [Plugin] Attribute, 标识插件
PluginResources.resx        ← 本地化字符串(插件名等)
*.cs                        ← 功能实现
  ├─ ITranslationProviderFactory  → MT/TM 提供程序
  ├─ AbstractFileContentProcessingAutomaticTask → 批处理任务
  └─ IActionExtension → Ribbon 按钮/一键操作
```

## 关键注意点
- **部署路径**: 2019 是 `%AppData%\SDL\SDL Trados Studio\15\Plugins\Packages\`（老 SDL 路径）; 2021+ 才是 `%AppData%\Trados\Trados Studio\{版本}\Plugins\`
- **SDK DLL**: C:\Program Files\Trados\Trados Studio\Studio{版本}\*.dll
- **强命名**: 插件程序集必须强命名签名(SdlCommunity.snk)
- **2026 术语库 API 变更**: SDLTB→ttb.file:///, 用 TerminologyProviderManager
- **第三方依赖打包**: 写进 pluginpackage.manifest.xml 的 `<Include><File>` (相对输出目录), 复制 local 不会自动入包; SQLite 需带 x86/x64 Interop
- **构建命令**: `"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" TradosToolkit.sln -restore -p:Configuration=Release`

## 发布流程
1. 申请 RWS 开发者许可 (app-signing@rws.com)
2. 拿签名密钥 + 开发者执照
3. .sdlplugin 签名 → AppStore 上架

## 里程碑
- [ ] M1: VS2022 安装完成
- [ ] M2: Trados Studio 授权到位(公司2019 或 RWS开发者许可)
- [x] M3: 插件项目骨架搭起, 空壳能编译出 .sdlplugin
- [ ] M4: 翻译提供程序功能（基本链路已通，待 Studio 实测）
- [ ] M5: 批处理/QA 功能
- [ ] M6: 一键自动化功能
- [ ] M7: 多版本打包 + 签名 + AppStore 发布

## 本地 HTTP API（agent 控制入口）
完整逐端点文档见 [docs/API.md](docs/API.md)。
Studio 启动即随 Ribbon 组自启 HttpListener，仅监听 `http://localhost:53902/`。
- 令牌鉴权: `%AppData%\TradosToolkit\api.token`，请求头 `X-Api-Key`（或 `?key=`）；`/api/status` 免鉴权
- 配置: `%AppData%\TradosToolkit\api.json` `{"enabled":true,"port":53902}`；日志 `api.log`
- 端点:
  - `GET /api/status` | `GET /api/templates` | `GET /api/projects`
  - `POST /api/projects` body `{name,folder,sourceLang,targetLangs[],files[],template?,openInStudio?}` → 建 .sdlp
  - `GET /api/project/files?path=xx.sdlp`
  - `POST /api/project/task?path=&task=pretranslate|analyze|wordcount|target|export|updatetm&files=id,`，body 可带 `{providerUri,providerState}`（预翻译前写入项目各目标语言 TM 配置）
  - `GET /api/project/report?path=` → 分析词数统计 JSON
  - `GET /api/project/tmfiles?path=` → 项目目录下的 *.sdltm 列表
  - `POST /api/project/package?path=` → 生成 .sdlppx（含主 TM）
  - `GET /api/file?path=` → 下载文件（令牌即门槛，仅限本机）
- 实现基础: `Sdl.ProjectAutomation.FileBased.FileBasedProject`（本机 2019 15.0.1 **没有** Sdl.ProjectAutomation.FileApi.dll，FileBased 是唯一无头路径）；Studio 对象模型调用全部 marshal 回 UI Dispatcher
