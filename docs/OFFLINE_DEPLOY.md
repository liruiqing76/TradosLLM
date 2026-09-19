# TradosToolkit 离线部署指南

> 适用：把插件装到**没有外网**的电脑（Studio 2019 / Studio15，x64 Windows）。
> 结论：**插件本体的安装与运行完全不需要网络**；唯一的外部依赖是"翻译中心"内置浏览器所用的
> Edge WebView2 系统运行时（Win11 必带，多数 Win10 也带；缺失只影响翻译中心浏览器，其余功能正常）。

## 一、依赖清单（对构建产物 TradosToolkit.dll 实测枚举，共 29 项引用）

### 1. Studio 自带，目标机必存在（无需随包分发）

| 程序集 | 版本 | 来源 |
|---|---|---|
| Sdl.Core.Globalization / Sdl.Core.PluginFramework / Sdl.Core.Settings | 1.8.0.0 | Studio 安装目录 |
| Sdl.Desktop.IntegrationApi / .Extensions | 15.0.0.0 | Studio 安装目录 |
| Sdl.FileTypeSupport.Framework.Core | 1.0.0.0 | Studio 安装目录 |
| Sdl.LanguagePlatform.Core / .TranslationMemory | 1.6.0.0 | Studio 安装目录 |
| Sdl.LanguagePlatform.TranslationMemoryApi | 15.0.0.0 | Studio 安装目录 |
| Sdl.ProjectAutomation.AutomaticTasks / .Core / .FileBased | 15.0.0.0 | Studio 安装目录 |
| Sdl.TranslationStudioAutomation.IntegrationApi | 15.0.0.0 | Studio 安装目录 |
| System.Data.SQLite | 1.0.103.0 | **故意用 Studio 自带的 1.0.103**（含其原生 SQLite.Interop），不随包分发，避免 native 版本冲突 |

### 2. .NET Framework 4.8 系统组件（能装 Studio 2019 的机器即满足）

mscorlib、System、System.Core、System.Data、System.Net.Http、System.Web.Extensions、
System.Windows.Forms、PresentationCore、PresentationFramework、WindowsBase、Xaml、WindowsFormsIntegration。

### 3. 第三方库——已全部打进 .sdlplugin，离线随包走

| 文件 | 大小 | 用途 |
|---|---|---|
| HandyControl.dll | 1.4MB | 界面控件主题 |
| Microsoft.Web.WebView2.Core.dll | 698KB | WebView2 托管层 |
| Microsoft.Web.WebView2.Wpf.dll | 86KB | WebView2 WPF 控件 |
| WebView2Loader.dll | 124KB | 原生加载器（运行时按绝对路径预载） |

整个 TradosToolkit.sdlplugin 约 **2.5MB**。

### 4. 唯一系统级外部依赖：Edge WebView2 Runtime（不在包内）

浏览器内核（约 150MB，微软作为系统组件分发），不能也不应塞进插件包。

- **Win11**：系统自带，无需处理。
- **Win10**：装有新版 Edge 的机器基本都带。
- **检查命令**（PowerShell，目标机执行）：

  ```powershell
  Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -Name pv
  ```

  版本号不是 `0.0.0.0` 即已安装。
- **缺失时的表现**：只有"翻译中心"浏览器区显示"内置浏览器不可用"提示；
  TM→LLM 级联提供程序、QA 批处理、HTTP API、编辑器 LLM 助手等全部照常。
- **离线补装**：在有网机器下载微软官方 *WebView2 Runtime* 独立安装包
  （Evergreen bootstrapper 或离线完整包，x64），拷到目标机安装一次即可。

## 二、离线安装步骤

1. 拷贝 `TradosToolkit.sdlplugin`（U 盘/内网共享均可，全程无网络）到目标机：

   ```
   %APPDATA%\SDL\SDL Trados Studio\15\Plugins\Packages\
   ```

