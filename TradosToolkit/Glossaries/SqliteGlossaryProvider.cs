using System;
using System.Collections.Generic;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Glossaries
{
    /// <summary>
    /// 翻译流水线读取术语的入口：按语言对从 SQLite 加载，并带简单缓存，
    /// 避免每个查询批次都开库。
    /// </summary>
    public class SqliteGlossaryProvider
    {
        private readonly GlossaryDb _db;
        private readonly Dictionary<string, IReadOnlyList<GlossaryEntry>> _cache =
            new Dictionary<string, IReadOnlyList<GlossaryEntry>>();

        public SqliteGlossaryProvider(GlossaryDb db = null)
        {
            _db = db ?? new GlossaryDb();
        }

        public IReadOnlyList<GlossaryEntry> Load(string kind, string sourceLang, string targetLang, string domain = null)
        {
            // 严格按当前领域过滤：只加载和当前领域一致的术语
            var dom = string.IsNullOrWhiteSpace(domain) ? ToolkitConfig.Load().Domain : domain.Trim();
            var key = kind + "|" + sourceLang + "|" + targetLang + "|" + dom;
            if (!_cache.TryGetValue(key, out var entries))
            {
                try
                {
                    entries = _db.GetTerms(kind, sourceLang, targetLang, dom);
                    ToolkitLog.Info("术语加载 " + key + " 条数=" + entries.Count);
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("术语加载失败 " + key + "（按无术语继续）", e);
                    entries = new List<GlossaryEntry>();
                }
                _cache[key] = entries;
            }
            return entries;
        }

        /// <summary>管理界面保存后调用，令缓存失效。</summary>
        public void InvalidateCache() => _cache.Clear();
    }
}
