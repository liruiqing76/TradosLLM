# TradosToolkit 优化路线图（2026-09-20 评审稿，未实施）

本文档记录一次功能盘点后的优化提案与"术语插件合并"可行性结论。**均为计划项，尚未实现**。

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

## 三、明确不做

- 一键预翻译按钮：Studio 原生已有预翻译（历史决策，见记忆"不做原生重复功能"）。
- 独立分发术语插件：所有能力继续收敛在单一 .sdlplugin。

## 四、建议实施顺序

1. ★★★-1（预翻译并发）→ 单独构建部署验证一轮
2. ★★★-2（连续润色）→ 与 1 分开提交，便于回滚
3. ★★☆-3、★★☆-4 可并行
4. ★☆☆-5、★☆☆-6 视反馈穿插
5. ITermbaseProvider（第一节）单独立项：先做 API 反射验证脚本，再进实现

> 前置条件：2026-09-20 部署版本（commit `9ef24bd` 构建链）经用户重启 Studio 目视验收通过（无未注册弹窗、英文 UI、工作台图标、书签树导航）后再启动上述任何实现。
