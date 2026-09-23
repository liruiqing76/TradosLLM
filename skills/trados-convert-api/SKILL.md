---
name: "trados-convert-api"
description: "通过本机 HTTP API 驱动 TradosToolkit 文件转换：投递任意受支持的源文件，直接产出单个目标语言的 .sdlxliff（一次性转换，不保留项目）。用于 agent/脚本把 docx/xlsx/txt/xml/html 等批量转成 Trados 双语文件。"
---

# TradosToolkit 文件转换 API

Trados Studio 2019 插件启动时会在本机开一个 HTTP 服务（`http://localhost:53902`，仅本机可访问）。本技能说明如何**直接调用**其中的转换端点：投递一个受 Studio 文件类型系统支持的源文件，产出**单个目标语言的 `.sdlxliff`**（临时项目静默创建、转换完即清理，只留结果文件）。

## 何时使用

- 要用 API / 脚本 / Agent 把任意源文件一键转成 `.sdlxliff` 双语文件。
- 只想拿到「源文件 → 目标语言 sdlxliff」这一件事，不需要报告 / 交付包 / 记忆库。
- 提到「文件转换 / 转 sdlxliff / convert / 任意文件转 Trados」的自动化。

> 不适用：
> - 需要「分析报告 + `.sdlppx` 交付包 + 匹配 `.sdltm` 记忆库」三件套 → 用 `trados-inbox-api`。
> - 普通项目管理、术语库维护 → 走 `docs/API.md` 的其它端点。

## 前置条件（务必先确认）

1. **Trados Studio 2019 已打开且已加载插件** —— HTTP 服务随 Studio 启停，Studio 未打开时所有调用都会连接失败。
2. **API 令牌**：首次启动自动生成于 `%APPDATA%\TradosToolkit\api.token`。
3. **输入文件必须是 Studio 文件类型系统认识的格式**（docx / xlsx / pptx / txt / xml / html 等）。
   PDF / 扫描件等需先安装对应文件类型过滤器，否则转换任务会报错。
4. 若不指定 `template`，需保证已存在可用的项目模板（自动挑模板，规则同 `POST /api/projects`）。

## 鉴权

服务基址 `http://localhost:53902`（仅本机）。除 `/api/status` 外都需令牌，二选一：

- 请求头：`X-Api-Key: <令牌>`
- 查询参数：`?key=<令牌>`

## 端点一览

| 方法 | 端点 | 作用 | 成功码 |
|---|---|---|---|
| POST | `/api/convert` | 单个源文件 → 目标语言 `.sdlxliff` | 200（同步）/ 202（`async=1`） |
| GET | `/api/task?id=` | 查后台任务进度（`async=1` 时用） | 200 |

**标准工作流**：
- 同步：直接 `POST /api/convert` → 读 `output`。
- 异步：`POST /api/convert?async=1` 拿 `taskId` → 轮询 `GET /api/task?id=` 直到结束 → 读结果。

## 调用示例

### curl

```bash
TOKEN=$(cat "$APPDATA/TradosToolkit/api.token")
BASE=http://localhost:53902

# 单个文件一次转换（同步）
curl -s -X POST "$BASE/api/convert" -H "X-Api-Key: $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"file":"D:\\in\\a.docx","sourceLang":"en-US","targetLang":"de-DE"}'

# 后台执行（大文件推荐）
curl -s -X POST "$BASE/api/convert?async=1" -H "X-Api-Key: $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"file":"D:\\in\\a.docx","sourceLang":"en-US","targetLang":"de-DE"}'

# 查进度
curl -s "$BASE/api/task?id=<taskId>" -H "X-Api-Key: $TOKEN"
```

### PowerShell（Windows PowerShell 5.1）

```powershell
$base  = 'http://localhost:53902'
$token = (Get-Content "$env:APPDATA\TradosToolkit\api.token" -Raw).Trim()
$h = @{ 'X-Api-Key' = $token }

# body 必须显式转成 UTF-8 字节，否则中文路径会乱码
$json = @{ file = 'D:\in\a.docx'; sourceLang = 'en-US'; targetLang = 'de-DE' } | ConvertTo-Json -Compress
$body = [System.Text.Encoding]::UTF8.GetBytes($json)
$res  = Invoke-RestMethod -Method Post -Uri "$base/api/convert" -Headers $h `
          -ContentType 'application/json; charset=utf-8' -Body $body
