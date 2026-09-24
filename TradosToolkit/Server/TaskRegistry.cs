using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TradosToolkit.Server
{
    /// <summary>一个后台批任务的生命周期记录（async=1 模式）。</summary>
    public class BackgroundTask
    {
        public string Id;
        public string Task;
        public string ProjectPath;
        public string Status = "running";   // running | done | error
        public DateTime StartedAt;
        public DateTime? FinishedAt;
        public string Error;
        public Dictionary<string, object> Progress;   // 可选：任务内逐步进度明细（如 pipeline 的每步状态）
        public ApiResult Result;            // 仅完成后有值，列表端点不下发

        public Dictionary<string, object> Summarize(bool includeResult)
        {
            var map = new Dictionary<string, object>
            {
                { "id", Id },
                { "task", Task },
                { "projectPath", ProjectPath },
                { "status", Status },
                { "startedAt", StartedAt },
                { "finishedAt", FinishedAt },
                { "elapsedMs", ((FinishedAt ?? DateTime.Now) - StartedAt).TotalMilliseconds },
            };
            if (Error != null) map["error"] = Error;
            if (Progress != null) map["progress"] = Progress;
            if (includeResult && Result != null) map["result"] = Result.Payload;
            return map;
        }
    }

    /// <summary>
    /// 后台任务注册表：async=1 的 RunAutomaticTask 跑在线程池（内部仍 marshal 回 UI 线程，
    /// 多个任务在 UI 线程天然串行），HTTP 立即返回 202。只保留最近 50 条，优先淘汰已完成项。
    /// 支持取消：Cancel(id) 标记任务为 cancelled，pipeline 循环检查令牌跳过后续步骤。
    /// 注意：Studio 原生自动任务一旦提交不可中断，取消仅在下一个 marshal 回调或 pipeline 步骤间生效。
    /// </summary>
    public static class TaskRegistry
    {
        private const int Cap = 50;
        private static readonly object Gate = new object();
        private static readonly List<BackgroundTask> Tasks = new List<BackgroundTask>();
        private static readonly Dictionary<string, CancellationTokenSource> _cancelSources
            = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        public static BackgroundTask Start(string taskKey, string projectPath, Func<ApiResult> op)
        {
            var st = new BackgroundTask
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Task = taskKey,
                ProjectPath = projectPath,
                StartedAt = DateTime.Now,
            };
            var cts = new CancellationTokenSource();
            lock (Gate)
            {
                Tasks.Add(st);
                _cancelSources[st.Id] = cts;
                if (Tasks.Count > Cap)
                {
                    var victims = Tasks.Where(t => t.Status != "running")
                        .OrderBy(t => t.StartedAt).Take(Tasks.Count - Cap).ToList();
                    foreach (var v in victims)
                    {
                        Tasks.Remove(v);
                        _cancelSources.Remove(v.Id);
                    }
                }
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ApiLog.Write("task " + st.Id + " (" + taskKey + ") begin");
                try
                {
                    st.Result = op();
                    st.Status = cts.IsCancellationRequested ? "cancelled" : "done";
                    ApiLog.Write("task " + st.Id + " " + st.Status + " in " +
                                 ((DateTime.Now - st.StartedAt).TotalMilliseconds).ToString("0") + "ms");
                }
                catch (Exception e)
                {
                    st.Status = "error";
                    st.Error = e.Message;
                    ApiLog.Write("task " + st.Id + " error: " + e.Message);
                }
                finally
                {
                    st.FinishedAt = DateTime.Now;
                    lock (Gate) _cancelSources.Remove(st.Id);
                }
            });
            return st;
        }

        /// <summary>取消一个运行中的后台任务。标记状态为 cancelled，令牌信号触发。
        /// Studio 原生任务不可中断，实际取消在 pipeline 步骤间或下个 marshal 回调时生效。</summary>
        public static bool Cancel(string id)
        {
            lock (Gate)
            {
                if (_cancelSources.TryGetValue(id, out var cts))
                {
                    cts.Cancel();
                    var t = Tasks.FirstOrDefault(x => x.Id == id);
                    if (t != null && t.Status == "running")
                        t.Status = "cancelled";
                    return true;
                }
                return false;
            }
        }

        /// <summary>获取任务关联的取消令牌（pipeline 循环在步骤间检查 IsCancellationRequested）。</summary>
        public static CancellationToken GetCancelToken(string id)
        {
            lock (Gate)
            {
                return _cancelSources.TryGetValue(id, out var cts) ? cts.Token : CancellationToken.None;
            }
        }

        public static List<Dictionary<string, object>> Snapshot(string id)
        {
            lock (Gate)
            {
                if (id != null)
                    return Tasks.Where(t => t.Id == id).Select(t => t.Summarize(true)).ToList();
                return Tasks.AsEnumerable().Reverse().Select(t => t.Summarize(false)).ToList();
            }
        }

        public static int RunningCount
        {
            get { lock (Gate) return Tasks.Count(t => t.Status == "running"); }
        }

        /// <summary>更新某后台任务的逐步进度明细（/api/task 可见）。任务不存在则忽略。</summary>
        public static void SetProgress(string id, Dictionary<string, object> progress)
        {
            lock (Gate)
            {
                var t = Tasks.FirstOrDefault(x => x.Id == id);
                if (t != null) t.Progress = progress;
            }
        }
    }
}
