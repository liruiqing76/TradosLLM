# TradosToolkit 优化路线图（2026-09-20）

本文档记录功能盘点后的优化提案与"术语插件合并"可行性结论。第一、二节为计划项；第三节为 2026-09-20 第二轮盘点，其中 3 项已获用户确认立项（任务 #22/#23/#24）。

## 一、术语插件与翻译插件"合并"结论

**现状：已经是同一个插件。** 仓库只有一个工程 `TradosToolkit/TradosToolkit.csproj`，只产出一个 `TradosToolkit.sdlplugin`；术语库（`Glossaries/`：`GlossaryDb` SQLite 存储 + `SqliteGlossaryProvider` + `TermReplacer` + 管理界面）与翻译提供程序（`TranslationProvider/`）是同一程序集内的两个模块，清单反射器（PluginManifestGenerator）自动收集全部扩展点进同一份 manifest，无需任何合并动作。

**可升级方向（真正的增益）：把 SQLite 术语库注册为 Studio 原生术语解决方案 `ITermbaseProvider`，仍打包在同一个 .sdlplugin 内。**

| 维度 | 说明 |
|---|---|
| 收益 | 原生体验白送：编辑器双击查词、术语识别下划线（term recognition）、验证器术语检查都能直接查内网 SQLite 术语库，不再只在翻译流水线里生效 |
| 成本 | 约一个周末量级 |
| 风险 | Studio 2019（Studio15）的 `ITermbaseProvider` / `ITermbase` API 面必须先反射验证兼容性（2019~2026 差异未知） |
| 参考 | `references/Sdl-Community/Code samples/SampleTerminologyPlugin/MySampleTerminology`、`MultiTermTestPlugin`、`GetTermbaseDetails`；`references/studio-api-docs` 术语章节 |
| 先例 | 本仓库已有"清单反射器只扫描输出目录/GAC"打包 landmine 的应对经验（见 csproj `CopySdlRefsForManifestScan`），新扩展点注册方式沿用即可 |

## 二、体验优化提案（按顺畅度收益排序）

### ★★★-1 预翻译提速：级联 provider 段级并发 + 超时重试

- **痛点**：本地 LLM 网关稳定 ~7s/段，但偶发 70s+ 慢响应；长文档逐段串行预翻译是最大等待源。
- **方案**：`ToolkitTranslationProviderLanguageDirection` 批量请求路径内做 N 路并发（默认 3，可配置）；单段超时（默认 90s，可配置）后自动重试 1 次，仍失败则回落 TM 结果或保持空段并在日志记录；全程走 ToolkitLog 详尽日志。
- **注意点**：网关并发下的吞吐上限需实测（并发≠线性加速）；Studio 预翻译进度条对 provider 内部多线程的兼容性需验证；等待期 UI 须有可见状态与取消（已有反馈约定）。

### ★★★-2 编辑器 LLM 助手"连续润色"模式

- **痛点**：`EditorPanel` 侧栏每改一段要手动切段，逐段操作繁琐。
- **方案**：写回成功后提供"确认并跳下一未确认段"（Ctrl+Enter 快捷键）；可选开关"连续模式"：发送 → 写回 → 自动前进。
- **注意点**：2019 `Segment` 遍历有 GetEnumerator 未实现坑（已用 Count/索引器绕开，见 commit `73b81ce`），跳段逻辑沿用索引器方案。

### ★★☆-3 工作台"测试连接" + 配置导入/导出

- **痛点**：内网多台机器部署时，LLM 网关地址/密钥配置靠手拷 config.json；网关是否可达只能翻译一段才知道。
- **方案**：WorkbenchWindow 连接页签加"测试连接"按钮（发一条极短请求，显示实测延迟与错误）；加"导出配置/导入配置"（json 文件选择对话框）。
- **注意点**：遵守既有约定——配置 UI 一次输入即持久、按页签拆分、等待要有秒数提示+取消。

### ★★☆-4 术语对 LLM 提示词的强约束 + QA 术语检查

- **痛点**：`TermReplacer` 已做术语替换，但 LLM 路径的术语约束不够显性，LLM 可能绕开术语自造译法。
- **方案**：a) 级联 provider 组装 prompt 时注入当前段命中的术语对，附"必须使用以下术语译法"硬提示；b) `QaCheckBatchTask` 增加"译文未采用术语库译法"检查项，进 QA 报告。
- **依赖**：与 ★★★-2、★★-1 同文件域（TranslationProvider/BatchTasks），可合并一次改动窗口。

### ★☆☆-5 翻译中心书签：导入 Chrome bookmarks.html

- **痛点**：书签树目前逐条手动录入；验收对标 Chrome，导入是自然预期。
- **方案**：`TranslationCenterNavControl` 加"从 Chrome 导出文件导入"菜单项，解析 netscape 格式 bookmarks.html（纯本地解析，无新依赖），保留原目录层级。

### ★☆☆-6 插件内版本自更新提示

- **痛点**：内网分发靠拷 `.sdlplugin`，用户不知道有没有新版。
- **方案**：启动时比对内网共享目录（可配置 UNC 路径）中 `release/TradosToolkit.sdlplugin` 的版本号，有新版仅提示 + 一键打开共享目录，不自动覆盖（自动更新撞 Studio 文件锁，风险大于收益）。

## 三、2026-09-20 第二轮盘点（用户已勾选前 3 项）

跳出本节上一版清单，围绕既有资产（级联 provider / SQLite 术语库 / 本地 API 53902 / 工作台）的增量方向，用户 2026-09-20 已选定实施顺序：

