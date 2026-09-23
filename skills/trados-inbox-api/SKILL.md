---
name: "trados-inbox-api"
description: "通过本机 HTTP API 驱动 TradosToolkit 收件箱：投递源文件、取回自动生成的三件套（分析报告/交付包/匹配记忆库）、轮询任务、启停监控目录。用于 agent/脚本批量产出 Trados 交付物。"
---

# TradosToolkit 收件箱（监控目录）API

Trados Studio 2019 插件启动时会在本机开一个 HTTP 服务（`http://localhost:53902`，仅本机可访问）。本技能说明如何**直接调用**其中的收件箱端点：投递一个源文件，自动产出**三件套** —— **分析报告** + **`.sdlppx` 交付包** + **匹配 `.sdltm` 记忆库**。

## 何时使用

- 要用 API / 脚本 / Agent 让 Trados 批量产出「报告 + 交付包 + 匹配记忆库」。
- 要投递源文件并等待产出、轮询任务进度、启停监控目录、处理目录已有文件。
- 提到「收件箱 / 监控目录 / 三件套 / inbox / watch folder」的自动化。

> 不适用：普通翻译、术语库维护、纯项目操作（那些走 `docs/API.md` 的其它端点）。

## 前置条件（务必先确认）

1. **Trados Studio 2019 已打开且已加载插件** —— HTTP 服务随 Studio 启停，Studio 未打开时所有调用都会连接失败。
2. **API 令牌**：首次启动自动生成于 `%APPDATA%\TradosToolkit\api.token`。
3. **已配置本地记忆库扫描目录**（`tmScanDirectory`）—— 否则「匹配本地库」及套库/导出步骤会被跳过（仍产出报告与交付包，但无匹配记忆库）。

## 鉴权

服务基址 `http://localhost:53902`（仅本机）。除 `/api/status` 外都需令牌，二选一：

- 请求头：`X-Api-Key: <令牌>`
- 查询参数：`?key=<令牌>`

## 端点一览

| 方法 | 端点 | 作用 | 成功码 |
|---|---|---|---|
| GET | `/api/inbox` | 监视与任务总览 | 200 |
| POST | `/api/inbox/start` | 开始监视（可顺带落盘配置） | 200 |
| POST | `/api/inbox/stop` | 停止监视 | 200 |
| POST | `/api/inbox/process` | 投递单个源文件产出三件套 | 202 |
| POST | `/api/inbox/process-all` | 处理监控目录已有文件 | 200 |
| GET | `/api/inbox/jobs?limit=50` | 任务列表（轻量，1–100） | 200 |
| GET | `/api/inbox/job?id=` | 单任务详情（分步 + 日志） | 200 |

**标准工作流**：`POST /api/inbox/process` 拿到 `id` → 轮询 `GET /api/inbox/job?id=` 直到 `status=done` → 读 `reportPath` / `packagePath` / `tmPath` / `jobFolder`。

## 调用示例

### curl

```bash
TOKEN=$(cat "$APPDATA/TradosToolkit/api.token")
BASE=http://localhost:53902

# 总览
curl -s "$BASE/api/inbox" -H "X-Api-Key: $TOKEN"

# 投递源文件（异步，立即返回 202 + id）
curl -s -X POST "$BASE/api/inbox/process" -H "X-Api-Key: $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"file":"D:\\in\\a.docx","targetLang":"en-US"}'

# 轮询单任务
curl -s "$BASE/api/inbox/job?id=<id>" -H "X-Api-Key: $TOKEN"
```

### PowerShell（Windows PowerShell 5.1）

```powershell
$base  = 'http://localhost:53902'
$token = (Get-Content "$env:APPDATA\TradosToolkit\api.token" -Raw).Trim()
$h = @{ 'X-Api-Key' = $token }

# 投递：body 必须显式转成 UTF-8 字节，否则中文路径会乱码
$json = @{ file = 'D:\in\a.docx'; targetLang = 'en-US' } | ConvertTo-Json -Compress
$body = [System.Text.Encoding]::UTF8.GetBytes($json)
$job  = Invoke-RestMethod -Method Post -Uri "$base/api/inbox/process" -Headers $h `
          -ContentType 'application/json; charset=utf-8' -Body $body

