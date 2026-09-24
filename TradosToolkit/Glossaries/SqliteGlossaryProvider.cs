using System;
using System.Collections.Generic;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Glossaries
{
    /// <summary>
    /// 翻译流水线读取术语的入口：按语言对从 SQLite 加载，并带简单缓存，
    /// 避免每个查询批次都开库。缓存加锁、有上限；并登记实例以便全局失效。
    /// </summary>
    public class SqliteGlossaryProvider
    {
        private const int MaxCacheKeys = 256;

        // 各翻译方向各自 new 一个 provider，管理界面拿不到它们的引用，
        // 因此用弱引用登记，术语改动后由静态方法统一失效（否则要重启 Studio 才生效）。
        private static readonly List<WeakReference> Instances = new List<WeakReference>();
        private static readonly object InstancesGate = new object();

        private readonly GlossaryDb _db;
        private readonly object _gate = new object();
        private readonly Dictionary<string, IReadOnlyList<GlossaryEntry>> _cache =
            new Dictionary<string, IReadOnlyList<GlossaryEntry>>();

        public SqliteGlossaryProvider(GlossaryDb db = null)
        {
            _db = db ?? new GlossaryDb();
            lock (InstancesGate)
            {
                Instances.RemoveAll(w => !w.IsAlive);
                Instances.Add(new WeakReference(this));
            }
        }

        public IReadOnlyList<GlossaryEntry> Load(string kind, string sourceLang, string targetLang, string domain = null)
        {
            // 严格按当前领域过滤：只加载和当前领域一致的术语
            var dom = string.IsNullOrWhiteSpace(domain) ? ToolkitConfig.Load().Domain : domain.Trim();
            var key = kind + "|" + sourceLang + "|" + targetLang + "|" + dom;

            // 同一 provider 的缓存与底层库访问都串行化：Dictionary 并发写会损坏，GlossaryDb 连接也不保证并发安全。
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;

                IReadOnlyList<GlossaryEntry> entries;
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

                if (_cache.Count >= MaxCacheKeys) _cache.Clear(); // 简单上限，避免无限增长
                _cache[key] = entries;
                return entries;
            }
        }

        /// <summary>管理界面保存后调用，令缓存失效。</summary>
        public void InvalidateCache()
        {
            lock (_gate) _cache.Clear();
        }

        /// <summary>术语库发生增删改后调用，令所有在用 provider 实例的缓存失效。</summary>
        public static void InvalidateAllCaches()
        {
            lock (InstancesGate)
            {
                Instances.RemoveAll(w => !w.IsAlive);
                foreach (var w in Instances)
                    (w.Target as SqliteGlossaryProvider)?.InvalidateCache();
            }
        }
    }
}
