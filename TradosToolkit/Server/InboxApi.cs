using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TradosToolkit.Inbox;

namespace TradosToolkit.Server
{
    /// <summary>
    /// 收件箱（监控目录）外部 API：把既有的「拖入即产出三件套」能力暴露成 HTTP 端点。
    ///
    /// 全部经 InboxWatcher 的单线程队列执行（Studio 项目自动化必须串行），异步任务式：
    /// 投递立即返回 202 + jobId，客户端轮询 /api/inbox/job?id= 拿分步进度与产出路径。
    /// 仅本机可访问（HttpListener 只监听 localhost，且需 X-Api-Key 令牌）。
    ///
    /// 端点：
    ///   GET  /api/inbox             监视与任务总览
    ///   POST /api/inbox/start       开始监视（可顺带落盘 watchFolder/语向/格式等）
    ///   POST /api/inbox/stop        停止监视
    ///   POST /api/inbox/process     投递单个源文件，产出三件套（返回 202 + id）
    ///   POST /api/inbox/process-all 处理监视目录里已有的文件（相当于界面「立即处理」）
    ///   GET  /api/inbox/jobs        任务列表（轻量，不含步骤/日志）
    ///   GET  /api/inbox/job?id=     单任务详情（含分步状态与日志）
    /// </summary>
    public static class InboxApi
    {
        private const int JobCap = 100;

        private static readonly object Gate = new object();
        private static readonly List<InboxJobRecord> Jobs = new List<InboxJobRecord>();

        /// <summary>投递时预登记的「路径 → 任务记录」映射，待 JobCreated 触发时把真实任务绑上去。</summary>
        private static readonly Dictionary<string, string> PendingByPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static bool _inited;

        /// <summary>订阅监视器的建任务事件，让所有任务（含拖进目录的）都能被 /api/inbox/jobs 查到。进程内只需一次。</summary>
        public static void EnsureInit()
        {
            lock (Gate)
            {
                if (_inited) return;
                _inited = true;
                try { InboxWatcher.Instance.JobCreated += OnJobCreated; }
                catch { /* 订阅失败不影响其余端点 */ }
            }
        }

        public static ApiResult Handle(string method, string path, Dictionary<string, string> query, string body)
        {
            EnsureInit();
            switch (path)
            {
                case "/api/inbox":
                    return method == "GET" ? Status() : ApiResult.Json(405, ProjectApi.Error("inbox 状态端点需 GET"));
                case "/api/inbox/start":
                    return method == "POST" ? Start(body) : ApiResult.Json(405, ProjectApi.Error("inbox/start 需 POST"));
                case "/api/inbox/stop":
                    return method == "POST" ? Stop() : ApiResult.Json(405, ProjectApi.Error("inbox/stop 需 POST"));
                case "/api/inbox/process":
                    return method == "POST" ? Process(body) : ApiResult.Json(405, ProjectApi.Error("inbox/process 需 POST"));
                case "/api/inbox/process-all":
                    return method == "POST" ? ProcessAll() : ApiResult.Json(405, ProjectApi.Error("inbox/process-all 需 POST"));
                case "/api/inbox/jobs":
                    return method == "GET" ? ListJobs(query) : ApiResult.Json(405, ProjectApi.Error("inbox/jobs 需 GET"));
                case "/api/inbox/job":
                    return method == "GET" ? OneJob(query) : ApiResult.Json(405, ProjectApi.Error("inbox/job 需 GET"));
                default:
                    return ApiResult.Json(404, ProjectApi.Error("未知收件箱端点 " + path));
            }
        }

        // ==================== 总览 / 启停 ====================

