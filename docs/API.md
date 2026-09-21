# TradosToolkit 本地 HTTP API 文档

Studio 打开并加载插件后自动启动，仅供本机 agent（如 Claude Code）驱动 Trados 2019。
Studio 关闭即服务消失，属设计行为。

- 基址：`http://localhost:53902`（端口改 `%APPDATA%\TradosToolkit\api.json` 的 `port`，`enabled:false` 可整体关闭）
- 鉴权：除 `/api/status` 与状态面板 `/` 外均需请求头 `X-Api-Key: <令牌>`，或查询参数 `?key=<令牌>`
  - 令牌文件：`%APPDATA%\TradosToolkit\api.token`（首次启动自动生成）
- 日志：`%APPDATA%\TradosToolkit\api.log`
- 编码：请求/响应均为 UTF-8 JSON（`/api/file` 除外，返回字节流）

## GET / （状态面板，免密钥）

浏览器打开 `http://localhost:53902/`：极简浅色 HTML 面板，5 秒自动刷新——
API/TM/LLM/去重/缓存状态磁贴、后台任务、最近请求轨迹、项目列表、`plugin.log` 尾部 30 行。
工作台窗口底部 `localhost:53902` 链接点击即打开。只读且不含令牌等敏感值。

## GET /api/status

探活，**免密钥**。

```bash
curl -s http://localhost:53902/api/status
```

```json
{ "product": "TradosToolkit", "studio": "x64", "version": "1.0.0.0",
  "port": 53902, "startedAt": "2026-09-19T10:00:00", "listening": true,
  "uptimeSeconds": 3600, "totalRequests": 42, "runningTasks": 0 }
```

## GET /api/templates

本机 Studio 已安装的项目模板列表。

```json
[ { "name": "Basic - zh-CN-ru-RU", "uri": "C:\\...\\Basic.sdlpt" } ]
```

## GET /api/projects

当前在 Studio 中打开的项目列表。

```json
[ { "id": "…", "name": "Project1", "projectPath": "D:\\…\\Project1.sdlp",
    "folder": "D:\\…", "sourceLang": "zh-CN", "targetLangs": ["ru-RU"], "isCompleted": false } ]
```

## POST /api/projects

创建基于文件的项目（`.sdlp`）。必填 `name folder sourceLang targetLangs[] files[]`。

```json
{ "name": "demo", "folder": "D:\\trados\\demo", "sourceLang": "zh-CN",
  "targetLangs": ["ru-RU"], "files": ["D:\\in\\a.docx"],
  "template": "D:\\tpl.sdlpt", "openInStudio": true, "description": "…" }
```

- `template` 缺省时自动挑名字含 `zh-CN-ru-RU` 的模板，再不行用第一个
- `openInStudio:true` 会把项目加入 Studio 项目视图
- 源文件自动设为 Translatable 角色

→ `200 { "projectPath": "D:\\…\\demo.sdlp", "id": "…" }`

## GET /api/project/files?path=xx.sdlp

列项目文件。

```json
{ "source": [ { "id": "…", "name": "a.docx", "role": "Source", "isSource": true,
                "language": "zh-CN", "path": "…", "bilingualPath": null } ],
  "target": [ { "id": "…", "language": "ru-RU", "role": "Translatable",
                "bilingualPath": "D:\\…\\a.docx.sdlxliff", "…": "…" } ] }
```

`target[].id` 即 task 端点 `files=` 参数用的文件 id。

## POST /api/project/task?path=…&task=…&files=…&async=…

执行自动化任务。缺省同步（返回即完成）；`async=1` 立即回 `202 { "taskId": "…", "status": "running" }`，
完成/失败状态用 `GET /api/task?id=` 查询（长任务 pretranslate/analyze 推荐异步，避免 HTTP 挂等）。

- `task`（必填）：`pretranslate` | `analyze` | `wordcount` | `target`（目标翻译）| `export`（翻译导出）| `updatetm`（批量任务，写主 TM）
- `files`（可选）：逗号分隔的文件 id，缺省 = 全部目标文件
- body（可选）：`{ "providerUri": "tradostoolkit://…", "providerState": "" }`
  —— 跑任务前把该翻译提供程序写入项目**所有目标语言**的 TM 配置（级联、结果找到即停）。
  用它可以免手工在 Studio 项目设置里加提供程序；URI 参数见插件配置（`%APPDATA%\TradosToolkit\config.json` 的 tmUrl/llmBaseUrl 等）。

```bash
curl -s -X POST "http://localhost:53902/api/project/task?path=D:\\demo.sdlp&task=pretranslate" \
  -H "X-Api-Key: $(cat ~/AppData/Roaming/TradosToolkit/api.token)" \
  -H "Content-Type: application/json" \
  -d '{"providerUri":"tradostoolkit://..."}'
```

→ `200 { "task": "pretranslate", "messages": ["…"], "reports": [{ "id": "…", "name": "…" }] }`

## GET /api/task?id=… 与 GET /api/tasks

异步任务查询。`/api/task?id=` 单任务详情（完成后带 `result` 原文），`/api/tasks` 最近 50 条列表（新→旧）。

```json
{ "id": "1a2b3c4d5e6f", "task": "pretranslate", "projectPath": "D:\\demo.sdlp",
  "status": "running|done|error", "startedAt": "2026-09-20T22:00:00",
  "finishedAt": "2026-09-20T22:01:30", "elapsedMs": 90000, "result": {…} }
```

