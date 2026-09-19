using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        /// <summary>点"写入当前段"：(提取后的修订译文, 是否标记为译文)。</summary>
        public event Action<string, bool> ApplyRequested;

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

        public LlmPanelView()
        {
            ToolkitLog.Info("LlmPanelView ctor");
            InitializeComponent();
            ChatList.ItemsSource = _bubbles;
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

        /// <summary>LLM 未配置或无活动段时禁用发送与快捷操作并给出提示。</summary>
        private void RefreshReady()
        {
            var hasSegment = _segmentId != null;
            var ready = hasSegment && LlmChatClient.IsReady();
            InputBox.IsEnabled = ready;
            SendButton.IsEnabled = ready && !_busy;
            QuickActions.IsEnabled = ready && !_busy;

            if (!hasSegment)
            {
                ConfigHint.Text = "打开编辑器中的目标文件后即可使用。";
                ConfigHint.Visibility = Visibility.Visible;
            }
            else if (!LlmChatClient.IsReady())
            {
                ConfigHint.Text = "未配置 LLM：编辑 %APPDATA%\\TradosToolkit\\config.json 的 llmBaseUrl/llmModel/apiKey，或在提供程序配置窗口填写。";
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
            RefreshReady();
        }

        private void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string prompt)
                SendMessage(prompt);
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
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
            if (_busy || _segmentId == null || !LlmChatClient.IsReady())
                return;

            InputBox.Clear();
            AddBubble(new Bubble { IsUser = true, Text = userText, Time = Now() });
            _history.Add(new ChatTurn { Role = "user", Content = userText });
            SetBusy(true);
            try
            {
                var reply = await LlmChatClient.ChatAsync(_source, _target, _targetLang, _history, userText,
                    _prevSource, _prevTarget, _nextSource);
                _history.Add(new ChatTurn { Role = "assistant", Content = reply });
                AddBubble(new Bubble { IsUser = false, Text = reply, Time = Now() });
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("面板对话失败", ex);
                AddBubble(new Bubble { IsUser = false, Text = "⚠ " + ex.Message, Time = Now() });
                _history.RemoveAt(_history.Count - 1);
            }
            finally
            {
                SetBusy(false);
            }
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
                ApplyRequested?.Invoke(revised, MarkTranslatedCheck.IsChecked == true);
            }
        }

        private static string Now() => DateTime.Now.ToString("HH:mm:ss");
    }
}
