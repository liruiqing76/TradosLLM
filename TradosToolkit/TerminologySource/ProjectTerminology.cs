using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Sdl.Core.Globalization;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 项目术语挂载：把插件的原生术语源（tradostoolkit://glossary）写进项目的术语库配置，
    /// 使项目"术语库"插件点直接生效（不装 MultiTerm 也可用）。
    ///
    /// 原理：Studio 的 Termbase 只需一个 SettingsXML，其中 &lt;Path&gt; 即术语源 URI；
    /// 我们的 URI 会被 NativeTerminologyProviderFactory 识别并实例化 Provider，
    /// 因此挂载的本质就是往 project.GetTermbaseConfiguration().Termbases 里塞一个本地术语库条目。
    ///
    /// 两套术语源都支持：
    ///   local  —— 指向本地 SQLite（GlossaryDb.term_entries），可写；
    ///   online —— 指向线上服务地址（ToolkitConfig.TermBaseUrl），只读。
    /// </summary>
    public static class ProjectTerminology
    {
        /// <summary>挂载后的术语库显示名。</summary>
        public static string DisplayName(string kind)
        {
            return "TradosToolkit · " + TermSourceKind.Label(kind);
        }

        /// <summary>
        /// 构造一个指向插件术语源的 Termbase 条目。
        /// </summary>
        public static Termbase BuildTermbase(string kind, string baseUrl, string srcLang, string tgtLang, string domain)
        {
            var uri = NativeTerminologyProviderHelper.BuildUri(kind, baseUrl, srcLang, tgtLang, domain);
            return new LocalTermbase(uri.ToString(), DisplayName(kind), BuildSettingsXml(uri.ToString()));
        }

        /// <summary>
        /// SettingsXML 格式（Studio 2019 内嵌于 .sdlproj 的 &lt;SettingsXml&gt;）：
        /// TermbaseSettings/Path 指向术语源；Local=true 允许本地（无服务端）术语库。
        /// </summary>
        public static string BuildSettingsXml(string path)
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-16", null),
                new XElement("TermbaseSettings",
                    new XAttribute("xmlns:xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                    new XAttribute("xmlns:xsd", "http://www.w3.org/2001/XMLSchema"),
                    new XElement("Path", path),
                    new XElement("IsOpen", "false"),
                    new XElement("Filter", "0"),
                    new XElement("FilterHighlight", "true"),
                    new XElement("Layout", "0"),
                    new XElement("Local", "true")));
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>该术语源是否已挂载到项目（按 Path 判断，避免重复挂）。</summary>
        public static bool IsMounted(TermbaseConfiguration config, string srcLang, string tgtLang, string domain)
        {
            if (config == null || config.Termbases == null) return false;
            return config.Termbases.Any(t => HasPath(t, srcLang, tgtLang, domain));
        }

        private static bool HasPath(Termbase termbase, string srcLang, string tgtLang, string domain)
        {
            if (termbase == null || string.IsNullOrEmpty(termbase.SettingsXML)) return false;
            try
            {
                var doc = XDocument.Parse(termbase.SettingsXML);
                var path = doc.Root?.Element("Path")?.Value;
                if (string.IsNullOrEmpty(path) ||
                    !path.StartsWith(NativeTerminologyProviderHelper.SchemeActivation, StringComparison.OrdinalIgnoreCase))
                    return false;
                var uri = new Uri(path);
                return string.Equals(NativeTerminologyProviderHelper.GetQueryParam(uri, "src"), srcLang, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(NativeTerminologyProviderHelper.GetQueryParam(uri, "tgt"), tgtLang, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(NativeTerminologyProviderHelper.GetQueryParam(uri, "domain") ?? "", domain ?? "", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 把插件术语源挂到项目术语库配置上（幂等：同语言对+领域已存在则跳过）。
        /// 返回挂载的术语库个数；0 表示没有可挂的（如本地库为空且未配置线上服务）。
        ///
        /// 语言对**以项目为准**：源语言取项目源语言，目标语言取项目各目标语言；
        /// localPairs 仅用于判断"本地库在这对语言上是否有内容"，不再决定挂载哪些语言对
        /// ——否则切换项目后会沿用上一个项目的目标语言。
        /// </summary>
        /// <param name="localPairs">本地库已有内容的语言对（src,tgt），来自 GlossaryDb.GetAllTermPairs()。</param>
        public static int Mount(FileBasedProject project, IEnumerable<string[]> localPairs,
                                string onlineBaseUrl, string domain)
        {
            if (project == null) return 0;

            var projectPairs = ProjectPairs(project);
            if (projectPairs.Count == 0) return 0;

            var dbPairs = new HashSet<string>(
                (localPairs ?? Enumerable.Empty<string[]>())
                    .Where(p => p != null && p.Length >= 2 && !string.IsNullOrWhiteSpace(p[0]) && !string.IsNullOrWhiteSpace(p[1]))
                    .Select(p => p[0] + "|" + p[1]),
                StringComparer.OrdinalIgnoreCase);

            var termbases = new List<Tuple<string, string, string, string>>(); // kind, base, src, tgt

            foreach (var pair in projectPairs)
            {
                // 本地源：仅当本地库在该语言对上确有内容时才挂，避免空库占位。
                if (dbPairs.Contains(pair[0] + "|" + pair[1]))
                    termbases.Add(Tuple.Create(TermSourceKind.Local, (string)null, pair[0], pair[1]));

                // 线上源：配置了服务地址即按项目语言对挂载（服务端自行决定有无数据）。
                if (!string.IsNullOrWhiteSpace(onlineBaseUrl))
                    termbases.Add(Tuple.Create(TermSourceKind.Online, onlineBaseUrl, pair[0], pair[1]));
            }

            if (termbases.Count == 0)
                return 0;

            var config = project.GetTermbaseConfiguration();
            if (config == null) config = new TermbaseConfiguration();
            if (config.Termbases == null) config.Termbases = new List<Termbase>();
            if (config.LanguageIndexes == null) config.LanguageIndexes = new List<TermbaseLanguageIndex>();

            // 清掉本项目语言对之外的历史挂载（切换项目/改语向后遗留的旧语言对），
            // 否则 Studio 术语插入点仍会显示上一个项目的目标语言。
            var stale = config.Termbases.Where(t => IsPluginTermbase(t) && !IsForProjectPairs(t, projectPairs)).ToList();
            foreach (var t in stale)
            {
                config.Termbases.Remove(t);
                ToolkitLog.Info("项目术语挂载：移除过期术语库 " + t.Name + "（不属于当前项目语言对）");
            }

            var mounted = 0;
            foreach (var item in termbases)
            {
                if (IsMounted(config, item.Item3, item.Item4, domain)) continue;

                Termbase tb;
                try
                {
                    tb = BuildTermbase(item.Item1, item.Item2, item.Item3, item.Item4, domain);
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("项目术语挂载：构造术语库失败 " + item.Item3 + "-" + item.Item4, e);
                    continue;
                }
                config.Termbases.Add(tb);
                mounted++;
            }

            foreach (var pair in projectPairs)
            {
                TryAddLanguageIndex(config, project, pair[0]);
                TryAddLanguageIndex(config, project, pair[1]);
            }

            if (mounted == 0 && stale.Count == 0) return 0;

            // 坑位：Terminology 引擎要求至少一个术语库；但我们只在挂载了内容时才更新，
            // 因此这里 Termbases 必非空。仍做保护，防止把空配置写回导致异常。
            if (config.Termbases.Count == 0) return 0;

            try
            {
                project.UpdateTermbaseConfiguration(config);
                project.Save();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：UpdateTermbaseConfiguration 失败", e);
                return 0;
            }

            ToolkitLog.Info($"项目术语挂载：新增 {mounted} 个、清理 {stale.Count} 个术语库（domain={domain}）");
            return mounted;
        }

        /// <summary>是否本插件挂载的术语库（按 Path 是否为 tradostoolkit:// URI 判断）。</summary>
        private static bool IsPluginTermbase(Termbase termbase)
        {
            var path = SettingsPath(termbase);
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith(NativeTerminologyProviderHelper.SchemeActivation, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>该插件术语库的 src/tgt 是否属于给定项目语言对集合。</summary>
        private static bool IsForProjectPairs(Termbase termbase, List<string[]> projectPairs)
        {
            var path = SettingsPath(termbase);
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var uri = new Uri(path);
                var src = NativeTerminologyProviderHelper.GetQueryParam(uri, "src");
                var tgt = NativeTerminologyProviderHelper.GetQueryParam(uri, "tgt");
                return projectPairs.Any(p =>
                    string.Equals(p[0], src, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p[1], tgt, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string SettingsPath(Termbase termbase)
        {
            if (termbase == null || string.IsNullOrEmpty(termbase.SettingsXML)) return null;
            try
            {
                return XDocument.Parse(termbase.SettingsXML).Root?.Element("Path")?.Value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>项目自身的语言对：源语言 × 各目标语言。</summary>
        public static List<string[]> ProjectPairs(FileBasedProject project)
        {
            var pairs = new List<string[]>();
            if (project == null) return pairs;
            try
            {
                var info = project.GetProjectInfo();
                var src = info.SourceLanguage?.IsoAbbreviation;
                if (string.IsNullOrEmpty(src)) return pairs;
                foreach (var tgt in info.TargetLanguages ?? new Language[0])
                {
                    var code = tgt?.IsoAbbreviation;
                    if (!string.IsNullOrEmpty(code)) pairs.Add(new[] { src, code });
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：读取项目语言失败", e);
            }
            return pairs;
        }

        private static void TryAddLanguageIndex(TermbaseConfiguration config, FileBasedProject project, string isoCode)
        {
            if (string.IsNullOrWhiteSpace(isoCode)) return;
            bool exists = config.LanguageIndexes.Any(li =>
                li.ProjectLanguage != null &&
                string.Equals(li.ProjectLanguage.IsoAbbreviation, isoCode, StringComparison.OrdinalIgnoreCase));
            if (exists) return;
            try
            {
                config.LanguageIndexes.Add(new TermbaseLanguageIndex(new Language(isoCode), isoCode));
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：添加语言索引失败 " + isoCode, e);
            }
        }
    }
}
