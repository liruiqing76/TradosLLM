using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.EditorPanel
{
    /// <summary>
    /// 编辑器右侧"LLM 更正助手"面板视图：展示当前段、多轮对话、写回译文。
    /// 事件（发请求/写回）冒泡给 LlmPanelController，由它操作 Studio 文档对象。
    /// Studio15 的 ViewPart 只收 WinForms Control，宿主用 ElementHost 包这个 WPF 控件。
    /// </summary>
    public partial class LlmPanelView : UserControl
    {
        public class Bubble
        {
            public bool IsUser { get; set; }
            public string Text { get; set; }
            public string Time { get; set; }
            public HorizontalAlignment Align => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            public Brush BubbleBrush => IsUser ? UserBrush : AssistantBrush;
            public Visibility ApplyVisibility => IsUser ? Visibility.Collapsed : Visibility.Visible;

            private static readonly Brush UserBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xFF)));
            private static readonly Brush AssistantBrush =
                Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)));

            private static Brush Frozen(Brush brush)
            {
                brush.Freeze();
                return brush;
            }
        }

        /// <summary>点"写入当前段"：(提取后的修订译文, 是否标记为译文, 是否连续润色)。</summary>
        public event Action<string, bool, bool> ApplyRequested;

        private readonly ObservableCollection<Bubble> _bubbles = new ObservableCollection<Bubble>();
        private readonly List<ChatTurn> _history = new List<ChatTurn>();

        private string _segmentId;
        private string _source = string.Empty;
        private string _target = string.Empty;
        private string _targetLang;
        private string _prevSource;
        private string _prevTarget;
        private string _nextSource;
        private bool _busy;
        private CancellationTokenSource _cts;
        private readonly DispatcherTimer _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private DateTime _busySince;

        public LlmPanelView()
        {
            ToolkitLog.Info("LlmPanelView ctor");
            InitializeComponent();
            ChatList.ItemsSource = _bubbles;
            _busyTimer.Tick += (s, e) => RefreshReady();
            RefreshReady();
        }

        /// <summary>控制器在每次段变化/文档切换时推最新段信息（含前后邻段上下文）；换段自动清空对话。</summary>
        public void ShowSegment(string segmentId, string source, string target,
                                string confirmationLevel, bool locked, string targetLang,
                                string prevSource, string prevTarget, string nextSource)
        {
            var changed = !string.Equals(segmentId, _segmentId, StringComparison.Ordinal);
            _segmentId = segmentId;
            _source = source ?? string.Empty;
            _target = target ?? string.Empty;
            _targetLang = targetLang;
            _prevSource = prevSource;
            _prevTarget = prevTarget;
            _nextSource = nextSource;

            SourceText.Text = "源: " + (_source.Length > 400 ? _source.Substring(0, 400) + "…" : _source);
            TargetText.Text = "译: " + (_target.Length > 400 ? _target.Substring(0, 400) + "…" : _target);
            StatusChip.Text = confirmationLevel + (locked ? " · 已锁定" : string.Empty);

            if (changed)
            {
                if (_busy) _cts?.Cancel();
                _bubbles.Clear();
                _history.Clear();
            }
            RefreshReady();
        }

        public void ShowNoSegment(string reason)
        {
            _segmentId = null;
            _prevSource = _prevTarget = _nextSource = null;
            SourceText.Text = reason;
            TargetText.Text = string.Empty;
            StatusChip.Text = string.Empty;
            _bubbles.Clear();
            _history.Clear();
        }

        /// <summary>LLM 未配置或无活动段时禁用发送与快捷操作并给出提示；等待回复时显示秒数并允许取消。</summary>
        private void RefreshReady()
        {
            var hasSegment = _segmentId != null;
            var ready = hasSegment && LlmChatClient.IsReady();
            InputBox.IsEnabled = ready && !_busy;
            SendButton.IsEnabled = ready;
            SendButton.Content = _busy ? "取消" : "发送";
            QuickActions.IsEnabled = ready && !_busy;

            if (!hasSegment)
            {
                ConfigHint.Text = "打开编辑器中的目标文件后即可使用。";
                ConfigHint.Visibility = Visibility.Visible;
            }
            else if (!ready)
            {
                ConfigHint.Text = "未配置 LLM：编辑 %APPDATA%\\TradosToolkit\\config.json 的 llmBaseUrl/llmModel/apiKey，或在提供程序配置窗口填写。";
                ConfigHint.Visibility = Visibility.Visible;
            }
            else if (_busy)
            {
                var seconds = (int)((DateTime.Now - _busySince).TotalSeconds);
                ConfigHint.Text = "⏳ 等待 LLM 回复… " + seconds + " 秒（网关较慢时可到 1 分钟，点\"取消\"可中止）";
                ConfigHint.Visibility = Visibility.Visible;
            }
            else
            {
                ConfigHint.Visibility = Visibility.Collapsed;
            }
        }

        public void SetBusy(bool busy)
        {
            _busy = busy;
            if (busy)
            {
                _busySince = DateTime.Now;
                _busyTimer.Start();
            }
            else
            {
                _busyTimer.Stop();
            }
            RefreshReady();
        }

        private void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                if (tag == "__BTQA__")
                    RunBackTranslateQa();
                else
                    SendMessage(tag);
            }
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                ToolkitLog.Info("面板用户取消等待中的请求");
                _cts?.Cancel();
                return;
            }
            var text = InputBox.Text.Trim();
            if (!string.IsNullOrEmpty(text))
                SendMessage(text);
        }

        private void InputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter &&
                System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Shift)
            {
                e.Handled = true;
                Send_Click(sender, e);
            }
        }

        private async void SendMessage(string userText)
        {
            try
            {
                if (_busy || _segmentId == null || !LlmChatClient.IsReady())
                    return;

                InputBox.Clear();
                AddBubble(new Bubble { IsUser = true, Text = userText, Time = Now() });
                _history.Add(new ChatTurn { Role = "user", Content = userText });
                SetBusy(true);
                _cts = new CancellationTokenSource();
                try
                {
                    var reply = await LlmChatClient.ChatAsync(_source, _target, _targetLang, _history, userText,
                        _prevSource, _prevTarget, _nextSource, _cts.Token);
                    _history.Add(new ChatTurn { Role = "assistant", Content = reply });
                    AddBubble(new Bubble { IsUser = false, Text = reply, Time = Now() });
                }
                catch (OperationCanceledException)
                {
                    ToolkitLog.Info("面板对话已取消: " + userText);
                    AddBubble(new Bubble { IsUser = false, Text = "已取消等待。", Time = Now() });
                    _history.RemoveAt(_history.Count - 1);
                }
                catch (Exception ex)
                {
                    ToolkitLog.Error("面板对话失败", ex);
                    AddBubble(new Bubble { IsUser = false, Text = "⚠ " + ex.Message, Time = Now() });
                    _history.RemoveAt(_history.Count - 1);
                }
                finally
                {
                    _cts.Dispose();
                    _cts = null;
                    SetBusy(false);
                }
            }
            catch (Exception e) { ToolkitLog.Error("LlmPanelView.SendMessage 异常", e); }
        }

        private void AddBubble(Bubble bubble)
        {
            _bubbles.Add(bubble);
            ChatScroll.ScrollToEnd();
        }

        private void ApplyBubble_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is Bubble bubble && !bubble.IsUser)
            {
                var revised = LlmChatClient.ExtractRevised(bubble.Text);
                ToolkitLog.Info("面板请求写回: 提取长度=" + (revised?.Length ?? 0));
                ApplyRequested?.Invoke(revised, MarkTranslatedCheck.IsChecked == true, ContinueNextCheck.IsChecked == true);
            }
        }

        /// <summary>
        /// 当前段回译质检：把译文回译成源语 → 与原文比对 → 给出判定。
        /// 结果以气泡形式展示在对话区，不写回译文（仅诊断）。
        /// </summary>
        private async void RunBackTranslateQa()
        {
            if (_busy || _segmentId == null || !LlmChatClient.IsReady())
                return;
            if (string.IsNullOrWhiteSpace(_target))
            {
                AddBubble(new Bubble { IsUser = false, Text = "当前段没有译文，无法回译质检。", Time = Now() });
                return;
            }

            SetBusy(true);
            _cts = new CancellationTokenSource();
            AddBubble(new Bubble { IsUser = true, Text = "回译质检", Time = Now() });
            AddBubble(new Bubble { IsUser = false, Text = "正在回译…", Time = Now() });

            try
            {
                // 阶段1：回译 target → source
                var backPrompt = "把以下译文准确回译成源语言，只返回回译文本，不要任何解释。\n"
                    + "忠实还原语义，不润色不补充，保持数字和占位符原样。\n译文：" + _target;
                var back = await LlmChatClient.ChatAsync(_source, _target, _targetLang,
                    new List<ChatTurn>(), backPrompt, null, null, null, _cts.Token);

                // 移除可能被注入的 <<< >>> 包裹
                back = back.Replace("<<<", "").Replace(">>>", "").Trim();

                // 阶段2：判官比对 source vs back
                var judgePrompt = "你是翻译质检。下面给出原文和回译文本（译文被独立回译成源语言后的结果）。\n"
                    + "原理：忠实译文的回译应与原文语义一致；差异越大越可能漏译/增译/错译。\n"
                    + "原文：" + _source + "\n回译：" + back + "\n\n"
                    + "请判定并给出：\n"
                    + "1) verdict: ok(语义一致) / amber(有偏差需复核) / red(语义不符)\n"
                    + "2) score: 0-100 语义保真度\n"
                    + "3) reason: 一句话说明问题\n"
                    + "4) suggestion: 如果有问题，给出修正后的译文\n"
                    + "用以下格式回复（不要 markdown 代码块）：\n"
                    + "判定: red/amber/ok\n分数: N\n原因: ...\n建议: ...";

                AddBubble(new Bubble { IsUser = false, Text = "回译完成，正在判定…\n回译：" + back, Time = Now() });

                var judge = await LlmChatClient.ChatAsync(_source, _target, _targetLang,
                    new List<ChatTurn>(), judgePrompt, null, null, null, _cts.Token);

                // 格式化展示
                var sb = new StringBuilder();
                sb.AppendLine("回译质检结果");
                sb.AppendLine("回译: " + back);
                sb.AppendLine();
                sb.AppendLine(judge);
                AddBubble(new Bubble { IsUser = false, Text = sb.ToString(), Time = Now() });
            }
            catch (OperationCanceledException)
            {
                AddBubble(new Bubble { IsUser = false, Text = "已取消。", Time = Now() });
            }
            catch (Exception ex)
            {
                AddBubble(new Bubble { IsUser = false, Text = "回译质检失败: " + ex.Message, Time = Now() });
                ToolkitLog.Error("LlmPanelView.RunBackTranslateQa 异常", ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static string Now() => DateTime.Now.ToString("HH:mm:ss");
    }
}
