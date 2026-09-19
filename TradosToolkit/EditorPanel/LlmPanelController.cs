using System;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Sdl.Core.Globalization;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.FileTypeSupport.Framework;
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
            }
            _document = document;
            if (_document != null)
            {
                _document.ActiveSegmentChanged += OnSegmentChanged;
                _document.ContentChanged += OnContentChanged;
                _document.SegmentsConfirmationLevelChanged += OnSegmentChanged;
                _document.ActiveFilePropertiesChanged += OnSegmentChanged;
            }
            PushSegment();
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

                _targetHasTags = pair.Target.OfType<IAbstractMarkupData>()
                                     .Any(item => !(item is IText));
                var file = _document.ActiveFile;
                _view.ShowSegment(
                    pair.Properties.Id.Id,
                    pair.Source.ToString(),
                    pair.Target.ToString(),
                    pair.Properties.ConfirmationLevel.ToString(),
                    pair.Properties.IsLocked,
                    file?.Language?.IsoAbbreviation);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LlmPanelController 读取当前段失败", e);
                _view.ShowNoSegment("读取当前段失败: " + e.Message);
            }
        }

        private void OnApplyRequested(string text, bool markTranslated)
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
            }
            catch (Exception e)
            {
                ToolkitLog.Error("面板写回失败", e);
                Warn("写入失败: " + e.Message);
            }
        }

        private static void Warn(string message) =>
            MessageBox.Show(message, "TradosToolkit LLM 助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
