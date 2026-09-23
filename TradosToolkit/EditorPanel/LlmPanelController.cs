using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Sdl.Core.Globalization;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.EditorPanel
{
    /// <summary>
    /// 编辑器右侧停靠的"LLM 更正助手"ViewPart。
    /// Studio15 实测：GetContentControl() 返回 WinForms Control（用 ElementHost 包 WPF 视图）；
    /// ActiveDocument 是具体 Document 类；读段 GetActiveSegmentPair()，
    /// 写回 Target.Clear/Add + UpdateSegmentPair(+Properties)。
    /// </summary>
    [ViewPart(Id = "TradosToolkit.LlmPanel", Name = "LLM 更正助手",
              Description = "与 LLM 对话更正当前段译文并一键写回")]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Right,
                    Width = 340, MinWidth = 240, Visible = true)]
    public class LlmPanelController : AbstractViewPartController
    {
        private readonly LlmPanelView _view = new LlmPanelView();
        private readonly ElementHost _host;

        private EditorController _editorController;
        private Document _document;
        private bool _targetHasTags;

        public LlmPanelController()
        {
            ToolkitLog.Boot("editor-panel");
            _host = new ElementHost { Dock = System.Windows.Forms.DockStyle.Fill, Child = _view };
            _view.ApplyRequested += OnApplyRequested;
        }

        protected override void Initialize()
        {
            ToolkitLog.Info("LlmPanelController.Initialize");
            _editorController = SdlTradosStudio.Application.GetController<EditorController>();
            _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;
            SetDocument(_editorController.ActiveDocument);
        }

        protected override Control GetContentControl() => _host;

        private void OnActiveDocumentChanged(object sender, DocumentEventArgs e) => SetDocument(e.Document);

        private void SetDocument(Document document)
        {
            if (_document != null)
            {
                _document.ActiveSegmentChanged -= OnSegmentChanged;
                _document.ContentChanged -= OnContentChanged;
                _document.SegmentsConfirmationLevelChanged -= OnSegmentChanged;
                _document.ActiveFilePropertiesChanged -= OnSegmentChanged;
                _document.ActiveFileChanged -= OnActiveFileChanged;
            }
            _document = document;
            if (_document != null)
            {
                _document.ActiveSegmentChanged += OnSegmentChanged;
                _document.ContentChanged += OnContentChanged;
                _document.SegmentsConfirmationLevelChanged += OnSegmentChanged;
                _document.ActiveFilePropertiesChanged += OnSegmentChanged;
                _document.ActiveFileChanged += OnActiveFileChanged;
            }
            PushSegment();
            RemountTerminology();
        }

        /// <summary>
        /// 语向变化（切换文档或切换项目文件）时重挂术语源，
        /// 使术语插入点始终跟随当前打开文件的语向。
        /// </summary>
        private void OnActiveFileChanged(object sender, EventArgs e)
        {
            PushSegment();
            RemountTerminology();
        }

        private void RemountTerminology()
        {
            try
            {
                if (_document?.Project == null) return;
                var cfg = ToolkitConfig.Load();
                var mounted = TerminologySource.ProjectTerminology.Mount(
                    _document.Project as Sdl.ProjectAutomation.FileBased.FileBasedProject,
                    TerminologySource.ProjectTerminology.CurrentPair(),
                    cfg.TermBaseUrl, cfg.Domain);
                ToolkitLog.Info("编辑器：语向变化重挂术语源，挂载 " + mounted + " 个");
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("编辑器：重挂术语源失败", ex);
            }
        }

        private void OnContentChanged(object sender, DocumentContentEventArgs e) => PushSegment();

        private void OnSegmentChanged(object sender, EventArgs e) => PushSegment();

        private void PushSegment()
        {
            try
            {
                var pair = _document?.GetActiveSegmentPair();
                if (pair == null)
                {
                    _view.ShowNoSegment("未打开文档或无活动段");
                    return;
                }

                // 2019 的 Segment.GetEnumerator() 直接抛 NotImplemented，只能用 Count/索引器遍历
                _targetHasTags = false;
                for (var i = 0; i < pair.Target.Count; i++)
                    if (!(pair.Target[i] is IText))
                    {
                        _targetHasTags = true;
                        break;
                    }
                var file = _document.ActiveFile;
                FindNeighbours(pair.Properties.Id.Id,
                    out var prevSource, out var prevTarget, out var nextSource);
                _view.ShowSegment(
                    pair.Properties.Id.Id,
                    pair.Source.ToString(),
                    pair.Target.ToString(),
                    pair.Properties.ConfirmationLevel.ToString(),
                    pair.Properties.IsLocked,
                    file?.Language?.IsoAbbreviation,
                    prevSource, prevTarget, nextSource);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LlmPanelController 读取当前段失败", e);
                _view.ShowNoSegment("读取当前段失败: " + e.Message);
            }
        }

        /// <summary>顺序遍历 SegmentPairs 找当前段的前后邻段，找到下一段即停（早停，避免全文档物化）。
        /// 仅取文本不做标签处理；任何异常退化为无上下文，不影响面板主功能。</summary>
        private void FindNeighbours(string activeId,
            out string prevSource, out string prevTarget, out string nextSource)
        {
            prevSource = prevTarget = nextSource = null;
            if (string.IsNullOrEmpty(activeId)) return;
            try
            {
                ISegmentPair current = null;
                ISegmentPair previous = null;
                foreach (var sp in _document.SegmentPairs)
                {
                    if (current != null)
                    {
                        nextSource = sp.Source.ToString();
                        break;
                    }
                    if (string.Equals(sp.Properties.Id.Id, activeId, StringComparison.Ordinal))
                    {
                        current = sp;
                        prevSource = previous?.Source.ToString();
                        prevTarget = previous?.Target.ToString();
                    }
                    else
                    {
                        previous = sp;
                    }
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LlmPanelController 邻段扫描失败（按无上下文继续）", e);
                prevSource = prevTarget = nextSource = null;
            }
        }

        private void OnApplyRequested(string text, bool markTranslated, bool continueNext)
        {
            try
            {
                var pair = _document?.GetActiveSegmentPair();
                if (pair == null || string.IsNullOrEmpty(text))
                {
                    ToolkitLog.Info("面板写回取消: 无活动段或空文本");
                    return;
                }
                if (pair.Properties.IsLocked)
                {
                    Warn("当前段已锁定，无法写入译文。");
                    return;
                }
                if (_targetHasTags &&
                    MessageBox.Show(
                        "当前段译文包含标签/占位符，写入将整体替换并丢失这些标签。仍要写入吗？",
                        "TradosToolkit LLM 助手", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                    != DialogResult.Yes)
                {
                    ToolkitLog.Info("面板写回取消: 用户放弃含标签段的整体替换");
                    return;
                }

                pair.Target.Clear();
                pair.Target.Add(_document.ItemFactory.CreateText(
                    _document.PropertiesFactory.CreateTextProperties(text)));
                _document.UpdateSegmentPair(pair);
                if (markTranslated)
                {
                    pair.Properties.ConfirmationLevel = ConfirmationLevel.Translated;
                    _document.UpdateSegmentPairProperties(pair, pair.Properties);
                }
                ToolkitLog.Info("面板写回成功: 段=" + pair.Properties.Id.Id +
                                " 长度=" + text.Length + " 标记译文=" + markTranslated);
                if (continueNext && markTranslated)
                    JumpToNextUnconfirmed(pair.Properties.Id.Id);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("面板写回失败", e);
                Warn("写入失败: " + e.Message);
            }
        }

        private static void Warn(string message) =>
            MessageBox.Show(message, "TradosToolkit LLM 助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        /// <summary>连续润色：从当前段之后找第一个"未确认"段并跳转。
        /// 跳过已确认(Translated/Approved)与锁定段；未找到则保持当前段。
        /// 任何异常退化为不跳转，不影响已完成的写回。</summary>
        private void JumpToNextUnconfirmed(string activeId)
        {
            try
            {
                if (string.IsNullOrEmpty(activeId)) return;
                var foundSelf = false;
                foreach (var sp in _document.SegmentPairs)
                {
                    if (!foundSelf)
                    {
                        // 先定位到当前段，之后才可能进入"下一段"判定
                        if (string.Equals(sp.Properties.Id.Id, activeId, StringComparison.Ordinal))
                            foundSelf = true;
                        continue;
                    }
                    // 从当前段之后逐段找下一个可写段（仅 Unspecified/Draft 视为"未确认"目标，其余跳过）
                    if (sp.Properties.IsLocked) continue;
                    var level = sp.Properties.ConfirmationLevel;
                    if (level != ConfirmationLevel.Unspecified && level != ConfirmationLevel.Draft)
                        continue;
                    _document.SetActiveSegmentPair((string)null, sp.Properties.Id.Id, true);
                    ToolkitLog.Info("连续润色跳转: 段=" + sp.Properties.Id.Id);
                    return;
                }
                ToolkitLog.Info("连续润色：已到末尾，停止跳转");
            }
            catch (Exception e)
            {
                ToolkitLog.Error("连续润色跳转失败（保持当前段）", e);
            }
        }
    }
}
