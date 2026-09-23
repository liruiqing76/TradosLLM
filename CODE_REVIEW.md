# TradosToolkit 代码审查报告

## 一、代码健壮性

### 高风险

| 位置 | 问题 | 建议 |
|---|---|---|
| `TermHttpClient.cs:72` | HttpClient 无超时 + `CancellationToken.None`，术语服务无响应时请求**无限挂起**，阻塞 Studio 线程 | 设置 `Timeout=30s` 或用 `CancelAfter` |
| `EngineHttp.cs:20` | HttpClient 无默认超时，4参数重载传 `timeoutSeconds=0` 不超时，TM 请求可无限挂起 | 兜底 `Timeout=300s` |
| 14 处 `async void` | WPF 事件处理器未捕获异常会**崩溃 Studio 进程**（`WorkbenchWindow`/`TmManagerWindow`/`InboxWindow`/`LlmPanelView` 等） | 每个方法体包 try/catch + `ToolkitLog.Error` |
| `ToolkitApiServer.cs:50` | `HttpListener` 从未 Dispose，启动失败时端口残留 | 失败时 `_listener?.Dispose()`，提供 `Stop` 方法 |

### 中风险

| 位置 | 问题 |
|---|---|
| `ProjectApi.cs:2130` | `catch(Exception){}` 完全吞掉 JSON 解析异常，无日志 |
| `ToolkitApiServer.cs:74` | `ListenLoop` catch 后直接 break，服务静默死亡 |
| `OpenAiCompatEngine.cs:295` | 术语加载失败静默返回空列表，用户无感知 |
| `GlossaryDb.cs:36` | 无连接池，`EnsureSchema` 每次 new 都执行建表检查 |
| `InboxWatcher.cs:39` | `_cfg` 非 volatile，工作线程读取未加锁 |

### 做得好的方面
- 空引用防护完善，外部返回值多数有判空
- 配置边界校验严格（并发1-32、超时5-600、重试0-5）
- 敏感信息脱敏到位（apiKey/bearerToken 仅记长度或"有/无"）

---

## 二、日志完整性

### 框架缺陷

| 问题 | 影响 |
|---|---|
| **仅 Info/Error 两级**，缺 Debug/Warn | 无法分级过滤，生产环境无法降噪；可恢复异常只能用 Error |
| **三套日志割裂**：ToolkitLog(plugin.log) / ApiLog(api.log) / InboxJob(UI) | 排障需对照三个来源，跨组件问题难定位 |
| `Write` 为 private | 外部无法扩展级别或注入结构化字段 |

### 关键缺失

| 位置 | 缺失 |
|---|---|
| `TmApiEngine.cs` | **整个 TM 引擎零业务日志**，无开始/完成/命中数/耗时 |
| `InboxOrchestrator.cs` | 成功路径仅写 job.AppendLog，**不落盘 plugin.log** |
| `ToolkitApiServer.cs:83-121` | 正常 API 请求不入 plugin.log，重启即丢 |
| `SdlxliffConverter.cs` | 文件转换无开始/完成日志 |
| `ProjectApi.cs:36-89` | Handle 路由分发无日志，调用链路断裂 |

### 日志质量问题
- 多处用 Info 记录 Debug 级信息（ctor、Initialize）
- `CascadeEngine.cs:70` TM 失败回退用 Error，应为 Warn
- `EngineHttp.cs:57` 失败响应体截断 2000 字符可能含敏感信息
- `OpenAiCompatEngine.cs:190` 记录术语对明文，可能含商业机密

### 做得好的方面
- 268 处 ToolkitLog 调用，覆盖广
- HTTP 引擎层日志优秀（URL/状态码/耗时/失败/超时完整）
- 全局异常兜底：AppDomain + TaskScheduler 双挂钩

---

## 三、架构合理性

### 架构优点
1. **翻译引擎抽象到位**：`ITranslationEngine` 接口 + `CascadeEngine` 级联设计优雅，单/双引擎缺席都能降级
2. **入口自启容错**：每个服务 try-catch 兜底，单服务失败不阻断其余，`Interlocked.CompareExchange` 幂等启动
3. **收件箱编排清晰**：0-6 步线性流程，异常降级为步骤失败并跳过后续
4. **SDK 兼容专业**：SQLite 版本、WebView2Loader、自签证书等宿主环境细节处理到位
5. **单例用法克制**：无滥用单例做全局状态桶

### 架构问题

**🔴 循环依赖（严重）**

| 循环 | 位置 | 建议 |
|---|---|---|
| Inbox ↔ Server | `InboxOrchestrator.cs:10` + `Server/InboxApi.cs:5` | 抽出 `ProjectService` 纯业务层，两边都依赖它 |
| TranslationProvider.UI ↔ Workbench | `TmManagerWindow.xaml.cs:14` + `WorkbenchWindow.xaml.cs:19` | UI 间用事件/导航服务解耦 |

**🟠 反向依赖**

| 问题 | 位置 | 建议 |
|---|---|---|
| Common → Glossaries | `Common/Catalog/DomainCatalog.cs:2` | `DomainTree` 移入 Common，通用层不应依赖业务层 |
| Server → EditorPanel | `Server/StatusPage.cs:8` | API 层依赖 UI 层，用 StatusProvider 接口解耦 |
| Inbox → Workbench | `Inbox/InboxWindow.xaml.cs:15` | 编排层依赖 UI 层，用导航服务解耦 |

**🟠 其他问题**

| 问题 | 位置 | 建议 |
|---|---|---|
| ProjectApi 上帝类（2159 行，25+ 端点） | `Server/ProjectApi.cs` | 按职责拆分为 ProjectCommands/TaskCommands/PackageCommands 等 |
| 配置类平铺 30+ 字段无版本管理 | `ToolkitConfig.cs` | 按域分块 + configVersion + 类型安全反序列化 |
| CascadeEngine 硬编码引擎实现 | `CascadeEngine.cs:19` | 引入 EngineRegistry 工厂注册 |
| 构造函数做重活 | `TradosToolkitRibbonGroup.cs:18` | 用 Task.Run 异步启动 |

---

## 四、总结

| 维度 | 评级 | 关键问题 |
|---|---|---|
| 代码健壮性 | **中上** | HttpClient 无超时、async void 异常逃逸 |
| 日志完整性 | **中** | 缺 Debug/Warn 级别、三套日志割裂、TM 引擎零日志 |
| 架构合理性 | **中上（7/10）** | 2 处循环依赖、ProjectApi 上帝类 |

**优先修复项**：
1. `TermHttpClient`/`EngineHttp` 加超时 — 可致 Studio 挂死
2. 14 处 `async void` 补 try/catch — 未捕获异常崩溃 Studio
3. ToolkitLog 增加 Debug/Warn 级别 — 日志框架硬伤
4. 打破 Inbox↔Server 循环依赖 — 抽 ProjectService
5. `TmApiEngine` 补业务日志 — TM 查询不可观测