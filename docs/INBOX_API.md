# 收件箱（监控目录）API 详细文档

> 把界面上「把源文件丢进监控目录 → 自动产出三件套」的能力，暴露成本机 HTTP API，供外部程序 / Agent 投递源文件并取回产出。
>
> 三件套 = **分析报告** + **`.sdlppx` 交付包** + **匹配 `.sdltm` 记忆库**。

---

## 目录

- [1. 功能概述](#1-功能概述)
- [2. 架构与运行原理](#2-架构与运行原理)
- [3. 前置条件与配置](#3-前置条件与配置)
- [4. 快速上手](#4-快速上手)
- [5. 端点参考](#5-端点参考)
  - [GET /api/inbox](#get-apiinbox)
  - [POST /api/inbox/start](#post-apiinboxstart)
  - [POST /api/inbox/stop](#post-apiinboxstop)
  - [POST /api/inbox/process](#post-apiinboxprocess)
  - [POST /api/inbox/process-all](#post-apiinboxprocess-all)
  - [GET /api/inbox/jobs](#get-apiinboxjobs)
  - [GET /api/inbox/job](#get-apiinboxjobid)
- [6. 七步产出流水线](#6-七步产出流水线)
- [7. 任务状态模型](#7-任务状态模型)
- [8. 单任务配置覆盖](#8-单任务配置覆盖)
- [9. 错误码约定](#9-错误码约定)
- [10. 安全与访问范围](#10-安全与访问范围)
- [11. 常见问题排查](#11-常见问题排查)
- [12. 实现说明（开发者）](#12-实现说明开发者)

---

## 1. 功能概述

Trados Studio 插件里有一个「收件箱」功能：把一个或多个源文件（如 `.docx`）放进**监控目录**，插件会自动为每个文件走完一整套流程，最终在一个**任务目录**里产出三件套：

| 产出 | 文件 | 说明 |
|---|---|---|
| 分析报告 | `<文件名>_分析报告.xls`（或 `.xml/.html/.mht`） | 由 Studio 报告引擎「另存为」生成 |
| 交付包 | `<项目名>.sdlppx` | 可直接交付 / 导入的标准 Trados 项目包 |
| 匹配记忆库 | `匹配记忆库_<库名>.sdltm` | 从本地库中抽出本文档命中的翻译单元，另存为专用小库 |

本 API 把这个能力开放给外部调用者。**你不再需要把文件手动拷进监控目录**，只要一次 HTTP 请求投递源文件路径，即可拿到任务 id，然后轮询任务详情、读取三件套的绝对路径。

设计要点（对使用者的直接影响）：

- **异步任务式**：投递立即返回 `202` + 任务 `id`，不会挂住 HTTP 连接；后续用 `GET /api/inbox/job?id=` 轮询进度与产出。
- **串行执行**：所有任务走同一个单线程队列，一个一个跑（Studio 的项目自动化**必须串行**，不能并发）。因此大量投递时请自行控制并发 / 排队预期。
- **仅本机**：服务只监听 `localhost`，且需要 API 令牌，不对外开放到局域网。
- **统一任务视图**：无论通过 API 投递、还是直接拖进监控目录，任务都会出现在 `/api/inbox/jobs` 里，保留最近 **100** 条。

---

## 2. 架构与运行原理

### 2.1 组件关系

```
外部调用方 (curl / Agent / 程序)
        │  HTTP (localhost:53902)
        ▼
ToolkitApiServer (HttpListener 单例)
        │  鉴权后路由
        ▼
ProjectApi.Handle(...)  ── 命中 "/api/inbox*" ──▶  InboxApi.Handle(...)
                                                       │
                                                       │ 投递 / 启停 / 查询
                                                       ▼
                                              InboxWatcher (单例 · 单线程队列)
                                                       │ 逐个取出
                                                       ▼
                                              InboxJob (任务对象 · 7 步状态)
                                                       │
                                                       ▼
                                          InboxOrchestrator.Run(job, cfg)  ← 7 步引擎
                                                       │ 进程内复用业务方法
                                                       ▼
                                          ProjectApi.CreateProject / ExecuteTaskOnProject
                                          / SaveTaskReport / Package …（不经过网络）
```

### 2.2 关键设计

- **唯一串行执行器**：`InboxWatcher` 内部只有一个后台线程（`TradosToolkit-Inbox`）从 `ConcurrentQueue` 取文件逐个处理。API 投递与目录事件最终都汇聚到这条队列，从根本上保证 Studio 自动化串行。
- **进程内复用业务逻辑**：`InboxOrchestrator` 直接调用 `ProjectApi` 的既有方法（建项目、跑任务、另存报告、打包），**不发起 HTTP 请求**，因此没有自调用/死锁风险。
- **无界面也能跑**：`InboxJob.Post(...)` 在无 UI `Dispatcher` 时直接同步执行，所以即使收件箱窗口没打开，API 投递照样正常产出。
- **任务注册表**：`InboxApi` 维护一份内存注册表。API 投递时先按「文件路径 → 记录」预登记，等 `JobCreated` 事件触发时把真实任务对象绑定上去；直接拖入目录触发的任务则按任务自带 `Id` 登记。
- **单任务配置覆盖**：投递时可在请求里指定语向 / 报告格式 / 产出目录等，只作用于**这一个文件**，不改全局配置（见 [第 8 节](#8-单任务配置覆盖)）。

---

## 3. 前置条件与配置

### 3.1 前置条件

1. **Trados Studio 2019 已打开**，且已加载本插件。HTTP 服务随 Studio 启动而启动，随 Studio 关闭而消失（设计行为）。
2. 已取得 **API 令牌**：首次启动自动生成于 `%APPDATA%\TradosToolkit\api.token`。
3. 已配置 **记忆库扫描目录**（`tmScanDirectory`）——否则「匹配本地库」步骤会跳过，步骤 2、6 也一并跳过（仍能产出报告与交付包，但拿不到匹配记忆库）。
4. 目标语向对应的本地库已建立索引（工作台「记忆库」页可扫描/重建）。

### 3.2 配置文件

配置文件：`%APPDATA%\TradosToolkit\config.json`。与本功能相关的字段：

| 字段 | 类型 | 缺省 | 说明 |
|---|---|---|---|
| `inboxWatchFolder` | string | 空 | 监控目录（投放目录）。为空时无法启动监视 |
| `inboxOutputFolder` | string | 空 | 产出根目录；每个任务一个子目录。空 = `我的文档\TradosToolkit 收件箱` |
| `inboxProjectRoot` | string | 空 | 自动创建项目的存放目录；空 = `<产出目录>\Projects` |
| `inboxSourceLang` | string | `zh-CN` | 源语言（.NET 区域代码） |
| `inboxTargetLang` | string | `en-US` | 目标语言 |
| `inboxReportFormat` | string | `excel` | 分析报告格式：`excel` \| `xml` \| `html` \| `mht` |
| `inboxAutoStart` | bool | `false` | Studio 启动时是否自动开始监视 |
| `tmScanDirectory` | string | 空 | 本地记忆库扫描目录（步骤 0 匹配用） |

> `POST /api/inbox/start` 可在启动监视的同时把这些字段落盘（见 [第 5 节](#post-apiinboxstart)）。

### 3.3 产出目录结构

每个任务产出到独立子目录，自包含、互不干扰：

```
<产出根目录>\
└── <源文件名>_<yyyyMMdd_HHmmss>\        ← 任务目录 (jobFolder)
    ├── Source\
    │   └── <源文件名>.docx               ← 源文件被移入此处（保持监控目录干净）
    ├── <源文件名>_分析报告.xls            ← 报告 (reportPath)
    ├── <项目名>.sdlppx                   ← 交付包 (packagePath)
    └── 匹配记忆库_<库名>.sdltm           ← 匹配记忆库 (tmPath)
```

> 注意：源文件会被**移动**到任务目录的 `Source\` 下（移动失败则复制后删除原文件）。这是为了避免监控目录反复触发，并让任务产物自包含。

---

## 4. 快速上手

### 4.1 最小流程

```bash
TOKEN=$(cat ~/AppData/Roaming/TradosToolkit/api.token)
BASE=http://localhost:53902

# 1) 投递一个源文件（产出三件套）
curl -s -X POST "$BASE/api/inbox/process" \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{"file":"D:\\in\\a.docx"}'
# → {"id":"1a2b3c4d5e6f","file":"D:\\in\\a.docx","status":"queued","poll":"/api/inbox/job?id=1a2b3c4d5e6f"}

# 2) 轮询任务详情
curl -s "$BASE/api/inbox/job?id=1a2b3c4d5e6f" -H "X-Api-Key: $TOKEN"
# 未完成时 status=running；完成后 status=done，并带 reportPath/packagePath/tmPath/jobFolder
```

### 4.2 PowerShell 轮询示例

```powershell
$token = Get-Content "$env:APPDATA\TradosToolkit\api.token"
$base  = "http://localhost:53902"
$h = @{ "X-Api-Key" = $token }

$r = Invoke-RestMethod -Method Post -Uri "$base/api/inbox/process" -Headers $h `
     -ContentType "application/json" -Body '{"file":"D:\\in\\a.docx"}'
Write-Host "已投递 jobId=$($r.id)"

do {
    Start-Sleep -Seconds 3
    $job = Invoke-RestMethod -Uri "$base/api/inbox/job?id=$($r.id)" -Headers $h
    Write-Host "[$($job.status)] $($job.statusText) - $($job.message)"
} while ($job.status -in @("queued","running"))

$job | Select-Object reportPath, packagePath, tmPath, jobFolder
```

### 4.3 处理监控目录里已有的文件

如果你习惯先往目录里丢文件，再统一触发：

```bash
curl -s -X POST "$BASE/api/inbox/process-all" -H "X-Api-Key: $TOKEN"
# → {"enqueued":3,"watchingFolder":"D:\\in"}
```

---

## 5. 端点参考

> 以下所有端点都需要令牌：请求头 `X-Api-Key: <令牌>`，或查询参数 `?key=<令牌>`。
> 编码：请求/响应均为 UTF-8 JSON。

---

### GET /api/inbox

监视与任务总览。

**请求**

```bash
curl -s "$BASE/api/inbox" -H "X-Api-Key: $TOKEN"
```

**响应 `200`**

```json
{
  "running": true,
  "watchingFolder": "D:\\in",
  "watchFolder": "D:\\in",
  "outputFolder": "D:\\out",
  "projectRoot": "D:\\proj",
  "tmScanDirectory": "D:\\tm",
  "sourceLang": "zh-CN",
  "targetLang": "en-US",
  "reportFormat": "excel",
  "autoStart": false,
  "jobsTotal": 5,
  "jobsActive": 1
}
```

| 字段 | 说明 |
|---|---|
| `running` | 监视是否正在运行 |
| `watchingFolder` | 当前实际监视的目录（未运行则为空） |
| `watchFolder` / `outputFolder` / `projectRoot` | 配置中的监控目录 / 产出目录 / 项目目录 |
| `tmScanDirectory` | 本地库扫描目录 |
| `sourceLang` / `targetLang` / `reportFormat` / `autoStart` | 配置中的语向 / 报告格式 / 自启开关 |
| `jobsTotal` | 注册表内任务总数（上限 100） |
| `jobsActive` | 处于 `queued` 或 `running` 的任务数 |

---

### POST /api/inbox/start

开始监视。可在 body 里顺带把配置**落盘**到 `config.json`（只更新给出的字段，其余不动）。

**请求 body（全部可选）**

```json
{
  "watchFolder": "D:\\in",
  "outputFolder": "D:\\out",
  "projectRoot": "D:\\proj",
  "sourceLang": "zh-CN",
  "targetLang": "en-US",
  "reportFormat": "excel",
  "tmScanDirectory": "D:\\tm",
  "autoStart": true
}
```

| 字段 | 说明 |
|---|---|
| `watchFolder` | 监控目录（必填项，为空或目录不存在时启动失败） |
| `outputFolder` | 产出根目录 |
| `projectRoot` | 项目存放目录 |
| `sourceLang` / `targetLang` | 语向 |
| `reportFormat` | `excel` \| `xml` \| `html` \| `mht` |
| `tmScanDirectory` | 本地库扫描目录 |
| `autoStart` | 是否在下次 Studio 启动时自动开始监视 |

**响应**

- 成功：`200`，返回与 `GET /api/inbox` 相同的总览对象。
- `412`：`watchFolder` 未配置或目录不存在（`{ "error": "监视目录不存在：…" }`）。
- `500`：写配置失败。

---

### POST /api/inbox/stop

停止监视。会清空队列、待处理集合与所有单任务覆盖。

**请求**

```bash
curl -s -X POST "$BASE/api/inbox/stop" -H "X-Api-Key: $TOKEN"
```

**响应**：`200`，返回与 `GET /api/inbox` 相同的总览对象（`running` 变为 `false`）。

---

### POST /api/inbox/process

**核心端点**：投递单个源文件，产出三件套。异步执行。

**请求 body**

```json
{
  "file": "D:\\in\\a.docx",
  "sourceLang": "zh-CN",
  "targetLang": "en-US",
  "reportFormat": "excel",
  "outputFolder": "D:\\out",
  "projectRoot": "D:\\proj",
  "tmScanDirectory": "D:\\tm"
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `file` | ✅ | 待处理源文件的**绝对路径** |
| `sourceLang` | ✖ | 覆盖本次任务的源语言 |
| `targetLang` | ✖ | 覆盖本次任务的目标语言 |
| `reportFormat` | ✖ | 覆盖本次任务的报告格式 |
| `outputFolder` | ✖ | 覆盖本次任务的产出根目录 |
| `projectRoot` | ✖ | 覆盖本次任务的项目目录 |
| `tmScanDirectory` | ✖ | 覆盖本次任务的本地库扫描目录 |

> `file` 之外的字段**只作用于这一个任务**，不写盘、不影响后续任务（见 [第 8 节](#8-单任务配置覆盖)）。

**行为**

1. 规范化 `file` 为绝对路径并校验存在性。
2. 若监视未运行，自动按当前配置启动（失败则 `412`）。
3. 在注册表登记记录，把「路径 → 记录」加入待绑定表。
4. 调用 `InboxWatcher.Enqueue(file, overrideCfg)` 入队。

**响应 `202`**

```json
{
  "id": "1a2b3c4d5e6f",
  "file": "D:\\in\\a.docx",
  "status": "queued",
  "poll": "/api/inbox/job?id=1a2b3c4d5e6f"
}
```

| 错误 | 状态码 | 场景 |
|---|---|---|
| `400` | 缺少 `file`，或路径非法 |
| `404` | `file` 指向的文件不存在 |
| `409` | 该文件已在处理队列中 |
| `412` | 监视未启动且无法自动启动（未配置监控目录） |

**curl 示例**

```bash
curl -s -X POST "$BASE/api/inbox/process" \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{"file":"D:\\in\\a.docx","targetLang":"en-US","reportFormat":"xml"}'
```

---

### POST /api/inbox/process-all

处理监控目录里**已有的全部**文件（相当于界面上的「立即处理」）。不接受 body。

**行为**：若监视未运行则自动启动；随后把监控目录下所有通过过滤规则的文件入队。

> 过滤规则：跳过隐藏文件、目录、以 `~$` 或 `.` 开头的文件，以及 `.tmp/.temp/.crdownload/.part/.partial/.sdlppx/.sdltm/.sdltb/.log` 等扩展名；产出目录若被配进监控目录也会被排除（避免自产自销）。

**响应 `200`**

```json
{ "enqueued": 3, "watchingFolder": "D:\\in" }
```

| 错误 | 状态码 | 场景 |
|---|---|---|
| `412` | 监视未启动且无法自动启动 |

---

### GET /api/inbox/jobs

任务列表（**轻量**，不含步骤明细与日志），新→旧排序。

**查询参数**

| 参数 | 缺省 | 说明 |
|---|---|---|
| `limit` | 50 | 返回条数，范围 1–100 |

**请求**

```bash
curl -s "$BASE/api/inbox/jobs?limit=20" -H "X-Api-Key: $TOKEN"
```

**响应 `200`**（数组）

```json
[
  {
    "id": "1a2b3c4d5e6f",
    "file": "D:\\in\\a.docx",
    "fileName": "a.docx",
    "status": "done",
    "statusText": "已完成",
    "message": "三件套已生成 · D:\\out\\a_20260923_101500",
    "submittedAt": "2026-09-23T10:15:00",
    "elapsedMs": 92345.6,
    "projectPath": "D:\\proj\\a_20260923_101500\\a_20260923_101500.sdlp",
    "reportPath": "D:\\out\\a_20260923_101500\\a_分析报告.xls",
    "packagePath": "D:\\out\\a_20260923_101500\\a_20260923_101500.sdlppx",
    "tmPath": "D:\\out\\a_20260923_101500\\匹配记忆库_xxx.sdltm",
    "jobFolder": "D:\\out\\a_20260923_101500",
    "outputSummary": "报告 + 交付包 + 匹配库"
  }
]
```

---

### GET /api/inbox/job?id=

单任务详情。在列表字段基础上追加 `createdAt`、`finishedAt`、分步 `steps[]` 与完整 `log`。

**查询参数**

| 参数 | 必填 | 说明 |
|---|---|---|
| `id` | ✅ | 任务 id（来自 `process` 响应或 `jobs` 列表） |

**请求**

```bash
curl -s "$BASE/api/inbox/job?id=1a2b3c4d5e6f" -H "X-Api-Key: $TOKEN"
```

**响应 `200`**

```json
{
  "id": "1a2b3c4d5e6f",
  "file": "D:\\in\\a.docx",
  "fileName": "a.docx",
  "status": "running",
  "statusText": "运行中",
  "message": "等待处理…",
  "submittedAt": "2026-09-23T10:15:00",
  "createdAt": "2026-09-23T10:15:02",
  "finishedAt": null,
  "elapsedMs": 12345.6,
  "projectPath": "D:\\proj\\a_20260923_101500\\a_20260923_101500.sdlp",
  "reportPath": "",
  "packagePath": "",
  "tmPath": "",
  "jobFolder": "D:\\out\\a_20260923_101500",
  "outputSummary": "—",
  "steps": [
    { "index": 0, "title": "匹配本地记忆库", "status": "done",    "detail": "xxx库" },
    { "index": 1, "title": "创建项目",       "status": "done",    "detail": "a_20260923_101500.sdlp" },
    { "index": 2, "title": "套库预翻译",     "status": "running", "detail": "附加本地库并预翻译…" },
    { "index": 3, "title": "分析统计",       "status": "pending", "detail": "" },
    { "index": 4, "title": "生成分析报告",   "status": "pending", "detail": "" },
    { "index": 5, "title": "生成交付包 (.sdlppx)", "status": "pending", "detail": "" },
    { "index": 6, "title": "导出匹配记忆库", "status": "pending", "detail": "" }
  ],
  "log": "10:15:02 开始处理：a.docx\n10:15:02 匹配本地记忆库：xxx库（zh-CN → en-US）\n…"
}
```

| 错误 | 状态码 | 场景 |
|---|---|---|
| `400` | 缺少 `id` |
| `404` | 无此任务 id（可能已超出 100 条上限被清理） |

---

## 6. 七步产出流水线

`InboxOrchestrator.Run(job, cfg)` 顺序执行 7 步。**任一步抛异常 → 该步标记 `error`，其后所有 `pending` 步骤标记 `skipped`，任务整体 `status=error`**（无「容错继续」模式）。

| 步骤 | 标题 | 说明 | 可跳过条件 |
|---|---|---|---|
| 0 | 匹配本地记忆库 | 按 `tmScanDirectory` 扫描索引，按语向 `源语言 → 目标语言` 查找可用库 | 未配置目录 / 语言代码无效 / 无匹配库 |
| 1 | 创建项目 | 建任务目录、把源文件移入 `Source\`、调用 `CreateProject` 建 `.sdlp` | 不可跳过（失败即中止） |
| 2 | 套库预翻译 | 附加步骤 0 命中的库并跑 `pretranslate` | 步骤 0 未命中则跳过 |
| 3 | 分析统计 | 跑 `analyze`，产出报告对象供步骤 4 使用 | 不可跳过 |
| 4 | 生成分析报告 | 用 Studio 报告引擎把分析报告「另存为」到任务目录（格式取自配置） | 不可跳过 |
| 5 | 生成交付包 | 调用 `Package` 打包 `.sdlppx` 到任务目录 | 不可跳过 |
| 6 | 导出匹配记忆库 | 从命中的库中抽出本文档命中的翻译单元，另存为专用 `.sdltm` | 步骤 0 未命中则跳过 |

**关于步骤 6 的兜底**：若无法从双语文件抽到本文档句段，或句段未命中该库，则**退化为整库复制**，保证三件套始终齐全。

**完成标记**：全部走完 → `status=done`，`message` = `三件套已生成 · <jobFolder>`。

---

## 7. 任务状态模型

### 7.1 任务级 `status`

| 值 | `statusText` | 含义 |
|---|---|---|
| `queued` | 排队中 | 已入队，尚未开始 |
| `running` | 运行中 | 正在执行 |
| `done` | 已完成 | 三件套已生成 |
| `error` | 失败 | 中途出错，`message` 带原因 |

### 7.2 步骤级 `steps[].status`

| 值 | `StatusText` | 含义 |
|---|---|---|
| `pending` | ○ 待处理 | 尚未开始 |
| `running` | ◐ 进行中 | 正在执行 |
| `done` | ✓ 完成 | 成功 |
| `error` | ✕ 失败 | 失败（`detail` 带原因） |
| `skipped` | – 跳过 | 前置条件不满足或前序步骤失败 |

### 7.3 产出字段

| 字段 | 对应产出 |
|---|---|
| `jobFolder` | 任务目录（所有产出的根） |
| `reportPath` | 分析报告 |
| `packagePath` | `.sdlppx` 交付包 |
| `tmPath` | 匹配 `.sdltm` 记忆库 |
| `projectPath` | 自动创建的 `.sdlp` 项目 |
| `outputSummary` | 产出摘要，如 `报告 + 交付包 + 匹配库`；无产出为 `—` |

> 这些字段在对应步骤完成后才会有值；未完成的步骤对应字段为空字符串。

---

## 8. 单任务配置覆盖

在 `POST /api/inbox/process` 的 body 里带上可选字段（`sourceLang` / `targetLang` / `reportFormat` / `outputFolder` / `projectRoot` / `tmScanDirectory`），即可**只针对这一个任务**使用不同配置。

**机制**：

1. 投递时把覆盖配置放进 `InboxWatcher._overrides`（键 = 文件路径）。
2. 工作线程取到该文件时，`TryRemove` 取出覆盖配置作为本次运行的 `cfg`（取走即弃）。
3. 未提供覆盖时用监视器当前的全局配置。

**特点**：

- **不落盘**、不改 `config.json`、不影响监视目录的全局配置与后续任务。
- 未提供的字段沿用全局配置。
- 若入队失败（如同文件重复），覆盖配置会被回滚移除。

**示例**：全局是 `zh-CN → en-US`，但本次要按 `zh-CN → ru-RU`、输出 XML 报告：

```bash
curl -s -X POST "$BASE/api/inbox/process" \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{"file":"D:\\in\\b.docx","targetLang":"ru-RU","reportFormat":"xml"}'
```

---

## 9. 错误码约定

非 2xx 一律返回 `{ "error": "中文说明" }`。

| 状态码 | 含义（本 API 场景） |
|---|---|
| `200` | 成功（总览 / 处理已有 / 查询） |
| `202` | 已接受，任务已入队（`process`） |
| `400` | 参数缺失或非法（缺 `file` / `id`，路径非法） |
| `401` | 令牌缺失或错误 |
| `404` | 文件不存在 / 任务 id 不存在 / 未知收件箱端点 |
| `405` | 方法不对（如对 `inbox/start` 用 GET） |
| `409` | 该文件已在处理队列中 |
| `412` | 监视未启动且无法自动启动（未配置监控目录） |
| `500` | 保存配置失败 / 内部异常（详情看 `api.log`） |

> 完整的服务级错误约定见 [`docs/API.md`](./API.md)。

---

## 10. 安全与访问范围

- **仅本机**：`HttpListener` 只监听 `localhost`，局域网内其它机器无法访问。
- **令牌鉴权**：所有 `/api/inbox*` 端点均需 `X-Api-Key`（或 `?key=`）。令牌文件 `%APPDATA%\TradosToolkit\api.token`，首次启动自动生成。
- **生命周期**：HTTP 服务随 Studio 启动而启动、随 Studio 关闭而消失。Studio 未打开时 API 不可用。
- **文件系统访问**：`process` 的 `file` 可指向本机任意路径；请仅向你信任的调用方开放令牌。
- **日志**：`%APPDATA%\TradosToolkit\api.log`（HTTP 层）、`plugin.log`（插件内部 / 任务失败详情）。

---

## 11. 常见问题排查

| 现象 | 可能原因与处理 |
|---|---|
| 连接被拒绝 / 无法连接 | Studio 未打开，或服务未启动。确认 Studio 已加载插件 |
| `401` 令牌错误 | 令牌缺失或不匹配。读取 `%APPDATA%\TradosToolkit\api.token` |
| `412` 监视无法启动 | `inboxWatchFolder` 未配置或目录不存在。先调 `POST /api/inbox/start` 并给出 `watchFolder` |
| `404` 源文件不存在 | `file` 路径拼写错误，或未用绝对路径 |
| `409` 已在队列 | 同文件重复投递。等前一个完成，或换文件 |
| 步骤 0/2/6 被跳过 | 未配置 `tmScanDirectory`，或该语向无匹配本地库。配置扫描目录并重建索引 |
| 报告格式不对 | `reportFormat` 仅支持 `excel`/`xml`/`html`/`mht`，非法值回落到 `excel` |
| 任务查不到（`404`） | 任务超出 100 条上限被清理，或 id 有误 |
| 任务一直 `running` 不结束 | 大文件分析/预翻译耗时较长属正常；若长时间无变化，查 `plugin.log` |
| 产出缺「匹配记忆库」 | 步骤 0 未命中本地库（见上一条），退化为整库复制或跳过 |

**日志位置**

- `%APPDATA%\TradosToolkit\api.log` — HTTP 请求轨迹
- `%APPDATA%\TradosToolkit\plugin.log` — 插件内部日志、任务失败堆栈

---

## 12. 实现说明（开发者）

> 本节面向维护者，记录端点背后的代码位置与关键约定。

### 12.1 涉及文件

| 文件 | 角色 |
|---|---|
| `TradosToolkit/Server/InboxApi.cs` | **新增**：任务注册表 + `/api/inbox*` 端点实现 |
| `TradosToolkit/Inbox/InboxWatcher.cs` | 单线程串行执行器；新增 `_overrides` 与 `Enqueue(path, cfgOverride)` 重载 |
| `TradosToolkit/Inbox/InboxJob.cs` | 任务对象与 7 步状态模型 |
| `TradosToolkit/Inbox/InboxOrchestrator.cs` | 7 步产出引擎 |
| `TradosToolkit/Server/ProjectApi.cs` | 在鉴权之后挂载 `/api/inbox*` 路由 |
| `TradosToolkit/Action/TradosToolkitRibbonGroup.cs` | Studio 启动时调用 `InboxApi.EnsureInit()` 订阅 `JobCreated` |
| `TradosToolkit/TradosToolkit.csproj` | 注册 `Server\InboxApi.cs`（`EnableDefaultCompileItems=false`，须显式登记） |
| `docs/API.md` | 端点速查（本文为详细版） |

### 12.2 路由挂载

`ProjectApi.Handle(...)` 在通过令牌鉴权后、进入大 `switch (path)` 之前：

```csharp
// 收件箱（监控目录）外部 API：投递即产出三件套
if (path.StartsWith("/api/inbox", StringComparison.Ordinal))
    return InboxApi.Handle(method, path, query, body);
```

> 因此 `/api/inbox*` 继承了与其它端点一致的令牌鉴权。

### 12.3 注册表与线程安全

- `InboxApi` 用一把 `Gate` 锁保护 `Jobs` 列表与 `PendingByPath` 映射。
- `EnsureInit()` 保证 `JobCreated` 只订阅一次。
- `TrimLocked()` 在超过 100 条时优先清理**非活动**任务。
- `Summarize()` 读取 `job.Steps` 时容忍极端竞态（`try/catch` 丢弃明细）。

### 12.4 单任务覆盖实现

```csharp
// InboxWatcher
private readonly ConcurrentDictionary<string, ToolkitConfig> _overrides = ...;

public bool Enqueue(string path, ToolkitConfig cfgOverride)
{
    if (string.IsNullOrWhiteSpace(path)) return false;
    if (cfgOverride != null) _overrides[path] = cfgOverride;   // 先放覆盖
    if (Enqueue(path)) return true;                            // 再入队
    if (cfgOverride != null) { ToolkitConfig dropped; _overrides.TryRemove(path, out dropped); }
    return false;
}
```

```csharp
// WorkerLoop：取到文件后取走覆盖，仅作用于这一个任务
var runCfg = _cfg ?? ToolkitConfig.Load();
ToolkitConfig jobCfg;
if (_overrides.TryRemove(path, out jobCfg) && jobCfg != null) runCfg = jobCfg;
InboxOrchestrator.Run(job, runCfg);
```

`StopLocked()` 中会 `_overrides.Clear()`。

### 12.5 无界面运行

`InboxJob.Post(action)` 在 `UiDispatcher == null`（收件箱窗口未打开）时**直接同步执行** `action`，因此 API 路径无需任何 UI 即可完成全部状态更新。

### 12.6 相关端点速查

| 方法 | 路径 | 处理器 |
|---|---|---|
| GET | `/api/inbox` | `InboxApi.Status` |
| POST | `/api/inbox/start` | `InboxApi.Start` |
| POST | `/api/inbox/stop` | `InboxApi.Stop` |
| POST | `/api/inbox/process` | `InboxApi.Process` |
| POST | `/api/inbox/process-all` | `InboxApi.ProcessAll` |
| GET | `/api/inbox/jobs` | `InboxApi.ListJobs` |
| GET | `/api/inbox/job` | `InboxApi.OneJob` |