# 轮询直到结束
do {
  Start-Sleep -Seconds 3
  $j = Invoke-RestMethod -Uri "$base/api/inbox/job?id=$($job.id)" -Headers $h
} until ($j.status -in 'done', 'error')
$j | ConvertTo-Json -Depth 10
```

> **中文编码坑**：Windows PowerShell 5.1 下，若响应头未声明 charset，`Invoke-RestMethod` 会按 Latin-1 解码，中文会变乱码。稳妥做法是用 `Invoke-WebRequest -UseBasicParsing` 取回后把 `RawContentStream` 按 UTF-8 解码，或在会话里设 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`。

## 投递参数

`POST /api/inbox/start` 与 `POST /api/inbox/process` 的可选字段（均为小驼峰）：

| 字段 | 说明 |
|---|---|
| `watchFolder` | 监控目录（`start` 用；不存在会报错） |
| `outputFolder` | 产出根目录 |
| `projectRoot` | 项目根目录 |
| `sourceLang` | 源语言，如 `zh-CN` |
| `targetLang` | 目标语言，如 `en-US` |
| `reportFormat` | 报告格式：`excel` \| `xml` \| `html` \| `mht` |
| `tmScanDirectory` | 本地记忆库扫描目录 |
| `autoStart` | `start` 专用，布尔 |

- `start` 的这些字段会**落盘到 `config.json`**（改变全局配置）。
- `process` 的这些字段**只作用于本次任务**，不改全局配置。

## 任务状态与产出

- 任务级 `status`：`queued` → `running` → `done`（或 `error`）。**异步**：投递立即返回，不阻塞。
- 步骤级 `steps[].status`：`pending` / `running` / `done` / `error` / `skipped`。
- 产出字段（对应步骤完成后才有值）：`reportPath` 报告、`packagePath` 交付包、`tmPath` 匹配记忆库、`jobFolder` 任务目录、`outputSummary` 摘要。
- 七步流水线：`0 匹配本地库 → 1 创建项目 → 2 套库预翻译 → 3 分析统计 → 4 生成分析报告 → 5 生成交付包 → 6 导出匹配记忆库`。任一步失败，其后步骤标记 `skipped`。

> **串行执行**：所有任务共用一个单线程队列，一次只跑一个。批量投递请排队等待，不要假设并发。

## 错误码

非 2xx 返回 `{ "error": "中文说明" }`。

| 码 | 含义 |
|---|---|
| 400 | 参数缺失/非法（缺 `file` 或 `id`、路径非法） |
| 401 | 令牌缺失或错误 |
| 404 | 文件不存在 / 任务 id 不存在 / 未知端点 |
| 405 | 方法不对（如对 `start` 用 GET） |
| 409 | 该文件已在处理队列中 |
| 412 | 监视未启动且无法自动启动（未配置监控目录） |
| 500 | 保存配置失败 / 内部异常 |

## 排障

| 现象 | 处理 |
|---|---|
| 连接被拒绝 | Studio 未打开或插件未加载，先启动 Studio |
| 401 | 读 `%APPDATA%\TradosToolkit\api.token`，确认令牌正确 |
| 412 | 先 `POST /api/inbox/start` 并指定存在的 `watchFolder` |
| 409 | 同文件重复投递，等前一个完成或换文件 |
| 步骤 0/2/6 被跳过 | 配置 `tmScanDirectory` 并重建本地库索引 |
| 中文乱码 | 请求体按 UTF-8 编码；响应按 UTF-8 解码（见上「中文编码坑」） |
| 任务一直 running | 大文件耗时正常；长时间无变化查 `%APPDATA%\TradosToolkit\plugin.log` |

## 参考

- 完整详解（字段 / 响应 / 流水线全量）：[docs/INBOX_API.md](../../docs/INBOX_API.md)
- 全部端点速查：[docs/API.md](../../docs/API.md)
- 日志：`%APPDATA%\TradosToolkit\api.log`（HTTP）、`%APPDATA%\TradosToolkit\plugin.log`（插件内部）