2. 启动 Studio。首次会弹 **"Unsigned SDL Trados Studio Plug-in Found"**（未签名插件确认），点 **"是"**。
3. Studio 自动把包解压到同级 `Unpacked\TradosToolkit\`，无需手工干预。
4. 配置：插件设置窗口填一次（TM 地址 / LLM url / apiKey）即自动持久化到
   `%APPDATA%\TradosToolkit\config.json`，重启不丢。也可以直接把这台调试机的
   `config.json` 拷过去免输入。

### 开发机快速部署（非目标机）

把构建输出整套拷入 `Unpacked\TradosToolkit\`（Studio 必须**先退出**，
否则 `.plugin.resources` 被独占锁会造成"半新半旧"，甚至启动即 NRE）：

```
TradosToolkit.dll
Plugins\TradosToolkit.plugin.xml
Plugins\TradosToolkit.plugin.resources
HandyControl.dll
Microsoft.Web.WebView2.Core.dll
Microsoft.Web.WebView2.Wpf.dll
WebView2Loader.dll        ← 必须平铺在 Unpacked 根，不是 runtimes\ 子目录
```

## 三、离线环境下的功能可用性

| 功能 | 离线可用 | 说明 |
|---|---|---|
| TM→LLM 级联翻译提供程序 | ✅ | 连局域网/本机 TM 服务与本地 LLM 网关（如 127.0.0.1:8765）即可，不出外网 |
| QA 批处理 | ✅ | 纯本地 |
| Studio 内嵌 HTTP API（53902） | ✅ | 仅监听本机 |
| 编辑器 LLM 更正助手 | ✅ | 走本地网关 |
| 翻译中心内置浏览器 | ✅* | *需系统装有 WebView2 Runtime（见上），打开的站点本身是否可达另计 |
| 书签树（目录/收藏/导出 HTML） | ✅ | 存 config.json；导出文件可拷去 Chrome/Edge 导入 |

## 四、版本要求汇总

- Studio 2019（15.x，x64）——manifest `RequiredProduct name="SDLTradosStudio" 15.0–19.9`
- .NET Framework 4.8（运行时）
- Windows 10 1803+ / Windows 11
- （仅翻译中心浏览器）Edge WebView2 Runtime Evergreen

## 五、离线构建（依赖包已随仓库分发）

构建期 NuGet 依赖已全部 vendor 进仓库 `third_party/nuget/`（4 个 .nupkg，约 18MB）：

| 包 | 版本 | 用途 |
|---|---|---|
| Sdl.Core.PluginFramework | 2.1.0 | 扩展特性/控制器基类（编译引用；运行时用 Studio 自带） |
| Sdl.Core.PluginFramework.Build | 18.0.1 | 清单生成 + ResGen 资源编译 + .sdlplugin 打包任务 |
| HandyControl | 3.5.1 | WPF 皮肤（打进包内） |
| Microsoft.Web.WebView2 | 1.0.4191.47 | 翻译中心浏览器托管层（Core/Wpf 打进包内） |

仓库根 `nuget.config` 已 `<clear/>` 包源并只指向 `third_party/nuget`——无网机器 clone 后直接：

```
MSBuild.exe TradosToolkit/TradosToolkit.csproj -t:Rebuild -p:Configuration=Release
```

已实测：全新空缓存（RestorePackagesPath 指向空目录）下 restore+构建全绿。

注意两点：
1. 构建机仍需**本机安装 Studio 2019**——13 个 `Sdl.*` 引用与 System.Data.SQLite 走 `$(TradosInstallDir)` HintPath（`C:\Program Files (x86)\SDL\SDL Trados Studio\Studio15`），这些是 Studio 安装件、不进仓库；ResGen.exe 需 .NET Framework SDK/Win10 SDK 注册表可寻。
2. 以后新增 PackageReference 时，须把该包**及其传递依赖**的 .nupkg 一并放进 `third_party/nuget/`，否则离线 restore 会报错（这是 `<clear/>` 的预期行为，防止悄悄引入未 vendor 的依赖）。
