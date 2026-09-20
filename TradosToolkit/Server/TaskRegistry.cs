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
            if (includeResult && Result != null) map["result"] = Result.Payload;
            return map;
        }
    }

    /// <summary>
    /// 后台任务注册表：async=1 的 RunAutomaticTask 跑在线程池（内部仍 marshal 回 UI 线程，
    /// 多个任务在 UI 线程天然串行），HTTP 立即返回 202。只保留最近 50 条，优先淘汰已完成项。
    /// </summary>
    public static class TaskRegistry
    {
        private const int Cap = 50;
        private static readonly object Gate = new object();
        private static readonly List<BackgroundTask> Tasks = new List<BackgroundTask>();

        public static BackgroundTask Start(string taskKey, string projectPath, Func<ApiResult> op)
        {
            var st = new BackgroundTask
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Task = taskKey,
                ProjectPath = projectPath,
                StartedAt = DateTime.Now,
            };
            lock (Gate)
            {
                Tasks.Add(st);
                if (Tasks.Count > Cap)
                {
                    var victims = Tasks.Where(t => t.Status != "running")
                        .OrderBy(t => t.StartedAt).Take(Tasks.Count - Cap).ToList();
                    foreach (var v in victims) Tasks.Remove(v);
                }
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ApiLog.Write("task " + st.Id + " (" + taskKey + ") begin");
                try
                {
                    st.Result = op();
                    st.Status = "done";
                    ApiLog.Write("task " + st.Id + " done in " +
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
                }
            });
            return st;
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
    }
}
