using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 客户项目模板（功能 #3）：把"某客户的标准做法"固化成 JSON 文件，
    /// 新建项目时一键套用（语言对/领域/TM/风格指南/后续自动步骤）。
    /// 一客户一文件：%APPDATA%\TradosToolkit\templates\{name}.json，便于手工维护与组内拷贝共享。
    /// </summary>
    public class ClientProjectTemplate
    {
        /// <summary>模板名（= 客户名，作文件名）。</summary>
        public string name = string.Empty;
        public string sourceLang = string.Empty;
        public List<string> targetLangs = new List<string>();
        /// <summary>主 TM 文件路径（.sdltm，绝对路径；空 = 不挂 TM）。</summary>
        public string tmFile = string.Empty;
        /// <summary>绑定的术语领域（套用后写入全局 config.domain）。</summary>
        public string domain = string.Empty;
        /// <summary>客户风格指南（套用后写入全局 config.styleGuide，供 #5 提示词注入）。</summary>
        public string styleGuide = string.Empty;
        /// <summary>预翻译匹配阈值百分比（0-100；0 = 使用项目模板默认）。</summary>
        public int pretranslateThreshold;
        /// <summary>建项目完成后按序执行的自动步骤（pretranslate|analyze|wordcount|updatetm|export）。</summary>
        public List<string> postSteps = new List<string>();
        /// <summary>备注（交付要求、客户禁忌等）。</summary>
        public string notes = string.Empty;
        public DateTime createdAt;
        public DateTime updatedAt;

        public string UpdatedAtText => updatedAt == default(DateTime) ? "—" : updatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        // WPF 绑定友好属性（XAML 用 PascalCase）
        public string Name => name;
        public string SourceLang => sourceLang;
        public string TargetLangs => string.Join(",", targetLangs ?? new List<string>());
        public string TmFile => tmFile;
        public string Domain => domain;
        public string StyleGuideText => string.IsNullOrWhiteSpace(styleGuide) ? "" : styleGuide;
    }

    /// <summary>客户模板的文件系统存取层（JavaScriptSerializer，net48 无 System.Text.Json）。</summary>
    public static class ClientTemplateStore
    {
        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static string TemplatesDir
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       "TradosToolkit", "templates");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string FilePathOf(string name)
        {
            // 文件名安全化：禁止路径分隔符与非法字符
            var clean = string.Concat((name ?? string.Empty).Trim().Select(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_'));
            if (clean.Length == 0) throw new ArgumentException("模板名无效");
            return Path.Combine(TemplatesDir, clean + ".json");
        }

        public static List<ClientProjectTemplate> List()
        {
            var list = new List<ClientProjectTemplate>();
            foreach (var file in Directory.GetFiles(TemplatesDir, "*.json"))
            {
                try
                {
                    var t = Get(Path.GetFileNameWithoutExtension(file));
                    if (t != null) list.Add(t);
                }
                catch (Exception e)
                {
                    ToolkitLog.Warn("客户模板读取失败: " + file, e);
                }
            }
            return list.OrderByDescending(t => t.updatedAt).ToList();
        }

        public static ClientProjectTemplate Get(string name)
        {
            var file = FilePathOf(name);
            if (!File.Exists(file)) return null;
            var doc = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
            if (doc == null) return null;
            var t = new ClientProjectTemplate
            {
                name = Str(doc, "name") ?? Path.GetFileNameWithoutExtension(file),
                sourceLang = Str(doc, "sourceLang") ?? string.Empty,
                tmFile = Str(doc, "tmFile") ?? string.Empty,
                domain = Str(doc, "domain") ?? string.Empty,
                styleGuide = Str(doc, "styleGuide") ?? string.Empty,
                notes = Str(doc, "notes") ?? string.Empty,
            };
            if (doc.TryGetValue("targetLangs", out var tls) && tls is System.Collections.IEnumerable items && !(tls is string))
                foreach (var o in items)
                {
                    var s = o as string;
                    if (!string.IsNullOrWhiteSpace(s)) t.targetLangs.Add(s);
                }
            if (doc.TryGetValue("postSteps", out var ps) && ps is System.Collections.IEnumerable steps && !(ps is string))
                foreach (var o in steps)
                {
                    var s = o as string;
                    if (!string.IsNullOrWhiteSpace(s)) t.postSteps.Add(s);
                }
            if (doc.TryGetValue("pretranslateThreshold", out var pt) && pt is double d)
                t.pretranslateThreshold = Math.Max(0, Math.Min(100, (int)d));
            if (doc.TryGetValue("createdAt", out var ca) && ca is string cas && DateTime.TryParse(cas, out var cdt))
                t.createdAt = cdt;
            if (doc.TryGetValue("updatedAt", out var ua) && ua is string uas && DateTime.TryParse(uas, out var udt))
                t.updatedAt = udt;
            return t;
        }

        public static void Save(ClientProjectTemplate t)
        {
            if (t == null || string.IsNullOrWhiteSpace(t.name))
                throw new ArgumentException("模板名不能为空");
            t.updatedAt = DateTime.Now;
            var doc = new Dictionary<string, object>
            {
                { "name", t.name },
                { "sourceLang", t.sourceLang ?? string.Empty },
                { "targetLangs", t.targetLangs ?? new List<string>() },
                { "tmFile", t.tmFile ?? string.Empty },
                { "domain", t.domain ?? string.Empty },
                { "styleGuide", t.styleGuide ?? string.Empty },
                { "pretranslateThreshold", t.pretranslateThreshold },
                { "postSteps", t.postSteps ?? new List<string>() },
                { "notes", t.notes ?? string.Empty },
                { "createdAt", t.createdAt == default(DateTime) ? DateTime.Now : t.createdAt },
                { "updatedAt", t.updatedAt },
            };
            File.WriteAllText(FilePathOf(t.name), Json.Serialize(doc));
            ToolkitLog.Info("客户模板已保存: " + t.name);
        }

        public static void Delete(string name)
        {
            var file = FilePathOf(name);
            if (File.Exists(file))
            {
                File.Delete(file);
                ToolkitLog.Info("客户模板已删除: " + name);
            }
        }

        private static string Str(Dictionary<string, object> doc, string key) =>
            doc.TryGetValue(key, out var v) ? v as string : null;
    }
}