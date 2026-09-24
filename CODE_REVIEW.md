# TradosToolkit 代码审查报告

> 审查日期：2026-09-24　范围：`TradosToolkit/` 全部 **97 个 .cs 文件、约 22,046 行**
> 方法：按模块分 4 组并行审查 + 人工抽查复核（标注 ✅ 的条目已逐行确认）

---

## 一、总体结论

| 维度 | 评级 | 说明 |
|---|---|---|
| 功能完整性 | 好 | 翻译提供程序/术语源/QA/收件箱/工作台各链路完整，降级设计到位 |
| 安全 | 中上 | SQL 全参数化、apiKey 走 DPAPI 加密落盘、日志不记明文；但本地 API 的路径参数缺乏沙箱 |
| 线程模型 | **中下** | 大量长耗时逻辑跑在 Studio UI 线程（`WithProject → OnUi → Dispatcher.Invoke`）；后台线程访问 UI 对象 |
| 资源清理 | **中下** | WebView2 / HttpListener / CTS / TM 句柄多处未释放 |
| 并发正确性 | 中 | 缓存与状态字段多处无锁、无 volatile |
| 一致性 | 中 | QA 批处理与原生验证器、术语缓存失效路径、写回状态字段等存在分叉 |

**高危 13 项、中危约 30 项、低危/改进约 20 项。**

---

## 二、高危问题（建议优先修）

### A. 长耗时逻辑占用 Studio UI 线程（会导致宿主假死）

1. ✅ **[高] `Server/ProjectApi.cs:938-946`（打包）**
   `CreateProjectPackage` 后用 `while { Thread.Sleep(200); }` 轮询，最长 180s；而该 handler 经 `WithProject`(1769) → `OnUi`(1787) → `Dispatcher.Invoke` 跑在 **Studio UI 线程**。
   → 打包期间 Studio 界面完全冻结（表现为假死）。
   **修法**：轮询移出 UI 线程（`Task.Run` + 异步等待），UI 线程只负责发起。

2. **[高] `Server/BackTranslateQa.cs:107-121` + `Server/ProjectApi.cs:1787-1793`**
   整个 handler（含全部回译/判官 LLM 调用，`BackTranslateQa.cs:145/177`）被包进 `Dispatcher.Invoke`。
   → 回译质检期间 Studio UI 全程冻结；且与 `/api/review`（在 UI 线程外调 LLM）行为不一致。
   **修法**：只在 UI 线程解析双语路径，LLM 调用移出 UI 线程。

3. **[高] `Workbench/WorkbenchWindow.xaml.cs:1140,1154`**
   `await Task.Run(() => RunStudioPipeline(seq))` 在线程池线程执行，而 `RunStudioPipeline`(1090) 调 `NeedPath()`(1207) 读 `ToolsProjBox.Text`、`BaseQuery()`(1215) 读 `ToolsFileBox.Text` —— 都是 WPF 控件。
   → 后台线程访问 UI 对象抛 `InvalidOperationException`，**流程页里含 Studio 自动任务的流程必然失败**。
   **修法**：进入 `Task.Run` 前在 UI 线程取出所需字符串再传参。

4. **[高] `Workbench/WorkbenchWindow.xaml.cs:106-111`**
   `RunOnStudioUi` 用阻塞式 `app.Dispatcher.Invoke`，而调用方是专用 STA 线程。
   → 宿主线程正跑自动任务时工作台整体冻结；若宿主反向等待该窗口则死锁。
   **修法**：改 `InvokeAsync` + `await`，或加超时。

5. **[中] `Workbench/WorkbenchWindow.xaml.cs:99`、`Convert/ConvertWindow.xaml.cs:150`**
   `ShowOrActivate` 在**宿主 UI 线程**上 `ready.WaitOne(15s)`，超时静默返回、`_instance` 无同步。
   → 窗口建失败时宿主卡最多 15s，且用户点了没反应、无提示。

### B. 数据正确性

6. **[高] `Server/ProjectApi.cs:1376,1420-1441`（ApplyUniform）**
   内联标签守卫 `needsManual` 基于 `BilingualParser` 解析后的 Target —— 标签已被剥离、永不含 `<`，判定恒为 false。
   → `RemoveNodes()+Add(XText)` 会把带 `<g>/<ph>` 占位符的目标段整段覆盖，**占位符丢失、破坏句段**。
   **修法**：用原始 XML 的标签检测（同 `WriteSdlxliff` 的 `IsStructuralTag`）再决定是否改写。

