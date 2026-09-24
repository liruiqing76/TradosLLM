# Changelog

所有重要功能变更记录。版本号对应 csproj `<Version>`。

## v2.1.0（2026-09-24）— 上下文窗口 / 术语挖掘 / 客户模板 + 7 项增强

### 新增核心功能（#2 / #3 / #5）

- **#5 滑动窗口上下文翻译**：级联引擎支持多段上下文窗口（`Queue<ContextPair>`），可配置窗口段数（`contextWindowSegments`，缺省 3）与字符上限（`contextMaxChars`，缺省 1200），跨批续接；风格指南（`styleGuide`）注入翻译 prompt + 编辑器润色面板
- **#2 LLM 术语挖掘 + 审批闭环**：`TermMiner` 从文档源文正则粗筛 → LLM 精筛 → `term_candidates` 审批队列 → 人工批准写正式术语库（`term_entries` + `terms` 译前替换），术语缓存代次失效
- **#3 客户项目模板**：每客户一个 JSON 模板（语言/领域/TM 路径/风格指南/后续步骤），工作台新增"客户模板"页签

### 7 项增强（本轮）

1. **术语挖掘批准可编辑弹窗**：单条批准时弹确认框，用户可修改 LLM 建议译法后再写库（`TermApprovalDialog`）
2. **后台任务取消**：`TaskRegistry.Cancel(id)` + `POST /api/task/cancel` + pipeline 步骤间取消检查 + 状态面板取消按钮
3. **术语库备份/恢复**：`GlossaryDb.Backup()` WAL checkpoint + File.Copy，保留最近 10 份；工作台记忆库页加"备份术语库"/"从备份恢复"按钮
4. **apiKey DPAPI 加密**：`config.json` 中 apiKey 以 `dpapi:` 前缀 + Base64 加密存储（当前用户范围），兼容旧版明文自动迁移
5. **状态面板修复提示**：磁贴下方加诊断与修复区块，对 fail 项给出可点击修复建议
6. **CHANGELOG + README 功能清单补全**
7. ~~挖掘结果入候选池~~（核实 `MineAsync` 已天然实现，取消）

### UI 新增

- 术语管理窗口第三页签「术语挖掘审批」（语言对 + 领域 + 源文输入 + DataGrid + 批准/拒绝）
- 工作台「客户模板」导航项 + TemplatesPanel（模板编辑/列表/套用/删除）

### API 新增端点

| 端点 | 方法 | 说明 |
|---|---|---|
| `/api/terms/mine` | POST | 从源文挖掘术语候选 |
| `/api/terms/pending` | GET | 列出待审候选 |
| `/api/terms/approve` | POST | 批准候选写正式术语库 |
| `/api/terms/reject` | POST | 驳回候选 |
| `/api/client-templates` | GET/POST | 客户模板列表/保存 |
| `/api/client-templates/delete` | POST | 删除客户模板 |
| `/api/task/cancel` | POST | 取消运行中的后台任务 |

## v2.0.0（2026-09-21）— 爆点功能 + 翻译前分诊 + 多步管线

### 新增功能

1. QA 批处理（漏译/空译/数字/标点/术语未采用/重复段同译）
2. 术语强约束（LLM prompt 注入命中术语 + 未采用自动重译）
3. 报价/词数报告（`GET /api/project/report`，支持 CSV 下载）
4. 内网基线自检（`GET /api/health`，API/TM/LLM/术语源逐项探测）
5. 远程预翻译调度（`POST /api/project/pretranslate`，后台异步 + 进度查询）
6. 术语库自动回填（`POST /api/glossary/backfill`，从已确认双语段统计共现反抽高频术语对）
7. 翻译前智能分诊（`GET /api/project/triage`，skip/chunk/highrisk/normal 分类 + 重复段统计）
8. 全文一致性审计 + 一键统一（`GET|POST /api/project/audit`，同一源文多译法分歧检测 + 少数派改写）
9. 跨文件一致性审计（`GET /api/project/audit?all=1`）
10. 批量写回译文（`POST /api/project/sdlxliff`，按段 id 写 target + .bak 备份）
11. 多步批处理管线（`POST /api/project/pipeline`，按序编排 + tolerant 开关 + 逐步进度）
12. 重复段缓存 + 全文一致性（`SegmentDedup` + `LlmDiskCache`，跨文档命中）
13. TM 记忆库工具套件（多库合并去重、冲突检测、扫描库一键添加到项目、新建空库）
14. API 增强 + 状态面板（双语段导出 JSON/CSV、任务进度查询、`localhost:53902` HTML 状态面板）
15. 回译校验（`POST /api/project/backtranslate`，LLM 回译源文 + 与原文差异报告）
16. 收件箱监控目录（`/api/inbox/*`，拖入即产出三件套）
17. 文件格式转换（`POST /api/convert`，任意 Studio 支持格式 → sdlxliff）

## v1.0.0（初始版本）

- 级联翻译提供程序（TM → LLM）
- 工作台 + 翻译中心浏览器
- 编辑器 LLM 助手
- SQLite 术语库（译前替换 + 完整术语模型 + 原生术语引擎集成）
- 本地 HTTP API（项目/段/任务/TM）
