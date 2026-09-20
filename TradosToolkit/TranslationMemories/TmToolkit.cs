using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.TranslationProvider.Engines;

namespace TradosToolkit.TranslationMemories
{
    /// <summary>合并时同源文不同译文的取舍策略。</summary>
    public enum MergeConflictPolicy { KeepNewest, KeepOldest, KeepFirst }

    public class TmMergeReport
    {
        public string TargetPath;
        public int Read;
        public int Written;
        public int SkippedDuplicates;
        public int ConflictsResolved;
        public List<string> Notes = new List<string>();
    }

    public class TmDuplicateGroup
    {
        public string SourceText;
        public List<string> Targets = new List<string>();
        public int Count;
    }

    /// <summary>
    /// 本地 .sdltm 工具套件（Studio15 公共 API，反射确认签名）：
    /// 新建空库、多库合并去重（冲突按策略取舍）、库内重复条目检测。
    /// 全部跑在后台线程，支持进度与取消；异常一律向上抛给 UI 层降级提示。
    /// </summary>
    public static class TmToolkit
    {
        private const int Batch = 500;

        /// <summary>创建新的空记忆库（语言对在建库时固定）。FileBasedTranslationMemory 不实现 IDisposable，用完置空即可。</summary>
        public static void CreateNew(string filePath, string name, CultureInfo source, CultureInfo target)
        {
            if (File.Exists(filePath))
                throw new InvalidOperationException("文件已存在: " + filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(filePath);
            var tm = new FileBasedTranslationMemory(filePath, name, source, target,
                FuzzyIndexes.SourceWordBased | FuzzyIndexes.TargetWordBased,
                BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
            tm.Save();
            ToolkitLog.Info("TmToolkit: 新建空库 " + filePath + " " + source.Name + "→" + target.Name);
        }

        /// <summary>读出整库翻译单元（RegularIterator 按 ProcessedTranslationUnits &lt; 总数推进）。</summary>
        public static List<TranslationUnit> ReadAll(FileBasedTranslationMemory tm,
            IProgress<int> progress, CancellationToken ct)
        {
            var dir = tm.LanguageDirection;
            var total = dir.GetTranslationUnitCount();
            var all = new List<TranslationUnit>(Math.Max(total, 16));
            var it = new RegularIterator { Forward = true, MaxCount = Batch };
            while (all.Count < total)
            {
                ct.ThrowIfCancellationRequested();
                var batch = dir.GetTranslationUnits(ref it);
                if (batch == null || batch.Length == 0) break; // 防御：迭代器不再前进时退出
                all.AddRange(batch);
                progress?.Report(all.Count);
            }
            return all;
        }

        /// <summary>
        /// 多库 → 目标库合并。目标不存在则按首个源库的语言对新建；存在则直接写入。
        /// 键 = 归一化源文；同键不同译文按 policy 取舍（InsertDate 空值视为最旧）。
        /// </summary>
        public static TmMergeReport Merge(IList<string> sourcePaths, string targetPath,
            MergeConflictPolicy policy, IProgress<string> stage, CancellationToken ct)
        {
            if (sourcePaths == null || sourcePaths.Count == 0)
                throw new ArgumentException("没有选择源库");
            var report = new TmMergeReport { TargetPath = targetPath };

            // 1) 读全部源库（语言对不一致的跳过并记入 Notes）
            CultureInfo src = null, tgt = null;
            var best = new Dictionary<string, TranslationUnit>(); // key=归一化源文 → 胜出的 TU
            foreach (var path in sourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                stage?.Report(Path.GetFileName(path));
                FileBasedTranslationMemory tm = null;
                try
                {
                    tm = new FileBasedTranslationMemory(path);
                    if (tm.IsProtected)
                    {
                        report.Notes.Add(path + " : protected, skipped");
                        continue;
                    }
                    var dir = tm.LanguageDirection;
                    if (src == null) { src = dir.SourceLanguage; tgt = dir.TargetLanguage; }
                    else if (dir.SourceLanguage.Name != src.Name || dir.TargetLanguage.Name != tgt.Name)
                    {
                        report.Notes.Add(path + " : language pair " + dir.SourceLanguage.Name + "→" +
                                         dir.TargetLanguage.Name + " mismatch, skipped");
                        continue;
                    }
                    var tus = ReadAll(tm, null, ct);
                    report.Read += tus.Count;
                    foreach (var tu in tus)
                    {
                        var key = SegmentDedup.Normalize(tu.SourceSegment == null ? "" : tu.SourceSegment.ToPlain());
                        if (string.IsNullOrEmpty(key)) continue;
                        if (!best.TryGetValue(key, out var incumbent)) { best[key] = tu; continue; }
                        report.SkippedDuplicates++;
                        var incText = SegmentDedup.Normalize(incumbent.TargetSegment == null ? "" : incumbent.TargetSegment.ToPlain());
                        var curText = SegmentDedup.Normalize(tu.TargetSegment == null ? "" : tu.TargetSegment.ToPlain());
                        if (string.Equals(incText, curText, StringComparison.Ordinal)) continue;
                        report.ConflictsResolved++;
                        if (ShouldReplace(incumbent, tu, policy)) best[key] = tu;
                    }
                }
                finally
                {
                    try { (tm as IDisposable)?.Dispose(); } catch { }
                }
            }
            if (src == null) throw new InvalidOperationException("所有源库都无法读取");

            // 2) 打开/新建目标库并分批写入
            ct.ThrowIfCancellationRequested();
            stage?.Report("write");
            FileBasedTranslationMemory target = null;
            try
            {
                var exists = File.Exists(targetPath);
                if (!exists)
                    CreateNew(targetPath, Path.GetFileNameWithoutExtension(targetPath), src, tgt);
                target = new FileBasedTranslationMemory(targetPath);
                if (exists && (target.LanguageDirection.SourceLanguage.Name != src.Name ||
                               target.LanguageDirection.TargetLanguage.Name != tgt.Name))
                    throw new InvalidOperationException("target language pair mismatch");
                if (target.IsProtected) throw new InvalidOperationException("target is protected");

                var list = best.Values.ToList();
                var dir = target.LanguageDirection;
                var settings = new ImportSettings();
                for (var i = 0; i < list.Count; i += Batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = list.Skip(i).Take(Batch).ToArray();
                    var mask = chunk.Select(_ => true).ToArray();
                    var results = dir.AddTranslationUnitsMasked(chunk, settings, mask);
                    // ImportResult.Action: Discard/Add/Merge/Overwrite/Error（ErrorCode 是结构体不能判 null）
                    report.Written += results == null ? chunk.Length
                        : results.Count(r => r != null
                            && r.Action != Sdl.LanguagePlatform.TranslationMemory.Action.Error
                            && r.Action != Sdl.LanguagePlatform.TranslationMemory.Action.Discard);
                    stage?.Report(Math.Min(i + Batch, list.Count) + "/" + list.Count);
                }
                target.Save();
            }
            finally
            {
                try { (target as IDisposable)?.Dispose(); } catch { }
            }
            ToolkitLog.Info("TmToolkit.Merge 完成: 读=" + report.Read + " 写=" + report.Written +
                            " 重复跳过=" + report.SkippedDuplicates + " 冲突=" + report.ConflictsResolved +
                            " 目标=" + targetPath);
            return report;
        }

        /// <summary>库内重复条目检测（GetDuplicateTranslationUnits 原生迭代器）。</summary>
        public static List<TmDuplicateGroup> FindDuplicates(string path, IProgress<int> progress, CancellationToken ct)
        {
            var groups = new List<TmDuplicateGroup>();
            var tm = new FileBasedTranslationMemory(path);
            try
            {
                if (tm.IsProtected) throw new InvalidOperationException("protected");
                var dir = tm.LanguageDirection;
                var total = dir.GetTranslationUnitCount();
                var it = new DuplicateIterator { Forward = true, MaxCount = Batch };
                var seen = new HashSet<string>();
                while (it.ProcessedTranslationUnits < total)
                {
                    ct.ThrowIfCancellationRequested();
                    var tus = dir.GetDuplicateTranslationUnits(ref it);
                    if (tus == null || tus.Length == 0) break;
                    foreach (var g in tus.GroupBy(tu => SegmentDedup.Normalize(tu.SourceSegment == null ? "" : tu.SourceSegment.ToPlain())))
                    {
                        if (!seen.Add(g.Key)) continue;
                        groups.Add(new TmDuplicateGroup
                        {
                            SourceText = g.Key,
                            Count = g.Count(),
                            Targets = g.Select(tu => SegmentDedup.Normalize(tu.TargetSegment == null ? "" : tu.TargetSegment.ToPlain()))
                                       .Distinct().ToList(),
                        });
                    }
                    progress?.Report((int)it.ProcessedTranslationUnits);
                }
            }
            finally
            {
                try { (tm as IDisposable)?.Dispose(); } catch { }
            }
            ToolkitLog.Info("TmToolkit.FindDuplicates: " + path + " 组数=" + groups.Count);
            return groups;
        }

        private static bool ShouldReplace(TranslationUnit incumbent, TranslationUnit candidate, MergeConflictPolicy policy)
        {
            switch (policy)
            {
                case MergeConflictPolicy.KeepFirst: return false;
                case MergeConflictPolicy.KeepOldest:
                    return DateOf(candidate) < DateOf(incumbent);
                default: // KeepNewest
                    return DateOf(candidate) > DateOf(incumbent);
            }
        }

        private static DateTime DateOf(TranslationUnit tu) => tu.InsertDate ?? DateTime.MinValue;
    }
}
