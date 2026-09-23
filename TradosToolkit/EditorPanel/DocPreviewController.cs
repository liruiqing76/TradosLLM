using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.EditorPanel
{
    /// <summary>
    /// 编辑器右侧"文档预览"ViewPart：按段展示当前文档的原文/译文，
    /// 跟随编辑器高亮当前段、点击预览段可跳转编辑器、打字时就地刷新当前段。
    /// 与 LlmPanelController 同构：GetContentControl() 返回 WinForms Control（ElementHost 包 WPF 视图），
    /// ActiveDocument 是具体 Document 类，段内容用 Count/索引器遍历（2019 的 GetEnumerator 抛 NotImplemented）。
    /// </summary>
    [ViewPart(Id = "TradosToolkit.DocPreview", Name = "文档预览",
              Description = "按段实时预览当前文档的原文与译文，跟随编辑器高亮，点击可跳转")]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Right,
                    Width = 420, MinWidth = 280, Visible = true)]
    public class DocPreviewController : AbstractViewPartController
    {
        private readonly DocPreviewView _view = new DocPreviewView();
        private readonly ElementHost _host;

        private EditorController _editorController;
        private Document _document;
        private readonly System.Windows.Threading.DispatcherTimer _contentDebounce;

        public DocPreviewController()
        {
            ToolkitLog.Boot("doc-preview");
            _host = new ElementHost { Dock = DockStyle.Fill, Child = _view };
            _view.RefreshRequested += Rebuild;
            _view.SegmentActivated += OnSegmentActivated;
            _contentDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _contentDebounce.Tick += (s, e) => { _contentDebounce.Stop(); UpdateActiveSegment(); };
        }

        protected override void Initialize()
        {
            ToolkitLog.Info("DocPreviewController.Initialize");
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
                _document.ActiveSegmentChanged -= OnActiveSegmentChanged;
                _document.ContentChanged -= OnContentChanged;
                _document.SegmentsConfirmationLevelChanged -= OnActiveSegmentChanged;
                _document.ActiveFilePropertiesChanged -= OnActiveSegmentChanged;
                _document.ActiveFileChanged -= OnActiveFileChanged;
            }
            _document = document;
            if (_document != null)
            {
                _document.ActiveSegmentChanged += OnActiveSegmentChanged;
                _document.ContentChanged += OnContentChanged;
                _document.SegmentsConfirmationLevelChanged += OnActiveSegmentChanged;
                _document.ActiveFilePropertiesChanged += OnActiveSegmentChanged;
                _document.ActiveFileChanged += OnActiveFileChanged;
            }
            Rebuild();
        }

        private void OnActiveFileChanged(object sender, EventArgs e) => Rebuild();

        private void OnActiveSegmentChanged(object sender, EventArgs e) => PushActive();

        /// <summary>打字/改动很密集，防抖后再就地刷新当前段，避免整篇重载。</summary>
        private void OnContentChanged(object sender, DocumentContentEventArgs e)
        {
            _contentDebounce.Stop();
            _contentDebounce.Start();
        }

        /// <summary>整篇重建：文档打开/切换、切换文件、手动刷新时调用。</summary>
        private void Rebuild()
        {
            try
            {
                if (_document == null)
                {
                    _view.SetStatus(string.Empty);
                    _view.ShowEmpty("未打开文档。在编辑器中打开一个目标文件即可预览。");
                    return;
                }

                var html = DocPreviewRenderer.Build(_document, Header(), out var count);
                _view.ShowHtml(html);
                _view.SetStatus("共 " + count + " 段");
                PushActive();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("文档预览：重建失败", e);
                _view.ShowFallback("预览生成失败：" + e.Message);
            }
        }

        private string Header()
        {
            var file = _document.ActiveFile;
            var name = file?.Name ?? "当前文件";
            string src = null, tgt = file?.Language?.IsoAbbreviation;
            try { src = _document.Project?.GetProjectInfo()?.SourceLanguage?.IsoAbbreviation; }
            catch (Exception e) { ToolkitLog.Error("文档预览：读取源语言失败", e); }
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) return name;
            return name + "　" + src + " → " + tgt;
        }

        /// <summary>编辑器换段：让预览高亮并滚动到该段。</summary>
        private void PushActive()
        {
            try
            {
                var pair = _document?.GetActiveSegmentPair();
                if (pair == null) return;
                _view.SetActive(pair.Properties.Id.Id, true);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("文档预览：同步当前段失败", e);
            }
        }

        /// <summary>就地刷新当前段文本与状态（打字后防抖触发）。</summary>
        private void UpdateActiveSegment()
        {
            try
            {
                var pair = _document?.GetActiveSegmentPair();
                if (pair == null) return;
                _view.UpdateSegment(pair.Properties.Id.Id,
                    pair.Source?.ToString() ?? string.Empty,
                    pair.Target?.ToString() ?? string.Empty,
                    pair.Properties.ConfirmationLevel.ToString());
            }
            catch (Exception e)
            {
                ToolkitLog.Error("文档预览：更新当前段失败", e);
            }
        }

        /// <summary>点击预览里的段：把编辑器跳到该段（随后 ActiveSegmentChanged 会回推高亮）。</summary>
        private void OnSegmentActivated(string id)
        {
            try
            {
                if (_document == null || string.IsNullOrEmpty(id)) return;
                _document.SetActiveSegmentPair((string)null, id, true);
                ToolkitLog.Info("文档预览跳转: 段=" + id);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("文档预览：跳转段失败", e);
            }
        }
    }
}
