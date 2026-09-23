using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.Terminology.TerminologyProvider.Core;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 原生术语源：把术语库暴露给 Studio 的原生术语引擎（编辑器术语识别、术语库查词、验证器）。
    /// 支持两套数据来源，用 kind 区分：
    ///   local  —— 本地 SQLite（GlossaryDb.term_entries / term_synonyms），**可写**：编辑器里
    ///             添加/修改术语直接落库，与术语管理界面共用同一份数据；
    ///   online —— 线上 URL 服务（termBaseUrl，POST /match、GET /search），只读。
    /// 写入能力不在 ITerminologyProvider 上，而是由 NativeTerminologyProviderViewerWinFormsUI
    /// 实现 ITerminologyProviderViewerWinFormsUI 承接（见该类）。
    /// 继承 AbstractTerminologyProvider（Studio15 术语 API 的推荐基类）。
    /// </summary>
    public sealed class NativeTerminologyProvider : AbstractTerminologyProvider
    {
        private readonly string _kind;
        private readonly string _baseUrl;
        private readonly string _sourceLang;
        private readonly string _targetLang;
        private readonly string _domain;
        private readonly GlossaryDb _db;
        private readonly List<IEntry> _entryCache = new List<IEntry>();
        private readonly object _cacheGate = new object();

        /// <param name="kind">TermSourceKind.Local / Online。</param>
        /// <param name="baseUrl">线上服务地址；local 时可传空。</param>
        /// <param name="glossaryDbPath">本地库文件路径；null 用默认（%APPDATA%\TradosToolkit\glossary.db）。</param>
        public NativeTerminologyProvider(string kind, string baseUrl, string sourceLang, string targetLang,
                                         string domain = null, string glossaryDbPath = null)
        {
            _kind = TermSourceKind.Normalize(kind);
            _baseUrl = _baseUrl0(baseUrl);
            _sourceLang = string.IsNullOrWhiteSpace(sourceLang) ? "zh-CN" : sourceLang.Trim();
            _targetLang = string.IsNullOrWhiteSpace(targetLang) ? "en-US" : targetLang.Trim();
            _domain = string.IsNullOrWhiteSpace(domain) ? DomainTree.DefaultDomain : domain.Trim();
            if (IsLocal)
            {
                try { _db = new GlossaryDb(glossaryDbPath); }
                catch (Exception e) { ToolkitLog.Error("术语源打开本地库失败", e); }
            }

            ToolkitLog.Info($"术语源创建：kind={_kind} baseUrl={_baseUrl} src={_sourceLang}"
                            + $"{(string.IsNullOrWhiteSpace(sourceLang) ? "(回退默认!)" : "")} "
                            + $"tgt={_targetLang}{(string.IsNullOrWhiteSpace(targetLang) ? "(回退默认!)" : "")} "
                            + $"domain={_domain} db={(glossaryDbPath ?? "默认")}");
        }

        private static string _baseUrl0(string baseUrl)
        {
            return string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim().TrimEnd('/');
        }

        /// <summary>是否本地可写源。</summary>
        public bool IsLocal => _kind == TermSourceKind.Local;

        public string Kind => _kind;
        public string SourceLang => _sourceLang;
        public string TargetLang => _targetLang;
        public string Domain => _domain;

        /// <summary>写入后的变更通知（Studio 编辑器据此刷新下划线）。</summary>
        public event EventHandler TermEntriesChanged;

        public override IDefinition Definition => new Definition(GetDescriptiveFields(), GetDefinitionLanguages());

        public override string Description => IsLocal
            ? "读写本地 SQLite 术语库的本机术语源"
            : "从内网术语服务实时检索的本机只读术语源";

        public override string Name => IsLocal ? "TradosToolkit 本地术语库" : "TradosToolkit 线上术语服务";

        public override Uri Uri => NativeTerminologyProviderHelper.BuildUri(_kind, _baseUrl, _sourceLang, _targetLang, _domain);

        public override IEntry GetEntry(int id)
        {
            lock (_cacheGate)
            {
                var cached = _entryCache.FirstOrDefault(e => e.Id == id);
                if (cached != null) return cached;
            }
            if (IsLocal && _db != null)
            {
                var record = _db.GetTermEntry(id);
                if (record != null) return BuildEntry(record, _sourceLang, _targetLang);
            }
            return null;
        }

        public override IEntry GetEntry(int id, IEnumerable<ILanguage> languages)
        {
            return GetEntry(id);
        }

        public override IList<ILanguage> GetLanguages()
        {
            var list = new List<IDefinitionLanguage>
            {
                MakeLanguage(_sourceLang),
                MakeLanguage(_targetLang),
            };
            return list.Cast<ILanguage>().ToList();
        }

        /// <summary>
        /// Fuzzy = 编辑器术语识别（段文本包含匹配）；Normal = 术语库查词窗口（前缀匹配）。
        /// 本地源查 SQLite；线上源分别走 POST /match 与 GET /search。
        /// </summary>
        public override IList<ISearchResult> Search(string text, ILanguage source, ILanguage destination,
                                                    int maxResultsCount, SearchMode mode, bool targetRequired)
        {
            var src = source?.Locale != null ? source.Locale.Name : _sourceLang;
            var tgt = destination?.Locale != null ? destination.Locale.Name : _targetLang;
            var results = new List<ISearchResult>();
            if (string.IsNullOrWhiteSpace(text)) return results;

            ToolkitLog.Info(
                $"术语识别 Search：text=\"{Truncate(text)}\" src={src}({(source?.Locale != null ? "Locale" : "默认")}) "
                + $"tgt={tgt}({(destination?.Locale != null ? "Locale" : "默认")}) mode={mode} "
                + $"kind={(_kind ?? "local")} domain={_domain}");

            if (IsLocal)
            {
                if (_db == null)
                {
                    ToolkitLog.Info("术语识别 Search：本地库未打开，返回空");
                    return results;
                }
                var entries = _db.GetTermEntries(src, tgt, _domain);
                ToolkitLog.Info($"术语识别 Search：本地库取到 {entries.Count} 条候选（{src}->{tgt}/{_domain}）");
                var limit = maxResultsCount <= 0 ? 20 : maxResultsCount;
                var entriesById = new Dictionary<long, TermEntry>();
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.FromTerm) || string.IsNullOrWhiteSpace(entry.ToTerm)) continue;
                    var hit = mode == SearchMode.Normal
                        ? StartsWithAny(entry, text)
                        : ContainsAny(entry, text);
                    if (!hit) continue;
                    entriesById[entry.Id] = entry;
                    results.Add(new SearchResult
                    {
                        Text = entry.FromTerm,
                        Score = ScoreFor(entry, text),
                        Id = unchecked((int)entry.Id),
                        // 必须声明结果所属语言：Studio 的术语识别按 SearchResult.Language 过滤/分组，
                        // 为 null 时结果会被整体丢弃（表现为术语识别面板空白）。这里的文本是源术语。
                        Language = source ?? (ILanguage)MakeLanguage(_sourceLang),
                    });
                    if (results.Count >= limit) break;
                }
                CacheEntries(entriesById);
                ToolkitLog.Info($"术语识别 Search：命中 {results.Count} 条");
                return results;
            }

            // 线上源
            if (string.IsNullOrEmpty(_baseUrl))
            {
                ToolkitLog.Info("术语识别 Search：线上源 baseUrl 为空，返回空");
                return results;
            }
            var hits = mode == SearchMode.Normal
                ? TermHttpClient.Search(_baseUrl, src, tgt, text, maxResultsCount, _domain)
                : TermHttpClient.Match(_baseUrl, src, tgt, text, maxResultsCount, _domain);

            var remoteById = new Dictionary<long, TermHit>();
            foreach (var hit in hits)
            {
                if (string.IsNullOrEmpty(hit.target)) continue;
                remoteById[hit.id] = hit;
                results.Add(new SearchResult
                {
                    Text = hit.source ?? hit.target,
                    Score = Math.Max(0, hit.score),
                    Id = hit.id,
                    Language = source ?? (ILanguage)MakeLanguage(_sourceLang),
                });
            }
            CacheRemote(remoteById, src, tgt);
            ToolkitLog.Info($"术语识别 Search：线上源命中 {results.Count} 条");
            return results;
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= 60 ? s : s.Substring(0, 60) + "...";
        }

        /// <summary>写入一条术语（仅本地源支持）。供 Viewer UI 的 AddTerm/AddAndEditTerm 调用。</summary>
        public TermEntry AddOrUpdateEntry(TermEntry entry)
        {
            if (!IsLocal || _db == null) return null;
            if (entry == null || string.IsNullOrWhiteSpace(entry.FromTerm)) return null;

            entry.SourceLang = entry.SourceLang ?? _sourceLang;
            entry.TargetLang = entry.TargetLang ?? _targetLang;
            if (string.IsNullOrWhiteSpace(entry.Domain)) entry.Domain = _domain;
            if (string.IsNullOrWhiteSpace(entry.Status)) entry.Status = TermStatus.Preferred;

            entry.Id = _db.SaveTermEntry(entry);
            RaiseChanged();
            return entry;
        }

        /// <summary>删除一条术语（仅本地源支持）。</summary>
        public bool RemoveEntry(long id)
        {
            if (!IsLocal || _db == null) return false;
            _db.DeleteTermEntry(id);
            lock (_cacheGate) _entryCache.RemoveAll(e => e.Id == unchecked((int)id));
            RaiseChanged();
            return true;
        }

        private void RaiseChanged()
        {
            try { TermEntriesChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception e) { ToolkitLog.Error("术语变更通知失败", e); }
        }

        private void CacheEntries(Dictionary<long, TermEntry> entries)
        {
            if (entries.Count == 0) return;
            lock (_cacheGate)
            {
                foreach (var kv in entries)
                {
                    var id = unchecked((int)kv.Key);
                    _entryCache.RemoveAll(e => e.Id == id);
                    _entryCache.Add(BuildEntry(kv.Value, _sourceLang, _targetLang));
                }
            }
        }

        private void CacheRemote(Dictionary<long, TermHit> hits, string sourceLang, string targetLang)
        {
            if (hits.Count == 0) return;
            lock (_cacheGate)
            {
                foreach (var hit in hits.Values)
                {
                    _entryCache.RemoveAll(e => e.Id == hit.id);
                    _entryCache.Add(BuildRemoteEntry(hit, sourceLang, targetLang));
                }
            }
        }

        public IList<IDescriptiveField> GetDescriptiveFields()
        {
            return new List<IDescriptiveField>
            {
                new DescriptiveField { Label = "Source", Level = FieldLevel.EntryLevel, Type = FieldType.String },
            };
        }

        /// <summary>语言对来自本 Provider 构造参数；无项目上下文时可正确向 Studio 声明。</summary>
        public IList<IDefinitionLanguage> GetDefinitionLanguages()
        {
            return new List<IDefinitionLanguage>
            {
                MakeLanguage(_sourceLang),
                MakeLanguage(_targetLang),
            };
        }

        private static IDefinitionLanguage MakeLanguage(string locale)
        {
            var lang = new Language(locale);
            return new DefinitionLanguage
            {
                IsBidirectional = true,
                Locale = lang.CultureInfo,
                Name = lang.DisplayName,
                TargetOnly = false,
            };
        }

        /// <summary>把本地完整术语条目装配成 Studio 的 IEntry（主术语 + 同义词 + 描述字段）。</summary>
        internal static IEntry BuildEntry(TermEntry record, string sourceLang, string targetLang)
        {
            var entry = new Entry { Id = unchecked((int)record.Id) };

            var srcLang = new EntryLanguage();
            srcLang.Locale = new CultureInfo(sourceLang);
            srcLang.Name = new Language(sourceLang).DisplayName;
            srcLang.Terms.Add(new EntryTerm { Value = record.FromTerm });
            foreach (var syn in record.SynonymsFor(sourceLang))
                srcLang.Terms.Add(new EntryTerm { Value = syn });
            entry.Languages.Add(srcLang);

            var tgtLang = new EntryLanguage();
            tgtLang.Locale = new CultureInfo(targetLang);
            tgtLang.Name = new Language(targetLang).DisplayName;
            tgtLang.Terms.Add(new EntryTerm { Value = record.ToTerm });
            foreach (var syn in record.SynonymsFor(targetLang))
                tgtLang.Terms.Add(new EntryTerm { Value = syn });
            entry.Languages.Add(tgtLang);

            entry.Fields.Add(new EntryField { Name = "词性", Value = TermStatus.Label(record.Status) + " / " + PartOfSpeech.Label(record.PartOfSpeech) });
            if (!string.IsNullOrWhiteSpace(record.Definition))
                entry.Fields.Add(new EntryField { Name = "定义", Value = record.Definition });
            if (!string.IsNullOrWhiteSpace(record.Example))
                entry.Fields.Add(new EntryField { Name = "例句", Value = record.Example });
            if (!string.IsNullOrWhiteSpace(record.Note))
                entry.Fields.Add(new EntryField { Name = "备注", Value = record.Note });
            return entry;
        }

        private static IEntry BuildRemoteEntry(TermHit hit, string sourceLang, string targetLang)
        {
            var entry = new Entry { Id = hit.id };
            if (!string.IsNullOrEmpty(hit.source))
            {
                var srcLang = new EntryLanguage();
                srcLang.Locale = new CultureInfo(sourceLang);
                srcLang.Name = new Language(sourceLang).DisplayName;
                srcLang.Terms.Add(new EntryTerm { Value = hit.source });
                entry.Languages.Add(srcLang);
            }
            if (!string.IsNullOrEmpty(hit.target))
            {
                var tgtLang = new EntryLanguage();
                tgtLang.Locale = new CultureInfo(targetLang);
                tgtLang.Name = new Language(targetLang).DisplayName;
                tgtLang.Terms.Add(new EntryTerm { Value = hit.target });
                entry.Languages.Add(tgtLang);
            }
            return entry;
        }

        private static bool ContainsAny(TermEntry entry, string text)
        {
            return Matches(entry, text, (source, needle) => source.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool StartsWithAny(TermEntry entry, string text)
        {
            return Matches(entry, text, (source, needle) => source.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 在分段文本 text 中查找术语词条。
        /// 注意方向：predicate(整句, 术语)，即"整句里是否出现该术语"，
        /// 而不是"术语里是否出现整句"（后者永远匹配不到，会导致识别空白）。
        /// </summary>
        private static bool Matches(TermEntry entry, string text, Func<string, string, bool> predicate)
        {
            if (!string.IsNullOrEmpty(entry.FromTerm) && predicate(text, entry.FromTerm)) return true;
            foreach (var syn in entry.Synonyms)
                if (!string.IsNullOrWhiteSpace(syn.Term) && predicate(text, syn.Term)) return true;
            return false;
        }

        private static int ScoreFor(TermEntry entry, string text)
        {
            if (string.Equals(entry.FromTerm, text, StringComparison.OrdinalIgnoreCase)) return 100;
            return 80;
        }
    }
}
