using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace TradosToolkit.Inbox
{
    /// <summary>流程监控里的一步：pending / running / done / error / skipped。</summary>
    public class InboxStep : INotifyPropertyChanged
    {
        public InboxStep(int index, string title)
        {
            Index = index;
            Title = title;
        }

        public int Index { get; private set; }
        public string Title { get; private set; }

        private string _status = "pending";
        public string Status
        {
            get { return _status; }
            set
            {
                if (_status == value) return;
                _status = value;
                Raise("Status");
                Raise("StatusText");
            }
        }

        private string _detail = string.Empty;
        public string Detail
        {
            get { return _detail; }
            set { _detail = value ?? string.Empty; Raise("Detail"); }
        }

        public string StatusText
        {
            get
            {
                switch (_status)
                {
                    case "running": return "◐ 进行中";
                    case "done": return "✓ 完成";
                    case "error": return "✕ 失败";
                    case "skipped": return "– 跳过";
                    default: return "○ 待处理";
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 一次「拖入即产出」的任务：一个源文件从入队到产出三件套的全过程。
    /// 所有变更通过 Post 回到 UI 线程（无界面后台模式则原线程直接更新），可直接绑定到流程监控界面。
    /// </summary>
    public class InboxJob : INotifyPropertyChanged
    {
        /// <summary>UI 线程 Dispatcher：窗口打开时设置，关闭置空 = 无界面后台模式。</summary>
        public static Dispatcher UiDispatcher;

        /// <summary>把更新回到 UI 线程（无 Dispatcher 时同步执行）。</summary>
        public static void Post(System.Action action)
        {
            if (action == null) return;
            var d = UiDispatcher;
            if (d == null) { action(); return; }
            if (d.CheckAccess()) action();
            else { try { d.BeginInvoke(action); } catch { /* 窗口已关，忽略 */ } }
        }

        private readonly StringBuilder _log = new StringBuilder();

        /// <summary>步骤状态的同步副本。Post 走 BeginInvoke 是异步的，工作线程立刻读 Steps[i].Status
        /// 可能拿到过期的 "pending"（导致已完成步骤被误标「未执行」），因此另存一份同步维护的状态。</summary>
        private readonly string[] _stepStatus;

        public InboxJob(string filePath)
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            FilePath = filePath ?? string.Empty;
            FileName = Path.GetFileName(FilePath);
            CreatedAt = DateTime.Now;
            Steps = new ObservableCollection<InboxStep>
            {
                new InboxStep(0, "匹配本地记忆库"),
                new InboxStep(1, "创建项目"),
                new InboxStep(2, "套库预翻译"),
                new InboxStep(3, "分析统计"),
                new InboxStep(4, "生成分析报告"),
                new InboxStep(5, "生成交付包 (.sdlppx)"),
                new InboxStep(6, "导出匹配记忆库"),
            };
            _stepStatus = new string[Steps.Count];
            for (var i = 0; i < _stepStatus.Length; i++) _stepStatus[i] = "pending";
        }

        /// <summary>同步读取某步状态（不受 Post 异步派发影响，供编排逻辑判断用）。</summary>
        public string StepStatus(int index)
        {
            if (index < 0 || index >= _stepStatus.Length) return null;
            return Volatile.Read(ref _stepStatus[index]);
        }

        public string Id { get; private set; }
        public string FilePath { get; private set; }
        public string FileName { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public ObservableCollection<InboxStep> Steps { get; private set; }

        private string _status = "queued";
        public string Status
        {
            get { return _status; }
            private set { _status = value; Raise("Status"); Raise("StatusText"); }
        }

        public string StatusText
        {
            get
            {
                switch (_status)
                {
                    case "running": return "运行中";
                    case "done": return "已完成";
                    case "error": return "失败";
                    default: return "排队中";
                }
            }
        }

        private string _message = "等待处理…";
        public string Message { get { return _message; } set { _message = value ?? string.Empty; Raise("Message"); } }

        private string _projectPath = string.Empty;
        public string ProjectPath { get { return _projectPath; } set { _projectPath = value ?? string.Empty; Raise("ProjectPath"); } }

        private string _reportPath = string.Empty;
        public string ReportPath { get { return _reportPath; } set { _reportPath = value ?? string.Empty; Raise("ReportPath"); Raise("OutputSummary"); } }

        private string _packagePath = string.Empty;
        public string PackagePath { get { return _packagePath; } set { _packagePath = value ?? string.Empty; Raise("PackagePath"); Raise("OutputSummary"); } }

        private string _tmPath = string.Empty;
        public string TmPath { get { return _tmPath; } set { _tmPath = value ?? string.Empty; Raise("TmPath"); Raise("OutputSummary"); } }

        private string _jobFolder = string.Empty;
        public string JobFolder { get { return _jobFolder; } set { _jobFolder = value ?? string.Empty; Raise("JobFolder"); } }

        public DateTime? FinishedAt { get; private set; }

        public string CreatedText => CreatedAt.ToString("HH:mm:ss");

        public string ElapsedText
        {
            get
            {
                var seconds = ((FinishedAt ?? DateTime.Now) - CreatedAt).TotalSeconds;
                if (seconds < 0) seconds = 0;
                return seconds < 60 ? seconds.ToString("0.0") + "s" : TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
            }
        }

        public string OutputSummary
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(_reportPath)) parts.Add("报告");
                if (!string.IsNullOrEmpty(_packagePath)) parts.Add("交付包");
                if (!string.IsNullOrEmpty(_tmPath)) parts.Add("匹配库");
                return parts.Count == 0 ? "—" : string.Join(" + ", parts);
            }
        }

        public string LogText { get { lock (_log) return _log.ToString(); } }

        // ==================== 由监视器 / 编排服务调用（自动 marshal 回 UI） ====================

        public void AppendLog(string line)
        {
            lock (_log) _log.Append(DateTime.Now.ToString("HH:mm:ss")).Append(' ').Append(line).Append('\n');
            Post(() => Raise("LogText"));
        }

        public void BeginStep(int index, string detail)
        {
            SetStepStatus(index, "running");
            Post(() =>
            {
                if (index < 0 || index >= Steps.Count) return;
                var step = Steps[index];
                step.Status = "running";
                if (detail != null) step.Detail = detail;
                Status = "running";
            });
        }

        public void EndStep(int index, bool ok, string detail)
        {
            SetStepStatus(index, ok ? "done" : "error");
            Post(() =>
            {
                if (index < 0 || index >= Steps.Count) return;
                var step = Steps[index];
                step.Status = ok ? "done" : "error";
                if (detail != null) step.Detail = detail;
            });
        }

        public void SkipStep(int index, string detail)
        {
            SetStepStatus(index, "skipped");
            Post(() =>
            {
                if (index < 0 || index >= Steps.Count) return;
                var step = Steps[index];
                step.Status = "skipped";
                if (detail != null) step.Detail = detail;
            });
        }

        /// <summary>同步更新步骤状态副本（Post 派发之前），供工作线程判断用。</summary>
        private void SetStepStatus(int index, string status)
        {
            if (index < 0 || index >= _stepStatus.Length) return;
            Volatile.Write(ref _stepStatus[index], status);
        }

        public void SetStepDetail(int index, string detail)
        {
            Post(() =>
            {
                if (index < 0 || index >= Steps.Count) return;
                Steps[index].Detail = detail ?? string.Empty;
            });
        }

        public void MarkDone(string message)
        {
            FinishedAt = DateTime.Now;
            Post(() =>
            {
                Status = "done";
                Message = message;
                Raise("FinishedAt");
                Raise("ElapsedText");
            });
        }

        public void MarkFailed(string message)
        {
            FinishedAt = DateTime.Now;
            Post(() =>
            {
                Status = "error";
                Message = message;
                Raise("FinishedAt");
                Raise("ElapsedText");
            });
        }

        /// <summary>供界面定时器刷新"耗时"。</summary>
        public void Tick() => Raise("ElapsedText");

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
