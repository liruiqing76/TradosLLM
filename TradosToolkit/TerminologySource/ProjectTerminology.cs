using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Sdl.Core.Globalization;
using Sdl.ProjectApi.TermbaseApi;
using Sdl.ProjectAutomation.FileBased;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 项目术语挂载：把插件的原生术语源（tradostoolkit://glossary）写进项目的术语库配置，
    /// 使项目"术语库"插件点直接生效（不装 MultiTerm 也可用）。
    ///
    /// 原理：Studio 的 Termbase 只需一个 SettingsXML，其中 &lt;Path&gt; 即术语源 URI；
    /// 我们的 URI 会被 NativeTerminologyProviderFactory 识别并实例化 Provider。
    ///
    /// 为什么不能走公开 API：
    ///   FileBasedProject.UpdateTermbaseConfiguration 只认 LocalTermbase / ServerTermbase，
    ///   其中 LocalTermbase 会先 File.Exists 校验 Path，再强制把 SettingsXML 改写成
    ///   Local=true（SetTermbaseSettingsPathInfo）。我们的 tradostoolkit:// 不是文件，
    ///   必然抛 "The specified termbase does not exist"。
    ///
    /// 正确做法（Studio 内部落库同款）：
    ///   取底层 Sdl.ProjectApi.IProject.TermbaseConfiguration（该属性可写），
    ///   Copy 一份 → 往 Termbases 里 Add 一个 IsCustom=true 的条目 →
    ///   把这份"内容已变"的副本整体赋回。自定义条目会被写进项目 SettingsBundle 的
    ///   TerminologyProviderSettings/Termbases 分组（Studio 术语库对话框落库的就是这个形状），
    ///   由已注册的 Terminology Provider 工厂实例化，不做文件校验。
    ///   注意必须赋回副本而非原地改同一个实例：setter 用 Equals 短路，
    ///   内容相等的同一引用会被直接忽略、不落库。
    ///
    /// 两套术语源都支持：
    ///   local  —— 指向本地 SQLite（GlossaryDb.term_entries），可写；
    ///   online —— 指向线上服务地址（ToolkitConfig.TermBaseUrl），只读。
    /// </summary>
    public static class ProjectTerminology
    {
        /// <summary>挂载后的术语库显示名前缀。</summary>
        public static string DisplayName(string kind)
        {
            return "TradosToolkit · " + TermSourceKind.Label(kind);
        }

        /// <summary>
        /// 术语库显示名（带语向后缀）。
        /// Studio 的 ProjectTermbases 会拒绝同名条目（CanAdd：Name 或 SettingsXml 相同即拒绝），
        /// 而 DisplayName 只区分 local/online，多目标语言项目必然重名，因此必须带上语向。
        /// </summary>
        private static string TermbaseName(string kind, string srcLang, string tgtLang)
        {
            return DisplayName(kind) + " (" + srcLang + "-" + tgtLang + ")";
        }

        /// <summary>
        /// SettingsXML 格式（Studio 2019 内嵌于 .sdlproj 的 &lt;SettingsXml&gt;）：
        /// TermbaseSettings/Path 指向术语源。
        ///
        /// 关键：必须声明 &lt;IsCustom&gt;true&lt;/IsCustom&gt; 且 &lt;Local&gt;false&lt;/Local&gt;。
        /// 这是 Studio 判定"该条目由自定义 Terminology Provider 提供"的标记：
        ///   - Local=true / IsCustom=false —— 文件型本地术语库，Studio 会校验 Path 所指的
        ///     .sdltb/.ttb 文件是否存在，我们的 tradostoolkit:// URI 不是文件，挂载失败；
        ///   - IsCustom=true / Local=false —— 交给已注册的 Terminology Provider 工厂实例化，
        ///     不做文件校验，且 GetCustomTermbasesXml 会把它写进
        ///     TerminologyProviderSettings/Termbases 分组。
        /// （Studio 自己的术语库对话框落库的条目就是这个形状。）
        /// </summary>
        public static string BuildSettingsXml(string path)
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-16", null),
                new XElement("TermbaseSettings",
                    new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                    new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
                    new XElement("Path", path),
                    new XElement("IsOpen", "false"),
                    new XElement("Filter", "0"),
                    new XElement("FilterHighlight", "false"),
                    new XElement("Layout", "0"),
                    new XElement("Local", "false"),
                    new XElement("IsCustom", "true")));
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>该术语源是否已挂载到项目（按 Path 判断，避免重复挂）。</summary>
        private static bool IsMounted(IEnumerable<IProjectTermbase> termbases, string srcLang, string tgtLang, string domain)
        {
            if (termbases == null) return false;
            return termbases.Any(t => HasPath(t, srcLang, tgtLang, domain));
        }

        private static bool HasPath(IProjectTermbase termbase, string srcLang, string tgtLang, string domain)
        {
            if (termbase == null || string.IsNullOrEmpty(termbase.SettingsXml)) return false;
            try
            {
                var doc = XDocument.Parse(termbase.SettingsXml);
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
        /// 语言对优先级：
        ///   1) currentPair —— 编辑器当前打开文件的语向（ActiveDocument.ActiveFile.Language）；
        ///   2) 取不到时回退到项目语言对（源语言 × 各目标语言）。
        /// 之所以以"当前编辑语向"为先：Studio 的 LanguageIndexes 只映射到默认术语库
        /// （Termbases 列表首项），把项目多个目标语言一股脑挂上去会让首项与当前语向错位，
        /// 表现为"打开英德项目、术语插入点却显示另一个语言对"。
        /// </summary>
        /// <param name="currentPair">编辑器当前语向 [src,tgt]；为 null 时回退项目语言对。</param>
        public static int Mount(FileBasedProject project, string[] currentPair,
                                string onlineBaseUrl, string domain)
        {
            if (project == null) return 0;

            var apiProject = GetApiProject(project);
            if (apiProject == null)
            {
                ToolkitLog.Error("项目术语挂载：无法通过反射取到底层 ProjectApi 项目实例，跳过");
                return 0;
            }

            var projectPairs = ProjectPairs(project);
            var allPairs = new List<string[]>();
            if (currentPair != null && currentPair.Length >= 2 &&
                !string.IsNullOrWhiteSpace(currentPair[0]) && !string.IsNullOrWhiteSpace(currentPair[1]))
            {
                // 当前编辑语向优先，且排在最前 —— 保证 Termbases[0] 就是它。
                allPairs.Add(new[] { currentPair[0], currentPair[1] });
                ToolkitLog.Info($"项目术语挂载：使用当前编辑语向 {currentPair[0]}-{currentPair[1]}");
            }

            foreach (var p in projectPairs)
            {
                if (allPairs.Any(x => string.Equals(x[0], p[0], StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(x[1], p[1], StringComparison.OrdinalIgnoreCase)))
                    continue;
                allPairs.Add(p);
            }

            if (allPairs.Count == 0)
            {
                ToolkitLog.Error("项目术语挂载：既无当前编辑语向、项目语言对也为空，跳过");

                return 0;
            }

            // 注意：本地源不再以"库里此刻是否恰好有该语言对"为挂载前提。
            // 原因：库内语言代码写法可能与项目 ISO 代码不一致（en-US / en_US / en），
            // 一旦字符串不相等就会"一个都不挂"，导致后续新增术语永远不生效且极难排查。
            // 语言对一律以项目为准，空库挂上也无害；库匹配交由 Provider 查询时做规范化。
            var termbases = new List<Tuple<string, string, string, string>>(); // kind, base, src, tgt

            foreach (var pair in allPairs)
            {
                termbases.Add(Tuple.Create(TermSourceKind.Local, (string)null, pair[0], pair[1]));

                // 线上源：配置了服务地址即按项目语言对挂载（服务端自行决定有无数据）。
                if (!string.IsNullOrWhiteSpace(onlineBaseUrl))
                    termbases.Add(Tuple.Create(TermSourceKind.Online, onlineBaseUrl, pair[0], pair[1]));
            }

            if (termbases.Count == 0)
                return 0;

            IProjectTermbaseConfiguration config;
            try
            {
                config = apiProject.TermbaseConfiguration;
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：读取项目术语库配置失败", e);
                return 0;
            }
            if (config == null || config.Termbases == null)
            {
                ToolkitLog.Error("项目术语挂载：项目术语库配置为空，跳过");
                return 0;
            }

            // 必须在副本上改：setter 用 Equals 短路，原地改同一引用会被忽略、不落库。
            IProjectTermbaseConfiguration next;
            try
            {
                next = config.Copy();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：复制项目术语库配置失败", e);
                return 0;
            }
            if (next == null || next.Termbases == null)
            {
                ToolkitLog.Error("项目术语挂载：复制的术语库配置为空，跳过");
                return 0;
            }

            var list = next.Termbases;

            // 清掉本项目语言对之外的历史挂载（切换项目/改语向后遗留的旧语言对），
            // 否则 Studio 术语插入点仍会显示上一个项目的目标语言。
            var stale = list.Where(t => IsPluginTermbase(t) && !IsForProjectPairs(t, allPairs)).ToList();
            foreach (var t in stale)
            {
                list.Remove(t);
                ToolkitLog.Info("项目术语挂载：移除过期术语库 " + t.Name + "（不属于当前项目语言对）");
            }

            // 同一语言对只保留一条插件术语库：历史上重复挂载会堆出多条同名记录
            // （Studio 术语库对话框里表现为灰色残留项），一并去重。
            var dedup = 0;
            foreach (var item in termbases)
            {
                var extras = list
                    .Where(t => IsPluginTermbase(t) && !IsSameKind(t, item.Item1) && IsPairOf(t, new[] { item.Item3, item.Item4 }))
                    .ToList();
                foreach (var t in extras)
                {
                    list.Remove(t);
                    dedup++;
                    ToolkitLog.Info("项目术语挂载：去重术语库 " + t.Name + "（同语言对重复）");
                }
            }

            var mounted = 0;
            foreach (var item in termbases)
            {
                if (IsMounted(list, item.Item3, item.Item4, domain)) continue;

                try
                {
                    var uri = NativeTerminologyProviderHelper.BuildUri(item.Item1, item.Item2, item.Item3, item.Item4, domain);
                    var tb = next.Factory.CreateTermbase(
                        TermbaseName(item.Item1, item.Item3, item.Item4),
                        BuildSettingsXml(uri.ToString()),
                        null,
                        true);
                    list.Add(tb);
                    mounted++;
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("项目术语挂载：构造术语库失败 " + item.Item3 + "-" + item.Item4, e);
                }
            }

            // 语言索引名必须与 Studio 期望的一致（用语言显示名 English/German 这类），
            // 不能用 ISO 代码 de-DE —— 否则项目设置里该目标语言显示"无映射"，
            // Studio 会直接跳过该目标语言的术语查找（表现为术语识别空白）。
            var indexChanged = false;
            foreach (var pair in allPairs)
            {
                indexChanged |= TryAddLanguageIndex(next, pair[0]);
                indexChanged |= TryAddLanguageIndex(next, pair[1]);
            }

            if (mounted == 0 && stale.Count == 0 && dedup == 0 && !indexChanged) return 0;

            // 坑位：Terminology 引擎要求至少一个术语库；但我们只在挂载了内容时才更新，
            // 因此这里 Termbases 必非空。仍做保护，防止把空配置写回导致异常。
            if (list.Count == 0) return 0;

            try
            {
                // 走底层可写属性，自定义条目由 GetCustomTermbasesXml 落进
                // TerminologyProviderSettings/Termbases 分组；不做文件校验。
                apiProject.TermbaseConfiguration = next;
                project.Save();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：写入项目术语库配置失败", e);
                return 0;
            }

            ToolkitLog.Info($"项目术语挂载：语言对 {string.Join(",", allPairs.Select(p => p[0] + "-" + p[1]))}，"
                            + $"新增 {mounted} 个、清理 {stale.Count + dedup} 个术语库（domain={domain}）");
            return mounted;
        }

        /// <summary>
        /// 通过反射取 FileBasedProject 内部持有的 Sdl.ProjectApi.IProject。
        /// 公开 API 无法表达自定义 Terminology Provider 术语库，必须拿到这层才能注入。
        /// （FileBasedProject 有 internal 属性 InternalProject / private 字段 _project。）
        /// </summary>
        private static Sdl.ProjectApi.IProject GetApiProject(FileBasedProject project)
        {
            if (project == null) return null;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            for (var type = project.GetType(); type != null; type = type.BaseType)
            {
                try
                {
                    var prop = type.GetProperty("InternalProject", flags);
                    if (prop != null && typeof(Sdl.ProjectApi.IProject).IsAssignableFrom(prop.PropertyType))
                    {
                        var value = prop.GetValue(project, null) as Sdl.ProjectApi.IProject;
                        if (value != null) return value;
                    }

                    var field = type.GetField("_project", flags);
                    if (field != null)
                    {
                        var value = field.GetValue(project) as Sdl.ProjectApi.IProject;
                        if (value != null) return value;
                    }
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("项目术语挂载：反射读取 ProjectApi 项目失败（" + type.Name + "）", e);
                }
            }
            return null;
        }

        /// <summary>该术语库是否指定来源类型（local/online）。</summary>
        private static bool IsSameKind(IProjectTermbase termbase, string kind)
        {
            var path = SettingsPath(termbase);
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var uri = new Uri(path);
                return string.Equals(NativeTerminologyProviderHelper.GetQueryParam(uri, "kind"),
                                     TermSourceKind.Normalize(kind), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsPairOf(IProjectTermbase termbase, string[] pair)
        {
            var path = SettingsPath(termbase);
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var uri = new Uri(path);
                return string.Equals(NativeTerminologyProviderHelper.GetQueryParam(uri, "src"), pair[0], StringComparison.OrdinalIgnoreCase)
                    && string.Equals(NativeTerminologyProviderHelper.GetQueryParam(uri, "tgt"), pair[1], StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 取编辑器当前打开文件的语向 [src,tgt]；没有活动文档时返回 null（调用方回退项目语言对）。
        /// 必须在 Studio UI 线程调用（宿主自动化对象跨线程不可用）。
        /// </summary>
        public static string[] CurrentPair()
        {
            try
            {
                var editor = Sdl.TranslationStudioAutomation.IntegrationApi.SdlTradosStudio.Application
                    .GetController<Sdl.TranslationStudioAutomation.IntegrationApi.EditorController>();
                var doc = editor?.ActiveDocument;
                var file = doc?.ActiveFile;
                var tgt = file?.Language?.IsoAbbreviation;
                if (string.IsNullOrWhiteSpace(tgt)) return null;

                var src = doc?.Project?.GetProjectInfo()?.SourceLanguage?.IsoAbbreviation;
                if (string.IsNullOrWhiteSpace(src)) return null;

                return new[] { src, tgt };
            }
            catch (Exception e)
            {
                ToolkitLog.Error("读取当前编辑语向失败", e);

                return null;
            }
        }

        /// <summary>
        /// 取当前激活项目的语言对 [src,tgt]（无活动文档时的回退）。
        /// 必须在 Studio UI 线程调用。项目有多个目标语言时取第一个并记日志。
        /// </summary>
        public static string[] ActiveProjectPair()
        {
            try
            {
                var ctl = Sdl.TranslationStudioAutomation.IntegrationApi.SdlTradosStudio.Application
                    .GetController<Sdl.TranslationStudioAutomation.IntegrationApi.ProjectsController>();
                var project = ctl?.CurrentProject as Sdl.ProjectAutomation.FileBased.FileBasedProject;
                if (project == null) return null;

                var info = project.GetProjectInfo();
                var src = info?.SourceLanguage?.IsoAbbreviation;
                var tgts = info?.TargetLanguages;
                if (string.IsNullOrWhiteSpace(src) || tgts == null || tgts.Length == 0) return null;

                if (tgts.Length > 1)
                    ToolkitLog.Info("项目多目标语言：" + tgts.Length + " 个，无活动文档时取第一个 "
                                    + tgts[0]?.IsoAbbreviation);

                var tgt = tgts[0]?.IsoAbbreviation;
                if (string.IsNullOrWhiteSpace(tgt)) return null;

                return new[] { src, tgt };
            }
            catch (Exception e)
            {
                ToolkitLog.Error("读取当前项目语言对失败", e);

                return null;
            }
        }

        /// <summary>是否本插件挂载的术语库（按 Path 是否为 tradostoolkit:// URI 判断）。</summary>
        private static bool IsPluginTermbase(IProjectTermbase termbase)
        {
            var path = SettingsPath(termbase);
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith(NativeTerminologyProviderHelper.SchemeActivation, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>该插件术语库的 src/tgt 是否属于给定项目语言对集合。</summary>
        private static bool IsForProjectPairs(IProjectTermbase termbase, List<string[]> projectPairs)
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

        private static string SettingsPath(IProjectTermbase termbase)
        {
            if (termbase == null || string.IsNullOrEmpty(termbase.SettingsXml)) return null;
            try
            {
                return XDocument.Parse(termbase.SettingsXml).Root?.Element("Path")?.Value;
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

        /// <summary>
        /// 添加"项目语言 ↔ 术语库索引名"映射，返回是否发生了变更。
        /// 索引名用语言的**显示名**（English / German / French / Japanese），
        /// 与 Studio 项目设置里"所选术语库中的已映射语言"一栏完全一致；
        /// 若用 ISO 代码（de-DE），Studio 会认为该语言无映射，从而跳过其术语查找。
        /// </summary>
        private static bool TryAddLanguageIndex(IProjectTermbaseConfiguration config, string isoCode)
        {
            if (string.IsNullOrWhiteSpace(isoCode)) return false;
            try
            {
                var indexes = config.LanguageIndexes;
                if (indexes == null) return false;

                var lang = new Language(isoCode);
                var indexName = string.IsNullOrWhiteSpace(lang.DisplayName) ? isoCode : lang.DisplayName;

                var existing = indexes.FirstOrDefault(li =>
                    li.Language != null &&
                    string.Equals(li.Language.IsoAbbreviation, isoCode, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    // 修正历史遗留的 ISO 代码索引名（de-DE），Studio 不认这种映射。
                    if (existing.TermbaseIndex == null ||
                        !string.Equals(existing.TermbaseIndex.Name, indexName, StringComparison.OrdinalIgnoreCase))
                    {
                        ToolkitLog.Info("项目术语挂载：修正语言索引名 " + isoCode + " -> " + indexName);
                        existing.TermbaseIndex = config.Factory.CreateTermbaseIndex(indexName);
                        return true;
                    }
                    return false;
                }

                indexes.Add(config.Factory.CreateTermbaseLanguageIndex(lang, config.Factory.CreateTermbaseIndex(indexName)));
                return true;
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：添加语言索引失败 " + isoCode, e);
                return false;
            }
        }
    }
}
