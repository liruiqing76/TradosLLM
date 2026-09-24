using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using TradosToolkit.Diagnostics;
using TradosToolkit.EditorPanel;

namespace TradosToolkit.Server
{
    /// <summary>
    /// localhost:53902/ 极简 HTML 状态面板（免令牌、只读、本机可见）：
    /// 服务/TM/LLM/开关状态磁贴、最近 API 请求、后台任务、项目列表（2s UI 超时降级）、插件日志尾部。
    /// 浅色贴 Studio 宿主；5 秒 meta 自动刷新。全部经 HtmlEncode，不输出令牌等敏感值。
    /// </summary>
    public static class StatusPage
    {
        public static ApiResult Build()
        {
            var html = Render();
            var bytes = new UTF8Encoding(true).GetBytes(html);
            return new ApiResult { Status = 200, Bytes = bytes, ContentType = "text/html; charset=utf-8" };
        }

        private static string Render()
        {
            var server = ToolkitApiServer.Instance;
            var config = Safe(() => (object)ToolkitConfig.Load()) as ToolkitConfig;
            var uptime = server.StartedAt == default(DateTime) ? TimeSpan.Zero : DateTime.Now - server.StartedAt;
            var sb = new StringBuilder();

            sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>")
              .Append("<meta http-equiv=\"refresh\" content=\"5\"/>")
              .Append("<title>").Append(E(UiText.T("SP_Title"))).Append(" · localhost:").Append(server.Port).Append("</title>")
              .Append("<style>")
              .Append("body{background:#F5F7FA;color:#1F2A44;font:13px/1.6 'Segoe UI','Microsoft YaHei',sans-serif;margin:0;padding:24px}")
              .Append(".wrap{max-width:960px;margin:0 auto}")
              .Append("h1{font-size:19px;margin:0 0 4px}h1 span{color:#0F7AC4}.sub{color:#6B7686;font-size:12px;margin-bottom:18px}")
              .Append(".chips{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:18px}")
              .Append(".chip{background:#fff;border:1px solid #E3E8F0;border-radius:8px;padding:6px 12px;font-size:12px}")
              .Append(".chip b{font-weight:600}.dot{display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:6px}")
              .Append(".ok{background:#2EA86B}.bad{background:#E05D4B}.mut{background:#C0C6D2}")
              .Append("section{background:#fff;border:1px solid #E3E8F0;border-radius:10px;padding:14px 16px;margin-bottom:14px}")
              .Append("section h2{font-size:13px;margin:0 0 10px;color:#6B7686;font-weight:600;letter-spacing:.4px;text-transform:uppercase}")
              .Append("table{border-collapse:collapse;width:100%;font-size:12px}th{text-align:left;color:#6B7686;font-weight:600;padding:2px 10px 6px 0;border-bottom:1px solid #EDF1F6}")
              .Append("td{padding:4px 10px 4px 0;border-bottom:1px solid #F3F6FA;vertical-align:top}")
              .Append("tr:last-child td{border-bottom:none}.muted{color:#9AA5B4}.num{font-variant-numeric:tabular-nums}")
              .Append("pre{background:#FBFCFE;border:1px solid #EDF1F6;border-radius:6px;padding:10px;font:11px/1.5 Consolas,monospace;overflow-x:auto;margin:0;white-space:pre-wrap}")
              .Append(".s200{color:#2EA86B}.s4xx,.s5xx{color:#E05D4B}")
              .Append(".fix{background:#FFF8E1;border:1px solid #FFE082;border-radius:6px;padding:8px 10px;margin-top:8px;font-size:12px}")
              .Append(".fix a{color:#0F7AC4;text-decoration:none}.fix a:hover{text-decoration:underline}")
              .Append(".chip.bad{border-color:#F0C8C3}")
              .Append("</style><script>")
              .Append("function cancelTask(id){if(!confirm('取消此任务？'))return;")
              .Append("fetch('/api/task/cancel?id='+id,{method:'POST'}).then(r=>r.json()).then(j=>{")
              .Append("alert(j.message||'取消信号已发送');location.reload()})")
              .Append(".catch(e=>alert('取消失败: '+e.message))}")
              .Append("</script></head><body><div class=\"wrap\">");

            sb.Append("<h1><span>TradosToolkit</span> ").Append(E(UiText.T("SP_Title"))).Append("</h1>")
              .Append("<div class=\"sub\">v").Append(E(AsssemblyVersion()))
              .Append(" · http://localhost:").Append(server.Port)
              .Append(" · ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("</div>");

            // ===== 状态磁贴 =====
            sb.Append("<div class=\"chips\">");
            Chip(sb, server.IsListening, "API", server.IsListening ? "localhost:" + server.Port : UiText.T("SP_St_Down"));
            Chip(sb, true, UiText.T("SP_Uptime"), FormatUptime(uptime));
            Chip(sb, RequestTracker.Total > 0, UiText.T("SP_Requests"), RequestTracker.Total.ToString("N0"));
            Chip(sb, TaskRegistry.RunningCount > 0, UiText.T("SP_Running"),
                 TaskRegistry.RunningCount.ToString());
            if (config != null)
            {
                var tmOk = !string.IsNullOrEmpty(config.TmUrl);
                Chip(sb, tmOk, "TM", tmOk ? Shorten(config.TmUrl, 42) : UiText.T("SP_St_Unconfigured"));
                var llmOk = Safe(() => LlmChatClient.IsReady()) as bool? ?? false;
                Chip(sb, llmOk, "LLM", llmOk ? Shorten(config.LlmModel + " @ " + config.LlmBaseUrl, 42) : UiText.T("SP_St_Unconfigured"));
                Chip(sb, config.SegmentDedup, UiText.T("SP_Dedup"), OnOff(config.SegmentDedup));
                Chip(sb, config.LlmDiskCacheEnabled, UiText.T("SP_Cache"), OnOff(config.LlmDiskCacheEnabled));
            }
            sb.Append("</div>");

            // ===== 诊断与修复提示 =====
            AppendDiagnostics(sb, config);

            // ===== 后台任务 =====
            Section(sb, UiText.T("SP_Tasks"), () =>
            {
                var tasks = Safe(() => TaskRegistry.Snapshot(null)) as List<Dictionary<string, object>>;
                if (tasks == null || tasks.Count == 0) { Empty(sb); return; }
                sb.Append("<table><tr><th>").Append(E(UiText.T("SP_Col_Id"))).Append("</th><th>")
                  .Append(E(UiText.T("SP_Col_Task"))).Append("</th><th>").Append(E(UiText.T("SP_Col_Status"))).Append("</th><th>")
                  .Append(E(UiText.T("SP_Col_Started"))).Append("</th><th>").Append(E(UiText.T("SP_Col_Elapsed"))).Append("</th><th></th></tr>");
                foreach (var t in tasks.Take(10))
                {
                    var status = Str(t, "status");
                    var isRunning = status == "running";
                    var taskId = Str(t, "id");
                    sb.Append("<tr><td class=\"muted\">").Append(E(taskId))
                      .Append("</td><td>").Append(E(Str(t, "task")))
                      .Append("</td><td>").Append(E(status))
                      .Append("</td><td class=\"num\">").Append(E(Str(t, "startedAt") != null ? DateTime.Parse(Str(t, "startedAt")).ToString("HH:mm:ss") : ""))
                      .Append("</td><td class=\"num\">").Append(E(Str(t, "elapsedMs")))
                      .Append("</td><td>").Append(
                          isRunning
                              ? "<a href=\"#\" style=\"color:#E05D4B;font-weight:600\" onclick=\"cancelTask('" + E(taskId) + "');return false\">✗ 取消</a>"
                              : "<span class=\"muted\">—</span>")
                      .Append("</td></tr>");
                }
                sb.Append("</table>");
            });

            // ===== 最近请求 =====
            Section(sb, UiText.T("SP_RecentRequests"), () =>
            {
                var reqs = Safe(() => RequestTracker.Snapshot()) as List<Dictionary<string, object>>;
                if (reqs == null || reqs.Count == 0) { Empty(sb); return; }
                sb.Append("<table><tr><th>").Append(E(UiText.T("SP_Col_Time"))).Append("</th><th>")
                  .Append(E(UiText.T("SP_Col_Method"))).Append("</th><th>").Append(E(UiText.T("SP_Col_Path"))).Append("</th><th>")
                  .Append(E(UiText.T("SP_Col_Status"))).Append("</th><th>").Append(E(UiText.T("SP_Col_Ms"))).Append("</th></tr>");
                foreach (var r in reqs.Take(20))
                {
                    var status = Str(r, "status");
                    var cls = status.StartsWith("2") ? "s200" : "s4xx";
                    sb.Append("<tr><td class=\"muted num\">").Append(E(Str(r, "time")))
                      .Append("</td><td>").Append(E(Str(r, "method")))
                      .Append("</td><td>").Append(E(Str(r, "path")))
                      .Append("</td><td class=\"").Append(cls).Append(" num\">").Append(E(status))
                      .Append("</td><td class=\"num\">").Append(E(Str(r, "ms")))
                      .Append("</td></tr>");
                }
                sb.Append("</table>");
            });

            // ===== 项目 =====
            Section(sb, UiText.T("SP_Projects"), () =>
            {
                var projects = ProjectApi.SafeProjects();
                if (projects == null) { sb.Append("<div class=\"muted\">").Append(E(UiText.T("SP_Busy"))).Append("</div>"); return; }
                if (projects.Count == 0) { Empty(sb); return; }
                sb.Append("<table><tr><th>").Append(E(UiText.T("SP_Col_Project"))).Append("</th><th>")
                  .Append(E(UiText.T("SP_Col_Langs"))).Append("</th><th>").Append(E(UiText.T("SP_Col_Status"))).Append("</th></tr>");
                foreach (var p in projects.Take(15))
                {
                    var langs = (p.TryGetValue("targetLangs", out var tl) ? tl as IEnumerable<string> : null);
                    sb.Append("<tr><td>").Append(E(p.TryGetValue("name", out var n) ? n as string : ""))
                      .Append("</td><td class=\"muted\">")
                      .Append(E(p.TryGetValue("sourceLang", out var sl) ? sl as string : "")).Append(" → ")
                      .Append(E(langs == null ? "-" : string.Join(",", langs)))
                      .Append("</td><td>").Append(E(p.TryGetValue("isCompleted", out var ic) && ic is bool b && b ? "done" : "wip"))
                      .Append("</td></tr>");
                }
                sb.Append("</table>");
            });

            // ===== 日志尾部 =====
            Section(sb, UiText.T("SP_Log"), () =>
            {
                var tail = Safe(LogTail) as string;
                sb.Append("<pre>").Append(E(string.IsNullOrEmpty(tail) ? UiText.T("SP_None") : tail)).Append("</pre>");
            });

            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// 诊断与修复区块：对未配置/不可达的关键依赖给出可点击修复提示。
        /// 仅在有异常项时输出，全部正常时不占版面。
        /// </summary>
        private static void AppendDiagnostics(StringBuilder sb, ToolkitConfig config)
        {
            var fixes = new List<string[]>(); // { 标题, 问题描述, 修复建议 }
            if (config != null)
            {
                if (string.IsNullOrEmpty(config.TmUrl))
                    fixes.Add(new[] { "TM 服务", "tmUrl 未配置", "在工作台 → 连接页签填写 TM 服务地址，或直接编辑 <a href=\"" + E(ConfigFileUrl()) + "\">config.json</a>" });
                if (string.IsNullOrEmpty(config.LlmBaseUrl) || string.IsNullOrEmpty(config.ApiKey))
                    fixes.Add(new[] { "LLM 网关", "llmBaseUrl 或 apiKey 未配置", "编辑 <a href=\"" + E(ConfigFileUrl()) + "\">config.json</a> 填写 LLM 网关地址与密钥（apiKey 以 DPAPI 加密存储）" });
                else if (!(Safe(() => LlmChatClient.IsReady()) as bool? ?? false))
                    fixes.Add(new[] { "LLM 网关", "网关未就绪", "确认 llmBaseUrl 可达（GET <code>" + E(config.LlmBaseUrl.TrimEnd('/') + "/models") + "</code>），密钥有效；或重启 Studio" });
                if (string.IsNullOrEmpty(config.TermBaseUrl))
                    fixes.Add(new[] { "术语服务", "termBaseUrl 未配置", "若使用线上术语服务，编辑 <a href=\"" + E(ConfigFileUrl()) + "\">config.json</a> 填写；否则可忽略" });
            }
            if (!ToolkitApiServer.Instance.IsListening)
                fixes.Add(new[] { "API 服务", "未监听", "检查端口 53902 是否被占用，或重启 Studio 让插件重新注册" });

            if (fixes.Count == 0) return;

            sb.Append("<section><h2>诊断与修复</h2>");
            foreach (var f in fixes)
            {
                sb.Append("<div class=\"fix\"><b>").Append(E(f[0])).Append("：</b>")
                  .Append(E(f[1]))
                  .Append(" — ").Append(f[2]) // f[2] 含已编码的 HTML 链接
                  .Append("</div>");
            }
            sb.Append("<div class=\"fix\"><a href=\"file:///").Append(E(ToolkitLog.FolderPath).Replace("\\", "/"))
              .Append("\">打开日志目录</a>（<code>").Append(E(ToolkitLog.FolderPath)).Append("</code>）</div>");
            sb.Append("</section>");
        }

        private static string ConfigFileUrl()
        {
            var p = ToolkitConfig.ConfigFilePath;
            if (p.Length > 0 && char.IsUpper(p[0]) && p[1] == ':') p = p.Insert(1, "/"); // C:\ → C:/
            return "file://" + p;
        }

        private static void Section(StringBuilder sb, string title, System.Action body)
        {
            sb.Append("<section><h2>").Append(E(title)).Append("</h2>");
            try { body(); } catch (Exception e) { sb.Append("<div class=\"muted\">").Append(E(e.Message)).Append("</div>"); }
            sb.Append("</section>");
        }

        private static void Chip(StringBuilder sb, bool ok, string label, string value)
        {
            sb.Append("<div class=\"chip\"><span class=\"dot ").Append(ok ? "ok" : "bad").Append("\"></span>")
              .Append(E(label)).Append(" <b>").Append(E(value)).Append("</b></div>");
        }

        private static void Empty(StringBuilder sb)
            => sb.Append("<div class=\"muted\">").Append(E(UiText.T("SP_None"))).Append("</div>");

        private static string OnOff(bool on) => UiText.T(on ? "SP_On" : "SP_Off");

        private static string AsssemblyVersion()
        {
            try { return typeof(StatusPage).Assembly.GetName().Version.ToString(3); }
            catch { return "?"; }
        }

        private static string FormatUptime(TimeSpan t)
            => t.TotalDays >= 1 ? (int)t.TotalDays + "d " + t.Hours + "h" : t.Hours + "h " + t.Minutes + "m " + t.Seconds + "s";

        private static string LogTail()
        {
            var file = ToolkitLog.LogFile;
            if (!File.Exists(file)) return null;
            string[] lines;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var size = (int)Math.Min(fs.Length, 64 * 1024);
                fs.Seek(-size, SeekOrigin.End);
                using (var reader = new StreamReader(fs))
                {
                    if (size < fs.Length) reader.ReadLine(); // 可能被截断的首行丢弃
                    lines = reader.ReadToEnd().Split('\n');
                }
            }
            var kept = lines.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (kept.Count > 30) kept.RemoveRange(0, kept.Count - 30); // net48 无 TakeLast
            return string.Join("\n", kept);
        }

        private static object Safe(Func<object> probe)
        {
            try { return probe(); } catch { return null; }
        }

        private static string Str(Dictionary<string, object> map, string key)
            => map.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) : string.Empty;

        private static string Shorten(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private static string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
    }
}