## GET /api/project/segments?path=…&file=…&format=json|csv

按双语参照文件（`.sdlxliff`）导出段级双语表。`file` 缺省 = 唯一目标文件，多目标文件时必填（文件 id 或文件名）。
每行：`id / status(conf) / origin / percent / source / target`；`translate="no"` 结构段自动跳过。

```json
{ "project": "demo", "file": "a.docx", "fileId": "…", "language": "ru-RU",
  "bilingualPath": "D:\\…\\a.docx.sdlxliff", "count": 100,
  "segments": [ { "Id": "…", "Status": "Translated", "Origin": "tm", "Percent": "100",
                  "Source": "Getting Started", "Target": "Первые шаги" } ] }
```

`format=csv` 直接回下载文件（UTF-8 BOM，Excel 双击可开）。双语文件未生成时 409。

## GET /api/requests

最近 100 条 API 请求轨迹（不含状态面板刷新）：`[ { "time", "method", "path", "status", "ms" } ]`。

## GET /api/project/report?path=…

项目分析统计（需先跑过 `analyze`）。

```json
[ { "targetLang": "ru-RU", "analysis": {
      "total": { "words": 1234, "segments": 100, "characters": 2500 },
      "perfect": {…}, "exact": {…}, "inContextExact": {…}, "new": {…}, "repetitions": {…} } } ]
```

## GET /api/project/report?path=…

项目分析统计/报价词数报告（需先跑过 `analyze`）。返回 `{ project, total, targets }`（旧版直接返回 targets 数组，现聚合为带 `total` 的对象）：

```json
{ "project": "…", "total": { "words": 6800, "segments": 520, "characters": 15200, "targetLangs": 1 },
  "targets": [ { "targetLang": "ru-RU", "analysis": {
      "total": { "words": 1234, "segments": 100, "characters": 2500 },
      "perfect": {…}, "exact": {…}, "inContextExact": {…}, "new": {…}, "repetitions": {…} } } ] }
```

`total` 为各匹配等级 across 目标语言的词/句/字符汇总，供给客户报价用。追加 `&format=csv` 直接下载 UTF-8 报表：
列 `targetLang,level,words,segments,characters`，每目标语言 × 6 匹配等级一行。

## GET /api/health

内网基线自检（无需项目）：对 **API 自身 / TM(tmUrl) / LLM(llmBaseUrl+model) / 术语源(termBaseUrl)** 逐项发轻量 HTTP 探测（并行、各 5s 超时），返回每项 `ok` + `latencyMs`，不通时给 `error`。部署到新内网机器后一键验证网关链路可达性。

```json
{ "product": "TradosToolkit", "listening": true, "checked": "…",
  "tm":  { "ok": true,  "latencyMs": 3 },
  "llm": { "ok": true,  "latencyMs": 42, "http": 200 },
  "term":{ "ok": false, "error": "termBaseUrl 未配置" } }
```

## POST /api/project/pretranslate?path=…&files=id1,id2(可选)

远程预翻译调度：等价于 `POST /api/project/task?task=pretranslate&async=1`，固定走后台异步，立即返回 `202 {taskId, task, status}`，进度查 `GET /api/task?id=`。body 可选同 task 端点：`{"providerUri":"tradostoolkit://…","providerState":""}`。

## POST /api/glossary/backfill

术语库自动回填（统计共现法，零 LLM 成本）。从**已确认**双语段反抽高频术语对写入译前术语库 `kind=pre`，返回抽取列表供人工复核。

两种入料：`{"srcLang":"zh-CN","tgtLang":"en-US","bilingualPath":"D:\\…\\xx.sdlxliff","max":200}`（推荐：bilingualPath 可直接取自 `/api/project/segments` 的 `bilingualPath` 字段）
或 `{"srcLang","tgtLang","segments":[{"source":"…","target":"…"},…]}`。

```json
{ "processed": 480, "extracted": 87, "max": 200, "kind": "pre",
  "languagePair": "zh-CN->en-US",
  "terms": [ { "term": "范围涵盖", "translation": "scope covers" }, … ],
  "message": "已写入译前术语库 87 对，建议在术语管理界面复核后再使用" }
```

质量标准：只取两侧都出现 ≥2 次且共现覆盖度 ≥0.5 的稳定短语对；纯数字/品牌直译对（两侧相同）跳过。抽取结果默认进入译前约束与 QA 术语检查，强烈建议先在术语管理界面复核。

## GET /api/project/tmfiles?path=…

项目目录下全部主 TM。

```json
[ { "path": "D:\\…\\translation_v1.sdltm", "size": 40960 } ]
```

## POST /api/project/package?path=…

创建交付任务并打包 `.sdlppx`（含主 TM、分析结果，打包前重算统计）。
body 可选：`{ "out": "D:\\x.sdlppx", "packageName": "demo", "comment": "…" }`；
`out` 缺省写到项目根目录 `包名_时间戳.sdlppx`。

→ `200 { "packagePath": "D:\\x.sdlppx", "manualTaskId": "…" }`

## GET /api/file?path=…

下载本机任意文件（字节流，`Content-Disposition` 带文件名）。令牌即门槛，服务仅监听 localhost。

## 错误约定

非 2xx 均返回 `{ "error": "中文说明" }`：400 参数缺失/非法、401 令牌错误、404 端点或文件不存在、405 方法不对、500 内部异常（详情看 api.log）。
