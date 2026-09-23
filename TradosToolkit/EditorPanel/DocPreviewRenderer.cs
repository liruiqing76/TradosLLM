using System;
using System.Text;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.EditorPanel
{
    /// <summary>
    /// 把当前文档按段渲染成一份自包含 HTML：每段一块，含原文/译文与确认状态。
    /// 段 id 写入 data-id，页面脚本据此与宿主双向通信（高亮/跳转/局部更新）。
    /// 纯字符串拼接，不依赖任何前端资源，NavigateToString/本地文件均可加载。
    /// </summary>
    internal static class DocPreviewRenderer
    {
        public static string Build(Document document, string header, out int segmentCount)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("<!DOCTYPE html><html lang=\"zh\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<style>").Append(Css).Append("</style></head><body class=\"mode-both\">");
            if (!string.IsNullOrEmpty(header))
                sb.Append("<div class=\"hdr\">").Append(Html(header)).Append("</div>");
            sb.Append("<div class=\"doc\">");

            var count = 0;
            try
            {
                foreach (var pair in document.SegmentPairs)
                {
                    // 单段异常只跳过该段，不让整篇预览中断
                    try
                    {
                        AppendSegment(sb, pair);
                        count++;
                    }
                    catch (Exception e)
                    {
                        ToolkitLog.Error("文档预览：渲染段失败，跳过", e);
                    }
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("文档预览：遍历段失败（已渲染部分）", e);
                sb.Append("<div class=\"empty\">渲染中断：").Append(Html(e.Message)).Append("</div>");
            }

            if (count == 0)
                sb.Append("<div class=\"empty\">当前文件没有可预览的段落。</div>");

            sb.Append("</div>");
            sb.Append("<script>").Append(Script).Append("</script></body></html>");
            segmentCount = count;
            ToolkitLog.Info("文档预览：渲染 " + count + " 段");
            return sb.ToString();
        }

        private static void AppendSegment(StringBuilder sb, ISegmentPair pair)
        {
            var id = pair.Properties.Id.Id;
            var src = pair.Source?.ToString() ?? string.Empty;
            var tgt = pair.Target?.ToString() ?? string.Empty;
            var level = pair.Properties.ConfirmationLevel.ToString();
            var locked = pair.Properties.IsLocked;

            sb.Append("<div class=\"seg");
            if (tgt.Length == 0) sb.Append(" untranslated");
            if (locked) sb.Append(" locked");
            sb.Append("\" data-id=\"").Append(Html(id)).Append("\" data-level=\"").Append(Html(level)).Append("\">");
            sb.Append("<span class=\"meta\">").Append(Html(level));
            if (locked) sb.Append(" · 锁定");
            sb.Append("</span>");
            sb.Append("<div class=\"src\">").Append(Html(src)).Append("</div>");
            sb.Append("<div class=\"tgt\">").Append(Html(tgt)).Append("</div>");
            sb.Append("</div>");
        }

        private static string Html(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private const string Css = @"
:root{color-scheme:light}
*{box-sizing:border-box}
html,body{margin:0;padding:0}
body{font-family:""Microsoft YaHei UI"",""Segoe UI"",sans-serif;font-size:13px;line-height:1.55;background:#fff;color:#1f2937}
.hdr{position:sticky;top:0;z-index:5;background:#f6f8fb;border-bottom:1px solid #e2e6ee;padding:6px 10px;font-size:12px;color:#5b6472}
.doc{padding:8px 10px 40px}
.seg{border:1px solid #e6eaf1;border-radius:6px;padding:6px 9px;margin:0 0 6px;cursor:pointer;position:relative}
.seg:hover{border-color:#b9c6da}
.seg.active{border-color:#2b7cff;background:#f2f7ff;box-shadow:0 0 0 2px rgba(43,124,255,.18)}
.seg .meta{float:right;font-size:10px;color:#9aa3b2;margin-left:8px}
.seg .src{color:#334155;white-space:pre-wrap;word-wrap:break-word}
.seg .tgt{margin-top:4px;color:#0b7a3b;white-space:pre-wrap;word-wrap:break-word}
.seg.untranslated .tgt{color:#b3bcc9;font-style:italic}
.seg.locked{background:#fafafa}
.seg.locked .src,.seg.locked .tgt{color:#94a0b0}
.seg[data-level=""Translated""]{border-left:3px solid #34a853}
.seg[data-level=""Approved""]{border-left:3px solid #0b7a3b}
.seg[data-level=""Draft""]{border-left:3px solid #f2a900}
.seg[data-level=""RejectedTranslation""]{border-left:3px solid #d64545}
body.mode-target .src{display:none}
body.mode-source .tgt{display:none}
.empty{padding:24px;color:#8a94a6;text-align:center}
";

        private const string Script = @"
(function(){
var map={};
var segs=document.querySelectorAll('.seg');
for(var i=0;i<segs.length;i++){map[segs[i].getAttribute('data-id')]=segs[i];}
var activeId=null;
function setActive(id,scroll){
  if(activeId&&map[activeId]){map[activeId].classList.remove('active');}
  activeId=id;
  var el=map[id];
  if(!el){return;}
  el.classList.add('active');
  if(scroll!==false){try{el.scrollIntoView({block:'center',behavior:'smooth'});}catch(e){el.scrollIntoView();}}
}
function update(id,src,tgt,level){
  var el=map[id];if(!el){return;}
  var s=el.querySelector('.src');if(s){s.textContent=src||'';}
  var t=el.querySelector('.tgt');if(t){t.textContent=tgt||'';}
  el.classList.toggle('untranslated',!(tgt&&tgt.length));
  if(level){el.setAttribute('data-level',level);var m=el.querySelector('.meta');if(m){m.textContent=level;}}
}
function setMode(mode){document.body.className='mode-'+mode;}
if(window.chrome&&window.chrome.webview){
  window.chrome.webview.addEventListener('message',function(e){
    var m=e.data;if(!m){return;}
    if(m.type==='setActive'){setActive(m.id,m.scroll);}
    else if(m.type==='update'){update(m.id,m.src,m.tgt,m.level);}
    else if(m.type==='setMode'){setMode(m.mode);}
  });
}
document.addEventListener('click',function(e){
  var n=e.target;
  while(n&&n!==document&&!(n.classList&&n.classList.contains('seg'))){n=n.parentNode;}
  if(n&&n!==document&&n.classList&&n.classList.contains('seg')){
    var id=n.getAttribute('data-id');
    if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'activate',id:id});}
  }
});
})();
";
    }
}
