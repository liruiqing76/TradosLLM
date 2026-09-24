using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationMemories
{
    /// <summary>一次 TMX / SDLXLIFF 导入的产出报告（供 UI 汇总展示）。</summary>
    public class TmImportReport
    {
        public string FileName;
        public string Format = "TMX";           // TMX / SDLXLIFF
        public int Pairs;                       // 解析出的匹配句对
        public int Added;                       // 实际写入目标库
        public int SkippedMismatch;             // 语言对与该库不匹配被跳过
        public int SkippedTags;                 // 含内联标签(保护占位)被跳过
        public List<string> Notes = new List<string>();
    }

    internal struct SourcePair { public string Source; public string Target; }

    /// <summary>
    /// 把 .tmx / .sdlxliff 里的双语句对导入到既存的本地记忆库。
    /// "导入到指定语向"=目标库的语言对即是语向；源文件里与该语向不一致的句对自动跳过，
    /// 含内联结构标签(placeholder 保护)的段跳过，其余按普通句批量化写入。
    /// 全程可取消、有进度；异常向上抛给 UI 降级提示。
    /// </summary>
    public static class TmImporter
    {
        private const int Batch = 500;

        public static TmImportReport Import(string targetTmPath, string filePath,
                                            IProgress<string> stage, CancellationToken ct)
        {
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            if (ext == ".tmx")
                return ImportInternal(targetTmPath, filePath, "TMX", ParseTmx, stage, ct);
            if (ext == ".sdlxliff" || ext == ".xlf" || ext == ".xliff")
                return ImportInternal(targetTmPath, filePath, "SDLXLIFF", ParseSdlxliff, stage, ct);
            throw new InvalidOperationException("不支持的文件类型: " + ext + "（请选 .tmx 或 .sdlxliff）");
        }

        public static string[] SupportedFilters => new[] { "TMX 文件 (*.tmx)|*.tmx", "SDLXLIFF 文件 (*.sdlxliff)|*.sdlxliff" };

        private static TmImportReport ImportInternal(string targetTmPath, string filePath, string format,
            Func<XDocument, CultureInfo, CultureInfo, TmImportReport, List<SourcePair>> parse,
            IProgress<string> stage, CancellationToken ct)
        {
            var report = new TmImportReport { FileName = Path.GetFileName(filePath), Format = format };

            FileBasedTranslationMemory tm = null;
            try
            {
                if (!File.Exists(targetTmPath))
                    throw new InvalidOperationException("目标记忆库不存在: " + targetTmPath);
                // 写目标库前先备份 .bak：中途失败留半写入库时可回退（与译文写回的做法一致）
                try { File.Copy(targetTmPath, targetTmPath + ".bak", true); }
                catch (Exception e) { ToolkitLog.Warn("TmImporter: 备份目标库失败（继续导入）", e); }
                tm = new FileBasedTranslationMemory(targetTmPath);
                if (tm.IsProtected)
                    throw new InvalidOperationException("目标记忆库被保护，无法写入: " + Path.GetFileName(targetTmPath));
                var dir = tm.LanguageDirection;
                var src = dir.SourceLanguage;
                var tgt = dir.TargetLanguage;

                XDocument doc;
                try { doc = XDocument.Load(filePath, LoadOptions.PreserveWhitespace); }
                catch (Exception e) { throw new InvalidOperationException("解析 " + format + " 失败: " + e.Message); }

                stage?.Report("解析 " + format + " …");
                var pairs = parse(doc, src, tgt, report);
                report.Pairs = pairs.Count;

                var settings = new ImportSettings();
                for (var i = 0; i < pairs.Count; i += Batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = pairs.Skip(i).Take(Batch).Select(p => MakeTu(p, src, tgt)).ToArray();
                    var mask = chunk.Select(_ => true).ToArray();
                    var results = dir.AddTranslationUnitsMasked(chunk, settings, mask);
                    report.Added += results == null ? chunk.Length
                        : results.Count(r => r != null
                            && r.Action != Sdl.LanguagePlatform.TranslationMemory.Action.Error
                            && r.Action != Sdl.LanguagePlatform.TranslationMemory.Action.Discard);
                    stage?.Report("导入 " + Path.GetFileName(targetTmPath) + "：" +
                                   Math.Min(i + Batch, pairs.Count) + "/" + pairs.Count + " …");
                }
                tm.Save();
            }
            finally
            {
                try { (tm as IDisposable)?.Dispose(); } catch { }
            }

            ToolkitLog.Info("TmImporter:" + format + " " + filePath + " → " + targetTmPath +
                            " 句对=" + report.Pairs + " 写入=" + report.Added +
                            " 跳过语向=" + report.SkippedMismatch + " 跳过含标签=" + report.SkippedTags);
            return report;
        }

        private static TranslationUnit MakeTu(SourcePair p, CultureInfo src, CultureInfo tgt)
        {
            var tu = new TranslationUnit();
            var s = new Segment { Culture = src };
            s.Add(p.Source);
            var t = new Segment { Culture = tgt };
            t.Add(p.Target);
            tu.SourceSegment = s;
            tu.TargetSegment = t;
            return tu;
        }

        // ---------- TMX ----------

        private static List<SourcePair> ParseTmx(XDocument doc, CultureInfo src, CultureInfo tgt,
                                                 TmImportReport report)
        {
            var pairs = new List<SourcePair>();
            foreach (var tu in doc.Descendants().Where(e => e.Name.LocalName == "tu"))
            {
                var segs = new Dictionary<string, string>();
                foreach (var tuv in tu.Elements().Where(e => e.Name.LocalName == "tuv"))
                {
                    var lang = (string)tuv.Attribute(XNamespace.Xml + "lang") ?? "";
                    var seg = tuv.Elements().FirstOrDefault(e => e.Name.LocalName == "seg");
                    if (seg == null) continue;
                    segs[NormLang(lang)] = Plain(seg);
                }
                var s = segs.TryGetValue(NormLang(src), out var sv) ? sv : null;
                var t = segs.TryGetValue(NormLang(tgt), out var tv) ? tv : null;
                if (string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(t))
                {
                    if (s != null || t != null) report.SkippedMismatch++;
                    continue;
                }
                pairs.Add(new SourcePair { Source = s, Target = t });
            }
            return pairs;
        }

        // ---------- SDLXLIFF ----------

        private static List<SourcePair> ParseSdlxliff(XDocument doc, CultureInfo src, CultureInfo tgt,
                                                      TmImportReport report)
        {
            var pairs = new List<SourcePair>();
            foreach (var tu in doc.Descendants().Where(e => e.Name.LocalName == "trans-unit"))
            {
                var file = tu.Ancestors().FirstOrDefault(a => a.Name.LocalName == "file");
                if (file != null)
                {
                    var fs = (string)file.Attribute("source-language") ?? "";
                    var ft = (string)file.Attribute("target-language") ?? "";
                    if (fs.Length > 0 && !NormLang(fs).Equals(NormLang(src)))
                    { report.SkippedMismatch++; continue; }
                    if (ft.Length > 0 && !NormLang(ft).Equals(NormLang(tgt)))
                    { report.SkippedMismatch++; continue; }
                }
                var source = tu.Elements().FirstOrDefault(e => e.Name.LocalName == "source");
                var target = tu.Elements().FirstOrDefault(e => e.Name.LocalName == "target");
                if (source == null || target == null) continue;
                var s = Plain(source);
                var t = Plain(target);
                if (string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(t)) continue;
                // 含内联结构标签(g/x/bx/ex/ph/mrk)的段跳过，保护占位不破坏
                if (source.Descendants().Any() || target.Descendants().Any())
                { report.SkippedTags++; continue; }
                pairs.Add(new SourcePair { Source = s, Target = t });
            }
            return pairs;
        }

        // ---------- helpers ----------

        private static string Plain(XElement e)
        {
            return string.Concat(e.DescendantNodes().OfType<XText>().Select(x => x.Value)).Trim();
        }

        private static string NormLang(CultureInfo c) => NormLang(c?.Name);
        private static string NormLang(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return s.Trim().Replace('_', '-').ToLowerInvariant();
        }
    }
}