1. **重复段缓存 + 全文一致性**（任务 #22）：批量路径"源文归一化→译文"文档级缓存，重复段只调一次网关且全文同译；可选持久磁盘缓存 `%APPDATA%\TradosToolkit\cache` 跨文档命中，config.json 开关。与 ★★★-1 并发互补（先缓存砍调用量，再并发压剩余延迟）。
2. **TM 记忆库工具套件**（任务 #23）：基于 FileBasedTranslationMemory 写 API——多库合并去重、冲突条目检测、扫描库一键添加到当前项目、新建空库；入口在工作台"记忆库"页。
3. **API 增强 + 状态面板**（任务 #24）：双语段列表导出（JSON/CSV）、任务进度查询端点、localhost:53902 极简 HTML 状态面板（进度 + 日志尾部 + TM/LLM/API 状态）。

**实施状态（2026-09-20）**：1/2/3 三项均已完成并进了同一轮构建部署。
1 = `09a1503`（SegmentDedup + LlmDiskCache，独立 A/B 16 断言）；2 = `c5792c8`（TmToolkit + 工作台四工具，真实 .sdltm A/B 18 断言）；3 = 本轮（BilingualParser 真实 .sdlxliff A/B 10 断言、`/api/project/segments`、`async=1` + `/api/task`+`/api/tasks`、`/api/requests`、`GET /` 状态面板 + 工作台底部链接入口，docs/API.md 同步）。

未选留候选：**选中即译 + 术语挖掘**（编辑器选中文本即时 LLM 翻译；LLM 从存量译文/ sdltm 挖候选术语对进待审列表）。

## 四、明确不做

- 一键预翻译按钮：Studio 原生已有预翻译（历史决策，见记忆"不做原生重复功能"）。
- 独立分发术语插件：所有能力继续收敛在单一 .sdlplugin。

## 六、2026-09-21 爆点功能（本轮全部实现并构建通过）

面向本地译员仓库的空战补位方向，6 项已实现（`HealthProbe.cs` + `ProjectApi.cs` 扩展 + `OpenAiCompatEngine` 术语强约束 + `QaCheckProcessor`）。docs/API.md 已同步新端点。缺口对标：译员日常的**质量抽查→术语→报价→自检→远程批量→术语积累**闭环。

1. **QA 批处理**（漏译/空译/数字/标点/术语未采用/重复段同译）：`BatchTasks/QaCheckProcessor.cs` + `QaCheckSettings` 五项开关，`QaCheckBatchTask` 挂 `AddBilingualProcessor`，进 QA 报告。
2. **术语强约束**：`OpenAiCompatEngine.TranslateOneAsync` 取当前段命中术语对进 prompt 第 5 条硬规则，译文未采用术语映射即换新 prompt 强制重译一次。
3. **报价/词数报告**：`GET /api/project/report` 聚合 `total`(词/句/字符 × 目标语言) 供报价，追加 `&format=csv` 下载。
4. **内网基线自检**：`GET /api/health` 对 API/TM/LLM/术语源逐项探测 ok+fail+latency，并行 5s 超时，部署到新内网机器一键验链路。
5. **远程预翻译调度**：`POST /api/project/pretranslate`，固定后台异步（TaskRegistry），立即返回 taskId + 进度查询。
6. **术语库自动回填**：`POST /api/glossary/backfill`，从已确认双语段统计共现反抽高频术语对写入 `kind=pre`，零 LLM 成本、确定性输出，结果供人工复核。

**质量基线（第 6 项）**：只取两侧出现 ≥2 次且共现覆盖度 ≥0.5 的稳定短语对；跳过纯数字与两侧相同（品牌/代号）项。写入后默认参与术语约束与 QA 术语检查，强建议管理界面复核。

## 八、2026-09-21 第二弹：翻译前分诊 + 一致性审计一键统一（本轮实现并构建通过）

跳出"参考项目功能平移"，基于现有**双语段解析 + 文件改写**资产产出的两个原创闭环，均纯本地、零新依赖：

7. **翻译前智能分诊** `GET /api/project/triage`：开工前扫全文，对每条源段分类 `skip`(空/纯数字符号/URL，省网关费) / `chunk`(超长待分块) / `highrisk`(括号引号不闭合、含替换字符疑似乱码) / `normal`，并统计重复出现≥3次的源段 → 产出一段"重活 vs 白花钱"总结。复用 BilingualParser，零 LLM。
8. **全文一致性审计 + 一键统一** `GET|POST /api/project/audit`：GET 把"同一源文多次出现却译得不一样"的分歧组列全（主译法 + 各译法命中段）；POST 把少数派改写为主流译文写入 `.sdlxliff`（先备份 `.bak`，含标签分歧组自动跳过列人工，`SaveOptions.DisableFormatting` 保留空白）。做预翻译后的事后兜底，与术语强约束互补。

> 未做：分诊/审计的图形界面，走 API（与 #3/#5 一致的数据驱动风格）；POST audit 就地改写仅限纯文本目标，标签场景留人工。

## 七、建议实施顺序

1. ★★★-1（预翻译并发）→ 单独构建部署验证一轮
2. ★★★-2（连续润色）→ 与 1 分开提交，便于回滚
3. ★★☆-3、★★☆-4 可并行
4. ★☆☆-5、★☆☆-6 视反馈穿插
5. ITermbaseProvider（第一节）单独立项：先做 API 反射验证脚本，再进实现

> 前置条件：2026-09-20 部署版本（commit `9ef24bd` 构建链）经用户重启 Studio 目视验收通过（无未注册弹窗、英文 UI、工作台图标、书签树导航）后再启动上述任何实现。