7. **[中] `Server/ProjectApi.cs:1538`（WriteSdlxliff）**
   段状态写到 `trans-unit/@conf`，而 `BilingualParser.cs:44` 与 Studio 读的是 `sdl:seg/@conf`。
   → 写回的确认状态实际不生效、回读不到。

8. **[高] `TranslationProvider/UI/TermDialog.xaml.cs:82`**
   编辑术语时用 `TermStatus.Normalize` 归一"词性"，任何词性都被写成 `preferred`，下拉匹配不到即回落到"（无）"。
   → **改一次术语就静默清空词性**。
   **修法**：改用 `PartOfSpeech` 自身值匹配。

9. **[高] `TranslationProvider/UI/GlossaryManagerWindow.xaml.cs:615`**
   该窗口 `_provider` 恒为 null（`ShowOrActivate`/`ProviderConfigWindow` 都没传），`NotifyChanged` 空转，且从不调用 `OpenAiCompatEngine.InvalidateTermCache`。
   → 术语增删改后 `SqliteGlossaryProvider._cache` 与 LLM `_termCache` 不失效，**须重启 Studio 才生效**。
   **修法**：改动后调 `InvalidateTermCache()`，并让窗口持有真实 provider。

10. **[高] `TranslationProvider/Engines/LlmDiskCache.cs:28`**
    缓存键 `KeyFor` 只含 `baseUrl|model|源语|目标语|源文`，**不含领域**。
    → 切换领域后复用旧领域译文，术语/风格一致性被破坏。
    **修法**：把 `domain`（及 `styleGuide` 等影响输出的配置）纳入键。

11. ✅ **[高] `ToolkitConfig.cs:378-410`（Save）**
    读-改-写整个 JSON 且用非原子 `File.WriteAllText`，与并发 `Load`（收件箱工作线程/引擎线程）竞态。
    → 可读到半截 JSON，被 catch 后**静默回落全默认值**，配置瞬时丢失。
    **修法**：加锁 + 走 `FileKit` 的原子写（临时文件 + 替换）。

### C. 重试与降级逻辑

12. ✅ **[高] `TranslationProvider/Engines/OpenAiCompatEngine.cs:236`（IsTransient）**
    `return digit == '5' || digit == '4' || digit == '3';` —— 4xx/3xx 一律判可重试，与"4xx 直接失败"的注释相反。
    → 401/403/400 被退避重试，认证错误反复打网关。
    **修法**：仅 5xx、408、429 与网络异常可重试。

13. **[高] `TranslationProvider/Engines/OpenAiCompatEngine.cs:199`**
    术语未采纳时用 `continue` 消耗重试次数；`attempts=1`（retryCount=0）时循环退出且 `last == null`，抛异常**丢弃已取得的译文**。
    **修法**：校验失败单独计数，或直接返回 content。

### D. 资源与生命周期

14. **[高] `Workbench/TranslationCenterBrowserControl.xaml.cs:27-67` + `TranslationCenterController.cs:29-40`**
    `CoreWebView2Environment`/WebView2 控件从不 `Dispose`，三个事件也不解绑。
    → 浏览器进程与用户数据目录句柄常驻至 Studio 退出。
    **修法**：挂 `Unloaded`/实现 `Dispose`，释放并解绑。

15. **[高] `Inbox/InboxWatcher.cs:130,94`**
    `Stop()` 只 `Interrupt` 不 `Join`；旧 worker 可能仍在跑 `InboxOrchestrator` 时 `Start()` 又起新 worker。
    → 两个编排线程并发操作 Studio，破坏"必须串行"的不变量。
    **修法**：加"停止中"标志并 `Join`。

16. **[中] `Server/ToolkitApiServer.cs:69`（Stop）**
    `Stop()` 无任何调用点。
    → 插件卸载后 HttpListener 与端口/handle 不释放，直至进程退出。
    **修法**：在 Ribbon/插件卸载时机调用。

---

## 三、中危问题

**并发 / 可见性**
- `Server/TaskRegistry.cs:84-97` —— worker 线程无锁、非 volatile 写 `Status/Error/FinishedAt/Result`，HTTP 线程在锁内读，无可见性保证（可能读到过期 running / 空结果）。
- `Glossaries/SqliteGlossaryProvider.cs:22` —— `_cache` 无锁、无上限，被多个 LanguageDirection 并发读写（Dictionary 可能损坏 + 无限增长）。
- `TranslationProvider/ToolkitTranslationProvider.cs:38` —— `_directions` 读写无锁。
- `Inbox/InboxWatcher.cs:212` —— `_cfg` 非 volatile 而工作线程无锁读（`_outputRoot` 却是 volatile）。

