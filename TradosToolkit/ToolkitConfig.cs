using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using TradosToolkit.Diagnostics;

namespace TradosToolkit
{
    /// <summary>
    /// 插件级配置：%APPDATA%\TradosToolkit\config.json。
    /// tmUrl = 自建 TM 服务接口地址；apiKey/llmBaseUrl/llmModel = LLM 参数
    /// （配置窗口填写后自动保存，不走 Studio 凭据存储）。
    /// </summary>
    public class ToolkitConfig
    {
        /// <summary>翻译中心书签条目（Chrome 式：目录 → 条目）。</summary>
        public class BookmarkItem
        {
            public string name;
            public string url;
        }

        /// <summary>翻译中心书签目录（config.json 的 translationCenterFolders，folders 字段可无限嵌套）。</summary>
        public class BookmarkFolder
        {
            public string name;
            public List<BookmarkItem> items = new List<BookmarkItem>();
            public List<BookmarkFolder> folders = new List<BookmarkFolder>();
        }

        public string TmUrl = string.Empty;
        public string ApiKey = string.Empty;
        public string LlmBaseUrl = string.Empty;
        public string LlmModel = string.Empty;
        /// <summary>翻译中心主页地址（config.json 的 translationCenterUrl，空 = bing）。</summary>
        public string TranslationCenterUrl = string.Empty;
        /// <summary>LLM 并发请求数（config.json 的 llmConcurrency，缺省 6）。</summary>
        public int LlmConcurrency = 6;
        /// <summary>单个 LLM 请求超时秒数（config.json 的 llmTimeoutSeconds，缺省 120）。</summary>
        public int LlmTimeoutSeconds = 120;
        /// <summary>LLM 单段失败后的额外重试次数（config.json 的 llmRetryCount，缺省 1）。</summary>
        public int LlmRetryCount = 1;
        /// <summary>批内重复段去重（config.json 的 segmentDedup，缺省 true；false=每段独立送引擎）。</summary>
        public bool SegmentDedup = true;
        /// <summary>LLM 译文磁盘缓存跨文档复用（config.json 的 llmDiskCache，缺省 true）。</summary>
        public bool LlmDiskCacheEnabled = true;
        /// <summary>本地记忆库扫描目录（config.json 的 tmScanDirectory，工作台"记忆库"页使用）。</summary>
        public string TmScanDirectory = string.Empty;
        /// <summary>原生术语源所调用的线上术语服务地址（config.json 的 termBaseUrl，空 = 原生源不可用、返回空）。</summary>
        public string TermBaseUrl = string.Empty;
        /// <summary>翻译中心书签目录树；键缺失时用内置默认，键存在则完全按文件。</summary>
        public List<BookmarkFolder> Folders = DefaultFolders();

        public static List<BookmarkFolder> DefaultFolders() => new List<BookmarkFolder>
        {
            new BookmarkFolder
            {
                name = "常用地址",
                items = new List<BookmarkItem>
                {
                    new BookmarkItem { name = "Bing", url = "https://www.bing.com" },
                    new BookmarkItem { name = "百度", url = "https://www.baidu.com" },
                    new BookmarkItem { name = "百度翻译", url = "https://fanyi.baidu.com" },
                    new BookmarkItem { name = "Trados 社区", url = "https://community.rws.com" },
                },
            },
        };

        public static string ConfigFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "TradosToolkit", "config.json");