        private static ApiResult Status()
        {
            var watcher = InboxWatcher.Instance;
            var cfg = ToolkitConfig.Load();
            int total, active;
            lock (Gate)
            {
                total = Jobs.Count;
                active = Jobs.Count(IsActive);
            }
            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "running", watcher.IsRunning },
                { "watchingFolder", watcher.WatchingFolder },
                { "watchFolder", cfg.InboxWatchFolder },
                { "outputFolder", cfg.InboxOutputFolder },
                { "projectRoot", cfg.InboxProjectRoot },
                { "tmScanDirectory", cfg.TmScanDirectory },
                { "sourceLang", cfg.InboxSourceLang },
                { "targetLang", cfg.InboxTargetLang },
                { "reportFormat", cfg.InboxReportFormat },
                { "autoStart", cfg.InboxAutoStart },
                { "jobsTotal", total },
                { "jobsActive", active },
            });
        }

        private static ApiResult Start(string body)
        {
            var req = ProjectApi.ParseBody(body);
            var watch = ProjectApi.Str(req, "watchFolder");
            var output = ProjectApi.Str(req, "outputFolder");
            var projectRoot = ProjectApi.Str(req, "projectRoot");
            var src = ProjectApi.Str(req, "sourceLang");
            var tgt = ProjectApi.Str(req, "targetLang");
            var fmt = ProjectApi.Str(req, "reportFormat");
            var tm = ProjectApi.Str(req, "tmScanDirectory");
            bool? auto = req.ContainsKey("autoStart") ? ProjectApi.Bool(req, "autoStart") : (bool?)null;

            if (watch != null || output != null || projectRoot != null || src != null
                || tgt != null || fmt != null || tm != null || auto != null)
            {
                try
                {
                    ToolkitConfig.Save(tmScanDirectory: tm, inboxWatchFolder: watch, inboxOutputFolder: output,
                        inboxProjectRoot: projectRoot, inboxSourceLang: src, inboxTargetLang: tgt,
                        inboxReportFormat: fmt, inboxAutoStart: auto);
                }
                catch (Exception e)
                {
                    return ApiResult.Json(500, ProjectApi.Error("保存收件箱配置失败：" + e.Message));
                }
            }

            try
            {
                InboxWatcher.Instance.Start(ToolkitConfig.Load());
            }
            catch (Exception e)
            {
                return ApiResult.Json(412, ProjectApi.Error(e.Message));
            }
            return Status();
        }

        private static ApiResult Stop()
        {
            InboxWatcher.Instance.Stop();
            return Status();
        }

        // ==================== 投递 / 处理已有 ====================

        private static ApiResult Process(string body)
        {
            var req = ProjectApi.ParseBody(body);
            var file = ProjectApi.Str(req, "file");
            if (string.IsNullOrWhiteSpace(file))
                return ApiResult.Json(400, ProjectApi.Error("缺少必填字段 file（待处理源文件的绝对路径）"));

            try { file = Path.GetFullPath(file.Trim()); }
            catch (Exception e) { return ApiResult.Json(400, ProjectApi.Error("file 路径无效：" + e.Message)); }
            if (!File.Exists(file))
                return ApiResult.Json(404, ProjectApi.Error("源文件不存在：" + file));

            var watcher = InboxWatcher.Instance;
            if (!watcher.IsRunning)
            {
                try { watcher.Start(ToolkitConfig.Load()); }
                catch (Exception e)
                {
                    return ApiResult.Json(412, ProjectApi.Error(
                        "监视未启动且无法自动启动：" + e.Message + "（请先设置监视目录，或调用 POST /api/inbox/start）"));
                }
            }

            var overrideCfg = BuildOverride(req);

            InboxJobRecord rec;
            lock (Gate)
            {
                rec = new InboxJobRecord { Id = NewId(), FilePath = file };
                Jobs.Add(rec);
                PendingByPath[file] = rec.Id;
            }

            if (!watcher.Enqueue(file, overrideCfg))
            {
                lock (Gate)
                {
                    PendingByPath.Remove(file);
                    Jobs.Remove(rec);
                }
                return ApiResult.Json(409, ProjectApi.Error("该文件已在处理队列中：" + file));
            }

            return ApiResult.Json(202, new Dictionary<string, object>
            {
                { "id", rec.Id },
                { "file", file },
                { "status", "queued" },
                { "poll", "/api/inbox/job?id=" + rec.Id },
            });
        }

        private static ApiResult ProcessAll()
        {
            var watcher = InboxWatcher.Instance;
            if (!watcher.IsRunning)
            {
                try { watcher.Start(ToolkitConfig.Load()); }
                catch (Exception e)
                {
                    return ApiResult.Json(412, ProjectApi.Error(
                        "监视未启动且无法自动启动：" + e.Message + "（请先设置监视目录，或调用 POST /api/inbox/start）"));
                }
            }

            var n = watcher.EnqueueExisting();
            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "enqueued", n },
                { "watchingFolder", watcher.WatchingFolder },
            });
        }

        // ==================== 查询 ====================

        private static ApiResult ListJobs(Dictionary<string, string> query)
        {
            var limit = 50;
            string raw;
            if (query.TryGetValue("limit", out raw))
            {
                int v;
                if (int.TryParse(raw, out v)) limit = Math.Max(1, Math.Min(JobCap, v));
            }

            List<Dictionary<string, object>> list;
            lock (Gate)
                list = Jobs.OrderByDescending(r => r.SubmittedAt).Take(limit)
                           .Select(r => Summarize(r, false)).ToList();
            return ApiResult.Json(200, list);
        }

        private static ApiResult OneJob(Dictionary<string, string> query)
        {
            string id;
            if (!query.TryGetValue("id", out id) || string.IsNullOrEmpty(id))
                return ApiResult.Json(400, ProjectApi.Error("缺少 query 参数 id"));

            lock (Gate)
            {
                var rec = Jobs.FirstOrDefault(r => r.Id == id);
                if (rec == null) return ApiResult.Json(404, ProjectApi.Error("无此收件箱任务 " + id));
                return ApiResult.Json(200, Summarize(rec, true));
            }
        }

        // ==================== 注册表 ====================

        private static void OnJobCreated(InboxJob job)
        {
            if (job == null) return;
            lock (Gate)
            {
                InboxJobRecord rec = null;
                string recId;
                if (PendingByPath.TryGetValue(job.FilePath ?? string.Empty, out recId))
                {
                    PendingByPath.Remove(job.FilePath ?? string.Empty);
                    rec = Jobs.FirstOrDefault(r => r.Id == recId);
                }
                if (rec == null)
                {
                    // 拖进监视目录触发的任务：直接按任务自带 Id 登记
                    rec = new InboxJobRecord { Id = job.Id, FilePath = job.FilePath };
                    Jobs.Add(rec);
                }
                rec.Job = job;
                TrimLocked();
            }
        }

        private static void TrimLocked()
        {
            if (Jobs.Count <= JobCap) return;
            var victims = Jobs.Where(r => !IsActive(r))
                              .OrderBy(r => r.SubmittedAt)
                              .Take(Jobs.Count - JobCap)
                              .ToList();
            foreach (var v in victims) Jobs.Remove(v);
        }

        private static bool IsActive(InboxJobRecord rec)
        {
            var status = rec.Job == null ? "queued" : rec.Job.Status;
            return status == "running" || status == "queued";
        }

        private static Dictionary<string, object> Summarize(InboxJobRecord rec, bool includeDetail)
        {
            var job = rec.Job;
            var map = new Dictionary<string, object>
            {
                { "id", rec.Id },
                { "file", job != null ? job.FilePath : rec.FilePath },
                { "fileName", job != null ? job.FileName : Path.GetFileName(rec.FilePath ?? string.Empty) },
                { "status", job != null ? job.Status : "queued" },
                { "statusText", job != null ? job.StatusText : "排队中" },
                { "message", job != null ? job.Message : "等待处理…" },
                { "submittedAt", rec.SubmittedAt },
                { "createdAt", job != null ? (object)job.CreatedAt : null },
                { "finishedAt", job != null ? (object)job.FinishedAt : null },
                { "elapsedMs", job == null ? 0.0 : ((job.FinishedAt ?? DateTime.Now) - job.CreatedAt).TotalMilliseconds },
                { "projectPath", job != null ? job.ProjectPath : null },
                { "reportPath", job != null ? job.ReportPath : null },
                { "packagePath", job != null ? job.PackagePath : null },
                { "tmPath", job != null ? job.TmPath : null },
                { "jobFolder", job != null ? job.JobFolder : null },
                { "outputSummary", job != null ? job.OutputSummary : "—" },
            };

            if (job != null)
            {
                var steps = new List<Dictionary<string, object>>();
                try
                {
                    // Steps 构造后不再增删，结构稳定；仅单项状态字符串在变，跨线程读取安全
                    foreach (var s in job.Steps)
                        steps.Add(new Dictionary<string, object>
                        {
                            { "index", s.Index },
                            { "title", s.Title },
                            { "status", s.Status },
                            { "detail", s.Detail },
                        });
                }
                catch { /* 极端竞态下丢弃步骤明细即可 */ }
                map["steps"] = steps;
                if (includeDetail) map["log"] = job.LogText;
            }
            return map;
        }

        // ==================== 辅助 ====================

        /// <summary>把请求里给出的语向/报告格式/产出目录等，套到一份配置副本上，作为「这一个任务」的独立配置。</summary>
        private static ToolkitConfig BuildOverride(Dictionary<string, object> req)
        {
            var src = ProjectApi.Str(req, "sourceLang");
            var tgt = ProjectApi.Str(req, "targetLang");
            var fmt = ProjectApi.Str(req, "reportFormat");
            var output = ProjectApi.Str(req, "outputFolder");
            var projectRoot = ProjectApi.Str(req, "projectRoot");
            var tm = ProjectApi.Str(req, "tmScanDirectory");

            if (src == null && tgt == null && fmt == null && output == null && projectRoot == null && tm == null)
                return null;

            var cfg = ToolkitConfig.Load();
            if (!string.IsNullOrWhiteSpace(src)) cfg.InboxSourceLang = src.Trim();
            if (!string.IsNullOrWhiteSpace(tgt)) cfg.InboxTargetLang = tgt.Trim();
            if (!string.IsNullOrWhiteSpace(fmt)) cfg.InboxReportFormat = fmt.Trim().ToLowerInvariant();
            if (output != null) cfg.InboxOutputFolder = output.Trim();
            if (projectRoot != null) cfg.InboxProjectRoot = projectRoot.Trim();
            if (tm != null) cfg.TmScanDirectory = tm.Trim();
            return cfg;
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private sealed class InboxJobRecord
        {
            public string Id;
            public string FilePath;
            public DateTime SubmittedAt = DateTime.Now;
            public InboxJob Job;   // 绑定后指向真实任务；未绑定时为 null
        }
    }
}