$res.output
```

> **中文编码坑**：Windows PowerShell 5.1 下，若响应头未声明 charset，`Invoke-RestMethod` 会按 Latin-1 解码，中文会变乱码。稳妥做法是用 `Invoke-WebRequest -UseBasicParsing` 取回后把 `RawContentStream` 按 UTF-8 解码，或在会话里设 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`。

## 请求参数

`POST /api/convert` 的 body 字段（均为小驼峰）：

| 字段 | 必填 | 说明 |
|---|---|---|
| `file` | 是 | 源文件绝对路径 |
| `sourceLang` | 是 | 源语言，如 `zh-CN` |
| `targetLang` | 是 | 目标语言，如 `en-US` |
| `output` | 否 | 输出 `.sdlxliff` 路径；缺省 = 输入文件同目录同名加 `.sdlxliff` |
| `template` | 否 | 项目模板 `.sdlpt` 路径；缺省时自动挑模板 |
| `keepProject` | 否 | 默认 `false`，转换完清理临时项目目录；`true` 则保留 |

查询参数：

| 参数 | 说明 |
|---|---|
| `async=1` | 走后台任务，立即返回 `202`，进度查 `GET /api/task?id=` |

## 响应与产出

同步成功 → `200`：

```json
{ "output": "D:\\in\\a.sdlxliff", "file": "D:\\in\\a.docx",
  "sourceLang": "en-US", "targetLang": "de-DE",
  "projectPath": "C:\\…\\临时项目目录", "messages": ["…"] }
```

异步 → `202 { "taskId": "…", "task": "convert", "status": "running" }`。

- `output`：最终产出的目标语言 `.sdlxliff` 路径（唯一需要的产物）。
- `projectPath`：临时项目目录；未设 `keepProject` 时转换结束即被清理（保留时可在 Studio 里打开排查）。
- `messages`：转换过程中的执行消息（如过滤器警告）。

> **内部流程**：临时目录静默创建项目 → `AddFiles` → `Scan` / `ConvertToTranslatableFormat` / `CopyToTargetLanguages` → 把目标 sdlxliff 复制到 `output`。

## 错误码

非 2xx 返回 `{ "error": "中文说明" }`。

| 码 | 含义 |
|---|---|
| 400 | 参数缺失/非法（缺 `file`/`sourceLang`/`targetLang`、路径非法） |
| 401 | 令牌缺失或错误 |
| 404 | 源文件不存在 |
| 405 | 方法不对（该端点只接受 POST） |
| 500 | 内部异常（如缺少文件类型过滤器、模板不可用） |

## 排障

| 现象 | 处理 |
|---|---|
| 连接被拒绝 | Studio 未打开或插件未加载，先启动 Studio |
| 401 | 读 `%APPDATA%\TradosToolkit\api.token`，确认令牌正确 |
| 400 缺参数 | 确认 body 含 `file` / `sourceLang` / `targetLang` |
| 404 | 源文件路径不存在或不可访问，用绝对路径 |
| 500 / 转换报错 | 多因输入格式不受支持或缺少对应文件类型过滤器（PDF/扫描件常见）；也可在 Studio 里用「文件转换」按钮打开窗口复现 |
| 中文乱码 | 请求体按 UTF-8 编码；响应按 UTF-8 解码（见上「中文编码坑」） |
| 大文件耗时 | 加 `?async=1` 走后台，轮询 `GET /api/task?id=`；长时间无变化查 `%APPDATA%\TradosToolkit\plugin.log` |

## 参考

- 端点详解：[docs/API.md](../../docs/API.md)（`POST /api/convert` 一节）
- 三件套（报告/交付包/记忆库）批量产出：[docs/INBOX_API.md](../../docs/INBOX_API.md)，技能 `trados-inbox-api`
- 日志：`%APPDATA%\TradosToolkit\api.log`（HTTP）、`%APPDATA%\TradosToolkit\plugin.log`（插件内部）