**资源泄漏 / 清理**
- `Workbench/WorkbenchWindow.xaml.cs:612,629` —— `_memOpCts` 先创建再判重入，被拒时旧 CTS 变孤儿（不释放、取消指向错实例）。
- `TranslationProvider/UI/TmManagerWindow.xaml.cs:129` —— `_cts` 反复 Cancel 未 Dispose。
- `EditorPanel/LlmPanelView.xaml.cs:248-312` —— `RunBackTranslateQa` 的 `_cts` 从不 Dispose。
- `Inbox/InboxOrchestrator.cs:364-390` —— `ExportMatchedTm` 的两个 `FileBasedTranslationMemory` 未释放（.sdltm 句柄泄漏、产出库被锁）。
- `Inbox/InboxWatcher.cs:170-214` —— `_overrides` 在被拒/超时/文件消失时永久残留（内存泄漏 + 后续同路径误用旧覆盖）。

**数据一致性 / 存储**
- `Glossaries/GlossaryDb.cs:906`（Restore）—— 直接 `File.Copy` 覆盖主库，未关其他连接、未清 `-wal/-shm`，翻译进行中恢复易致库损坏。
- `Glossaries/TermMiningDb.cs:148` —— `UpsertCandidate` 在 DataReader 未关闭时于同一连接执行嵌套 UPDATE（易 `database is locked`）。
- `TranslationMemories/LocalTmIndex.cs:130,275,317` —— root 未规范化（`Upsert` 用 `GetDirectoryName`，其余用原样输入），同一库写出两套 root → 查询漏命中且孤儿行永不清理。
- `TranslationMemories/LocalTmIndex.cs:144-217` —— 增量扫描整段持 `Gate` 且内含逐个打开 `.sdltm`，期间查询/写入全阻塞，收件箱任务可卡数分钟。
- `TranslationMemories/TmToolkit.cs:135-167`、`TmImporter.cs:58-97` —— 合并/导入直接写目标库、无 `.bak`，中途失败留半写入库（与写回先备份的做法不一致）。

**安全 / 路径**
- `Inbox/InboxWatcher.cs:233-265` —— Accept/Enqueue 不校验路径位于监视目录内，而 InboxApi 可投任意绝对路径 → 任意本机文件被 Move 进任务目录并解析。
- `Server/ProjectApi.cs:813,918,962` —— `out`/下载 `path` 接受任意绝对路径，可读写项目目录之外（需令牌 + 仅 localhost）。
- `Server/ProjectApi.cs:48-49` —— `/api/task/cancel` 与 `/` 同层豁免令牌却改状态，本机任意进程可取消任务。

**错误处理**
- `TranslationProvider/UI/GlossaryManagerWindow.xaml.cs:518` —— `ReloadGlossary` 未包 try/catch（`ReloadRepl`/`ReloadMine` 都有）。
- `Workbench/WorkbenchWindow.xaml.cs:1562` —— `RevExport_Click` 的 `File.WriteAllText` 未包 try/catch。
- `Action/TradosToolkitRibbonGroup.cs:18-26` —— 构造函数内 `EnsureStarted/AutoStart/TmIndexScheduler.Start` 无 try/catch，任一异常使整个 Ribbon 组构造失败。

**行为不一致**
- `BatchTasks/QaCheckProcessor.cs:75-89` vs `Verification/ToolkitQaVerifier.cs:114-119` —— 空译文时验证器跳过数字/标点，批处理在"漏译检查"关闭时仍跑 → 同段两处结论不同，空译段刷"数字不一致"。
- `Verification/ToolkitQaVerifier.cs:100` —— `checkTerm = s != null && s.CheckTermAdoption`，设置不可用时术语检查**静默关闭**，而其余项默认开启。
- `Action/NewFromTemplateAction.cs:118-119` —— 模板匹配硬编码 `"en-"`，与 `SdlxliffConverter.cs:157` 的 `sourceLang+"-"+targetLang` 不一致，可能挂上语言对不符的模板。
- `Server/InboxApi.cs:174-190,257-288` —— 文件被拒收/就绪超时时 `JobCreated` 不触发，预登记的 rec 永远停留 `queued`、`PendingByPath` 永不清理 → 幽灵任务且阻塞 Trim。

**取消语义**
- `Workbench/WorkbenchWindow.xaml.cs:1377-1428` —— `_revCts` 令牌从未传给 `ProjectApi.Handle`，取消要等 LLM 批式跑完才生效。
- `Workbench/WorkbenchWindow.xaml.cs:1041-1197` —— 取消仅在步骤之间检查，正在执行的 pipeline/插件调用无 token，长任务无法中断。

---

## 四、低危 / 改进项