        public static ToolkitConfig Load()
        {
            var config = new ToolkitConfig();
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath));
                    File.WriteAllText(ConfigFilePath, "{\"tmUrl\":\"\",\"apiKey\":\"\"}");
                    ToolkitLog.Info("ToolkitConfig: 已创建默认 " + ConfigFilePath);
                    return config;
                }
                var json = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFilePath));
                if (json != null)
                {
                    if (json.TryGetValue("tmUrl", out var tm) && tm is string s)
                        config.TmUrl = (s ?? string.Empty).Trim();
                    if (json.TryGetValue("apiKey", out var ak) && ak is string k)
                        config.ApiKey = (k ?? string.Empty).Trim();
                    if (json.TryGetValue("llmBaseUrl", out var bu) && bu is string b)
                        config.LlmBaseUrl = (b ?? string.Empty).Trim();
                    if (json.TryGetValue("llmModel", out var mo) && mo is string m)
                        config.LlmModel = (m ?? string.Empty).Trim();
                    if (json.TryGetValue("translationCenterUrl", out var tc) && tc is string t)
                        config.TranslationCenterUrl = (t ?? string.Empty).Trim();
                    if (json.TryGetValue("llmConcurrency", out var cc))
                        config.LlmConcurrency = Math.Max(1, Math.Min(32, Convert.ToInt32(cc)));
                    if (json.TryGetValue("llmTimeoutSeconds", out var lt))
                        config.LlmTimeoutSeconds = Math.Max(5, Math.Min(600, Convert.ToInt32(lt)));
                    if (json.TryGetValue("llmRetryCount", out var lr))
                        config.LlmRetryCount = Math.Max(0, Math.Min(5, Convert.ToInt32(lr)));
                    if (json.TryGetValue("segmentDedup", out var sd))
                        config.SegmentDedup = Convert.ToBoolean(sd);
                    if (json.TryGetValue("llmDiskCache", out var dc))
                        config.LlmDiskCacheEnabled = Convert.ToBoolean(dc);
                    if (json.TryGetValue("tmScanDirectory", out var tsd) && tsd is string td)
                        config.TmScanDirectory = (td ?? string.Empty).Trim();
                    if (json.TryGetValue("termBaseUrl", out var tbu) && tbu is string tb)
                        config.TermBaseUrl = (tb ?? string.Empty).Trim();
                    if (json.TryGetValue("translationCenterFolders", out var bm))
                        config.Folders = ParseFolders(bm);
                    else if (json.TryGetValue("translationCenterBookmarks", out var legacy))
                    {
                        // 旧扁平列表 → 自动迁移进"常用地址"目录
                        var migrated = new BookmarkFolder { name = "常用地址" };
                        foreach (var item in ParseItems(legacy)) migrated.items.Add(item);
                        if (migrated.items.Count > 0) config.Folders = new List<BookmarkFolder> { migrated };
                    }
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("ToolkitConfig 读取失败: " + ConfigFilePath, e);
            }
            ToolkitLog.Info("ToolkitConfig: tmUrl=" + (string.IsNullOrEmpty(config.TmUrl) ? "(未配置)" : config.TmUrl) +
                            " apiKey=" + (string.IsNullOrEmpty(config.ApiKey) ? "(未配置)" : "(已配置,长度" + config.ApiKey.Length + ")"));
            return config;
        }

        private static List<BookmarkItem> ParseItems(object value)
        {
            var list = new List<BookmarkItem>();
            // JavaScriptSerializer 的数组是 ArrayList，只实现非泛型 IEnumerable
            var items = value as System.Collections.IEnumerable;
            if (items == null) return list;
            foreach (var o in items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var item = new BookmarkItem
                {
                    name = d.TryGetValue("name", out var n) ? n as string : null,
                    url = d.TryGetValue("url", out var u) ? u as string : null,
                };
                if (!string.IsNullOrWhiteSpace(item.url)) list.Add(item);
            }
            return list;
        }

        private static List<BookmarkFolder> ParseFolders(object value)
        {
            var folders = new List<BookmarkFolder>();
            var items = value as System.Collections.IEnumerable;
            if (items == null) return folders;
            foreach (var o in items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var folder = new BookmarkFolder
                {
                    name = d.TryGetValue("name", out var n) ? (n as string ?? "未命名") : "未命名",
                    items = d.TryGetValue("items", out var its) ? ParseItems(its) : new List<BookmarkItem>(),
                    folders = d.TryGetValue("folders", out var subs) ? ParseFolders(subs) : new List<BookmarkFolder>(),
                };
                folders.Add(folder);
            }
            return folders;
        }

        /// <summary>配置窗口点确定时调用：null 表示不改动该字段，保留文件里其余内容。</summary>
        public static void Save(string apiKey = null, string llmBaseUrl = null, string llmModel = null,
                               string translationCenterUrl = null, List<BookmarkFolder> folders = null,
                               string tmScanDirectory = null, string termBaseUrl = null)
        {
            try
            {
                var json = new JavaScriptSerializer();
                Dictionary<string, object> doc = null;
                if (File.Exists(ConfigFilePath))
                    doc = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFilePath));
                if (doc == null) doc = new Dictionary<string, object>();
                if (apiKey != null) doc["apiKey"] = apiKey;
                if (llmBaseUrl != null) doc["llmBaseUrl"] = llmBaseUrl;
                if (llmModel != null) doc["llmModel"] = llmModel;
                if (translationCenterUrl != null) doc["translationCenterUrl"] = translationCenterUrl;
                if (tmScanDirectory != null) doc["tmScanDirectory"] = tmScanDirectory;
                if (termBaseUrl != null) doc["termBaseUrl"] = termBaseUrl;
                if (folders != null)
                {
                    doc["translationCenterFolders"] = folders;
                    doc.Remove("translationCenterBookmarks");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath));
                File.WriteAllText(ConfigFilePath, json.Serialize(doc));
                ToolkitLog.Info("ToolkitConfig: 已保存 (apiKey=" + (apiKey == null ? "不变" : "长度" + apiKey.Length) +
                                " baseUrl=" + (llmBaseUrl ?? "不变") + " model=" + (llmModel ?? "不变") + ")");
            }
            catch (Exception e)
            {
                ToolkitLog.Error("ToolkitConfig: 保存失败", e);
                throw;
            }
        }
    }
}
