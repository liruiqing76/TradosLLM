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

        /// <summary>流程编排里的一张流程卡：一个有序步骤列表（steps 为操作 key，见工作台 ProcOps）。</summary>
        public class ProcessCard
        {
            public string name;
            public List<string> steps = new List<string>();
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
        /// <summary>是否按天定时重建本地库索引（config.json 的 tmIndexAutoRefresh，缺省 false）。</summary>
        public bool TmIndexAutoRefresh = false;
        /// <summary>本地库索引每日重建时间（config.json 的 tmIndexRefreshTime，HH:mm，缺省 02:00）。</summary>
        public string TmIndexRefreshTime = "02:00";
        /// <summary>上次自动重建本地库索引的完成时间（config.json 的 tmIndexLastRun，ISO 字符串）。</summary>
        public string TmIndexLastRun = string.Empty;
        /// <summary>收件箱（目录监视）监听的投放目录（config.json 的 inboxWatchFolder）。</summary>
        public string InboxWatchFolder = string.Empty;
        /// <summary>收件箱产出目录：每个任务一个子目录，放分析报告 / 交付包 / 匹配库（config.json 的 inboxOutputFolder）。</summary>
        public string InboxOutputFolder = string.Empty;
        /// <summary>收件箱自动创建项目的存放目录（config.json 的 inboxProjectRoot，空 = 产出目录下的 Projects）。</summary>
        public string InboxProjectRoot = string.Empty;
        /// <summary>收件箱源语言（config.json 的 inboxSourceLang，缺省 zh-CN）。</summary>
        public string InboxSourceLang = "zh-CN";
        /// <summary>收件箱目标语言（config.json 的 inboxTargetLang，缺省 en-US）。</summary>
        public string InboxTargetLang = "en-US";
        /// <summary>收件箱分析报告「另存为」格式（config.json 的 inboxReportFormat，缺省 excel；可选 excel|xml|html|mht）。</summary>
        public string InboxReportFormat = "excel";
        /// <summary>插件启动时是否自动开始目录监视（config.json 的 inboxAutoStart）。</summary>
        public bool InboxAutoStart = false;
        /// <summary>原生术语源所调用的线上术语服务地址（config.json 的 termBaseUrl，空 = 原生源不可用、返回空）。</summary>
        public string TermBaseUrl = string.Empty;
        /// <summary>全局领域（config.json 的 domain，缺省"通用"）。术语库/翻译插件/原生术语插件共用同一领域，工作台可直接切换。</summary>
        public string Domain = Glossaries.DomainTree.DefaultDomain;
        /// <summary>翻译中心书签目录树；键缺失时用内置默认，键存在则完全按文件。</summary>
        public List<BookmarkFolder> Folders = DefaultFolders();

        /// <summary>流程编排卡（config.json 的 processCards）。</summary>
        public List<ProcessCard> ProcessCards = DefaultProcessCards();

        /// <summary>内置示例流程卡：演示"用 Studio 原生自动任务 + 插件特有步骤 串成一条流程"。</summary>
        public static List<ProcessCard> DefaultProcessCards() => new List<ProcessCard>
        {
            new ProcessCard
            {
                name = "示例：预翻译→回填→统一→导出",
                steps = new List<string> { "pretranslate", "backfill", "auditapply", "updatetm" },
            },
        };

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

        /// <summary>校验 "HH:mm" 形式的每日时间（00:00–23:59）。</summary>
        public static bool IsValidTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return DateTime.TryParseExact(value.Trim(), new[] { "H:mm", "HH:mm" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _);
        }

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
                    if (json.TryGetValue("tmIndexAutoRefresh", out var tiar))
                        config.TmIndexAutoRefresh = Convert.ToBoolean(tiar);
                    if (json.TryGetValue("tmIndexRefreshTime", out var tirt) && tirt is string tirts)
                        config.TmIndexRefreshTime = IsValidTime(tirts) ? tirts.Trim() : "02:00";
                    if (json.TryGetValue("tmIndexLastRun", out var tilr) && tilr is string tilrs)
                        config.TmIndexLastRun = (tilrs ?? string.Empty).Trim();
                    if (json.TryGetValue("inboxWatchFolder", out var iwf) && iwf is string iwfs)
                        config.InboxWatchFolder = (iwfs ?? string.Empty).Trim();
                    if (json.TryGetValue("inboxOutputFolder", out var iof) && iof is string iofs)
                        config.InboxOutputFolder = (iofs ?? string.Empty).Trim();
                    if (json.TryGetValue("inboxProjectRoot", out var ipr) && ipr is string iprs)
                        config.InboxProjectRoot = (iprs ?? string.Empty).Trim();
                    if (json.TryGetValue("inboxSourceLang", out var isl) && isl is string isls)
                        config.InboxSourceLang = string.IsNullOrWhiteSpace(isls) ? "zh-CN" : isls.Trim();
                    if (json.TryGetValue("inboxTargetLang", out var itl) && itl is string itls)
                        config.InboxTargetLang = string.IsNullOrWhiteSpace(itls) ? "en-US" : itls.Trim();
                    if (json.TryGetValue("inboxReportFormat", out var irf) && irf is string irfs)
                        config.InboxReportFormat = string.IsNullOrWhiteSpace(irfs) ? "excel" : irfs.Trim().ToLowerInvariant();
                    if (json.TryGetValue("inboxAutoStart", out var ias))
                        config.InboxAutoStart = Convert.ToBoolean(ias);
                    if (json.TryGetValue("termBaseUrl", out var tbu) && tbu is string tb)
                        config.TermBaseUrl = (tb ?? string.Empty).Trim();
                    if (json.TryGetValue("domain", out var dom) && dom is string d)
                        config.Domain = string.IsNullOrWhiteSpace(d) ? Glossaries.DomainTree.DefaultDomain : d.Trim();
                    if (json.TryGetValue("translationCenterFolders", out var bm))
                        config.Folders = ParseFolders(bm);
                    else if (json.TryGetValue("translationCenterBookmarks", out var legacy))
                    {
                        // 旧扁平列表 → 自动迁移进"常用地址"目录
                        var migrated = new BookmarkFolder { name = "常用地址" };
                        foreach (var item in ParseItems(legacy)) migrated.items.Add(item);
                        if (migrated.items.Count > 0) config.Folders = new List<BookmarkFolder> { migrated };
                    }
                    if (json.TryGetValue("processCards", out var pc))
                        config.ProcessCards = ParseProcessCards(pc);
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

        private static List<ProcessCard> ParseProcessCards(object value)
        {
            var cards = new List<ProcessCard>();
            var items = value as System.Collections.IEnumerable;
            if (items == null) return cards;
            foreach (var o in items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var card = new ProcessCard
                {
                    name = d.TryGetValue("name", out var n) ? (n as string ?? "未命名") : "未命名",
                    steps = new List<string>(),
                };
                if (d.TryGetValue("steps", out var st) && st is System.Collections.IEnumerable se)
                {
                    foreach (var s in se) { var sk = s as string; if (!string.IsNullOrWhiteSpace(sk)) card.steps.Add(sk); }
                }
                if (string.IsNullOrEmpty(card.name)) card.name = "未命名";
                if (card.steps.Count > 0) cards.Add(card);
            }
            return cards;
        }

        /// <summary>配置窗口点确定时调用：null 表示不改动该字段，保留文件里其余内容。</summary>
        public static void Save(string apiKey = null, string llmBaseUrl = null, string llmModel = null,
                               string translationCenterUrl = null, List<BookmarkFolder> folders = null,
                               string tmScanDirectory = null, string termBaseUrl = null,
                               List<ProcessCard> processCards = null, string domain = null,
                               string inboxWatchFolder = null, string inboxOutputFolder = null,
                               string inboxProjectRoot = null, string inboxSourceLang = null,
                               string inboxTargetLang = null, bool? inboxAutoStart = null,
                               string inboxReportFormat = null,
                               bool? tmIndexAutoRefresh = null, string tmIndexRefreshTime = null,
                               string tmIndexLastRun = null)
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
                if (tmIndexAutoRefresh != null) doc["tmIndexAutoRefresh"] = tmIndexAutoRefresh.Value;
                if (tmIndexRefreshTime != null)
                    doc["tmIndexRefreshTime"] = IsValidTime(tmIndexRefreshTime) ? tmIndexRefreshTime.Trim() : "02:00";
                if (tmIndexLastRun != null) doc["tmIndexLastRun"] = tmIndexLastRun;
                if (termBaseUrl != null) doc["termBaseUrl"] = termBaseUrl;
                if (domain != null) doc["domain"] = domain;
                if (inboxWatchFolder != null) doc["inboxWatchFolder"] = inboxWatchFolder;
                if (inboxOutputFolder != null) doc["inboxOutputFolder"] = inboxOutputFolder;
                if (inboxProjectRoot != null) doc["inboxProjectRoot"] = inboxProjectRoot;
                if (inboxSourceLang != null) doc["inboxSourceLang"] = inboxSourceLang;
                if (inboxTargetLang != null) doc["inboxTargetLang"] = inboxTargetLang;
                if (inboxReportFormat != null) doc["inboxReportFormat"] = inboxReportFormat;
                if (inboxAutoStart != null) doc["inboxAutoStart"] = inboxAutoStart.Value;
                if (folders != null)
                {
                    doc["translationCenterFolders"] = folders;
                    doc.Remove("translationCenterBookmarks");
                }
                if (processCards != null) doc["processCards"] = processCards;
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
