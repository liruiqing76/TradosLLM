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
        /// <summary>翻译中心"常用地址"条目（config.json 的 translationCenterBookmarks）。</summary>
        public class BookmarkItem
        {
            public string name;
            public string url;
        }

        public string TmUrl = string.Empty;
        public string ApiKey = string.Empty;
        public string LlmBaseUrl = string.Empty;
        public string LlmModel = string.Empty;
        /// <summary>翻译中心主页地址（config.json 的 translationCenterUrl，空 = bing）。</summary>
        public string TranslationCenterUrl = string.Empty;
        /// <summary>LLM 并发请求数（config.json 的 llmConcurrency，缺省 6）。</summary>
        public int LlmConcurrency = 6;
        /// <summary>翻译中心常用地址；键缺失时用内置默认，键存在（含空数组）则完全按文件。</summary>
        public List<BookmarkItem> Bookmarks = DefaultBookmarks();

        public static List<BookmarkItem> DefaultBookmarks() => new List<BookmarkItem>
        {
            new BookmarkItem { name = "Bing", url = "https://www.bing.com" },
            new BookmarkItem { name = "百度", url = "https://www.baidu.com" },
            new BookmarkItem { name = "百度翻译", url = "https://fanyi.baidu.com" },
            new BookmarkItem { name = "Trados 社区", url = "https://community.rws.com" },
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
                    if (json.TryGetValue("translationCenterBookmarks", out var bm) &&
                        bm is IEnumerable<object> items)
                    {
                        var list = new List<BookmarkItem>();
                        foreach (var o in items)
                        {
                            if (!(o is Dictionary<string, object> d)) continue;
                            var item = new BookmarkItem
                            {
                                name = d.TryGetValue("name", out var n) ? n as string : null,
                                url = d.TryGetValue("url", out var u) ? u as string : null,
                            };
                            if (!string.IsNullOrWhiteSpace(item.url)) list.Add(item);
                        }
                        config.Bookmarks = list;
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

        /// <summary>配置窗口点确定时调用：null 表示不改动该字段，保留文件里其余内容。</summary>
        public static void Save(string apiKey = null, string llmBaseUrl = null, string llmModel = null,
                               string translationCenterUrl = null, List<BookmarkItem> bookmarks = null)
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
                if (bookmarks != null) doc["translationCenterBookmarks"] = bookmarks;
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
