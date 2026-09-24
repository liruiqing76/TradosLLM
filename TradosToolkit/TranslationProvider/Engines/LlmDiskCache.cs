using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationProvider.Engines
{
    /// <summary>
    /// LLM 译文磁盘缓存：%APPDATA%\TradosToolkit\llm_cache.json。
    /// 键 = SHA1(baseUrl|model|源语言|目标语言|归一化源文)，跨文档命中直接跳过网关调用，
    /// 同一模型下同一句永远同译。并发写线程安全；任何读写异常一律降级为"无缓存"，绝不影响翻译链路。
    /// </summary>
    public static class LlmDiskCache
    {
        private const int MaxEntries = 20000;

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _entries;
        private static bool _dirty;

        public static string CacheFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "TradosToolkit", "llm_cache.json");

        public static string KeyFor(string baseUrl, string model, string sourceLang, string targetLang,
                                    string domain, string styleGuide, string text)
        {
            // domain / styleGuide 会改变译文的术语与风格，必须纳入键，否则切换领域后会复用旧领域译文。
            var raw = (baseUrl ?? string.Empty).TrimEnd('/') + "|" + (model ?? string.Empty) + "|" +
                      sourceLang + "|" + targetLang + "|" +
                      (domain ?? string.Empty) + "|" + (styleGuide ?? string.Empty) + "|" +
                      SegmentDedup.Normalize(text);
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string TryGet(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (Gate)
            {
                EnsureLoaded();
                string value;
                return _entries.TryGetValue(key, out value) ? value : null;
            }
        }

        public static void Put(string key, string translation)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(translation)) return;
            lock (Gate)
            {
                EnsureLoaded();
                _entries[key] = translation;
                _dirty = true;
                if (_entries.Count > MaxEntries)
                {
                    // 容量控制：Dictionary 无插入序，这里只保证「砍掉约一半」，不承诺淘汰的是最旧条目。
                    var removed = _entries.Count / 2;
                    var trimmed = new Dictionary<string, string>();
                    var skip = removed;
                    foreach (var kv in _entries)
                    {
                        if (skip > 0) { skip--; continue; }
                        trimmed[kv.Key] = kv.Value;
                    }
                    _entries = trimmed;
                    ToolkitLog.Info("LLM 缓存超上限，已裁掉 " + removed + " 条（无插入序，非严格最旧），剩 " + _entries.Count);
                }
            }
        }

        /// <summary>每批翻译结束调用一次；无变化不写盘。</summary>
        public static void SaveIfDirty()
        {
            lock (Gate)
            {
                if (!_dirty || _entries == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath));
                    var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var doc = new Dictionary<string, object> { { "v", 1 }, { "entries", _entries } };
                    File.WriteAllText(CacheFilePath, json.Serialize(doc), Encoding.UTF8);
                    _dirty = false;
                    ToolkitLog.Info("LLM 缓存已保存: 条目=" + _entries.Count);
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("LLM 缓存保存失败（不影响翻译）", e);
                }
            }
        }

        /// <summary>测试/重置用：强制下次访问重新从文件加载。</summary>
        public static void ClearForTests()
        {
            lock (Gate)
            {
                _entries = null;
                _dirty = false;
            }
        }

        private static void EnsureLoaded()
        {
            if (_entries != null) return;
            _entries = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(CacheFilePath))
                {
                    ToolkitLog.Info("LLM 缓存: 文件不存在，首次使用 (" + CacheFilePath + ")");
                    return;
                }
                var doc = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(CacheFilePath));
                object raw;
                if (doc != null && doc.TryGetValue("entries", out raw))
                {
                    var dict = raw as Dictionary<string, object>;
                    if (dict != null)
                        foreach (var kv in dict)
                        {
                            var s = kv.Value as string;
                            if (!string.IsNullOrEmpty(s)) _entries[kv.Key] = s;
                        }
                }
                ToolkitLog.Info("LLM 缓存已加载: 条目=" + _entries.Count + " file=" + CacheFilePath);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("LLM 缓存加载失败，降级为无缓存", ex);
                _entries = new Dictionary<string, string>();
            }
        }
    }
}
