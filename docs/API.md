# TradosToolkit 本地 HTTP API 文档

Studio 打开并加载插件后自动启动，仅供本机 agent（如 Claude Code）驱动 Trados 2019。
Studio 关闭即服务消失，属设计行为。

- 基址：`http://localhost:53902`（端口改 `%APPDATA%\TradosToolkit\api.json` 的 `port`，`enabled:false` 可整体关闭）
- 鉴权：除 `/api/status` 外均需请求头 `X-Api-Key: <令牌>`，或查询参数 `?key=<令牌>`
  - 令牌文件：`%APPDATA%\TradosToolkit\api.token`（首次启动自动生成）
- 日志：`%APPDATA%\TradosToolkit\api.log`
- 编码：请求/响应均为 UTF-8 JSON（`/api/file` 除外，返回字节流）

## GET /api/status

探活，**免密钥**。

```bash
curl -s http://localhost:53902/api/status
```

```json
{ "product": "TradosToolkit", "studio": "x64", "version": "1.0.0.0",
  "port": 53902, "startedAt": "2026-09-19T10:00:00", "listening": true }
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

## POST /api/project/task?path=…&task=…&files=…

同步执行自动化任务（返回即完成，无轮询）。

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

## GET /api/project/report?path=…

项目分析统计（需先跑过 `analyze`）。

```json
[ { "targetLang": "ru-RU", "analysis": {
      "total": { "words": 1234, "segments": 100, "characters": 2500 },
      "perfect": {…}, "exact": {…}, "inContextExact": {…}, "new": {…}, "repetitions": {…} } } ]
```

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