- `Server/InboxApi.cs:39-48` —— `_inited=true` 先于订阅，订阅抛异常后永不重试；静态事件订阅从不退订。
- `Server/ToolkitApiServer.cs:40-44` —— 配置禁用时提前 return 但 `_started` 已置 1，无法再启用（需重启 Studio）。
- `Server/ProjectApi.cs:278,444` —— `new GlossaryDb()` 创建后未使用（构造即开库/建表），多余开销。
- `TranslationProvider/Engines/LlmDiskCache.cs:60` —— 淘汰后日志恒打印"淘汰最旧 0 条"，且 Dictionary 无插入序，"最旧"不成立。
- `TranslationProvider/UI/GlossaryManagerWindow.xaml.cs:432` —— 保存替换词条用当前页签 `Domain`，忽略 `dlg.Result.Domain`。
- `Glossaries/TermReplacer.cs:30` —— 纯 Ordinal 大小写敏感替换且无词边界，与 LLM 侧"大小写不敏感命中"口径不一致。
- `Workbench/WorkbenchWindow.xaml.cs:141` —— `NavToolsText` 等硬编码中文，其余页走 `UiText`；`:1566` CSV 未转义；`:488` explorer 参数直接拼路径。
- `Workbench/ClientProjectTemplate.cs:50,146` —— 静态 `JavaScriptSerializer` 被 UI 线程与 HTTP 线程共享；`WriteAllText` 非原子；名称安全化使"客户A"与"客户/A"落到同一文件互相覆盖。
- `TranslationMemories/TmIndexScheduler.cs:21,36` —— 无 `Stop()`、`_running` 永不复位、`_lastRun` 跨线程读写未同步。
- `TranslationMemories/LocalTmIndex.cs:271` / `LocalTmScanner.cs:53` —— 空/不存在目录会抛 `ArgumentNullException`，与 `GetOrScan` 的容错不一致。
- `EditorPanel/LlmPanelView.xaml.cs:210,216` —— `ShowSegment` 清空 `_history` 后，等待中请求的取消/异常分支仍 `RemoveAt(Count-1)` → 每次换段抛 `ArgumentOutOfRangeException`（被外层吞掉并记错日志）。
- `Verification/QaRules.cs:40-49` —— `GetPlainText` 用 `foreach` 枚举容器，而编辑器侧代码注明 Studio 2019 的 `GetEnumerator` 抛 `NotImplemented`，建议实测后改索引器。
- `Convert/SdlxliffConverter.cs:124-137` —— `keepProject=false` 时 `Directory.Delete` 失败仅记日志，临时项目在 `%TEMP%\TradosToolkit\convert` 无限累积。
- `TranslationMemories/LocalTmIndex.cs:195-204` —— 索引仅存 `readable` 布尔，`ReadOne` 的 Error 被折叠成 Protected，打不开的库显示为"受保护"。
- `Inbox/InboxOrchestrator.cs:104` —— worker 线程读经 `Post` 异步回写的 `Steps[i].Status`，已完成步骤偶被误标"未执行"。

---

## 五、做得好的方面

- **安全基线扎实**：SQL 全参数化；apiKey 走 DPAPI 加密落盘；日志只记密钥长度不记明文；HTTP 客户端均已设超时。
- **降级设计**：级联引擎单/双引擎缺席都能降级；收件箱步骤失败即跳过后续而不中断；术语加载失败降级为空。
- **QA 规则已收口**：`QaRules` 成为批任务与原生验证器的共同实现，消除了重复代码。
- **宿主兼容处理专业**：SQLite 版本、WebView2Loader、自签证书、专用 STA 线程解决宿主消息泵 `WM_CHAR` 丢失等细节都有注释与依据。
- **日志分级与异常兜底**：`ToolkitLog` 已有 Debug/Warn/Info/Error 四级，AppDomain + TaskScheduler 双挂钩；多处 `async void` 已补 try/catch。

---

## 六、建议修复顺序

1. **把长耗时 handler 移出 Studio UI 线程**（第 1、2、4 条）—— 直接影响可用性，用户可感知为"Studio 卡死"。
2. **修后台线程访问 UI 对象**（第 3 条）—— 流程页 Studio 步骤必失败。
3. **数据正确性**（第 6、8、9、10 条）—— 占位符丢失、词性丢失、术语缓存不失效、领域串味。
4. **`IsTransient` 重试口径**（第 12 条）—— 认证错误反复打网关。
5. **资源释放**（第 14、15、16 条 + 中危泄漏项）—— WebView2 / HttpListener / 编排线程串行化。
6. **`ToolkitConfig.Save` 原子化**（第 11 条）—— 配置偶发丢失。
7. 其余中低危按模块顺手清理。
