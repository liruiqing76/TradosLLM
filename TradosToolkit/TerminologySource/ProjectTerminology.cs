using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml.Linq;
using Sdl.Core.Globalization;
using Sdl.Core.Settings;
using Sdl.Core.Settings.Implementation;
using Sdl.ProjectAutomation.FileBased;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 项目术语挂载：把插件的原生术语源（tradostoolkit://glossary）写进项目的术语库配置，
    /// 使项目"术语库"插件点直接生效（不装 MultiTerm 也可用）。
    ///
    /// 全程只用公开 API（Sdl.Core.Settings + Sdl.ProjectAutomation.FileBased），
    /// 不引用 Sdl.ProjectApi、不用反射——Studio 拒绝加载引用了未验证 SDL 程序集的插件。
    ///
    /// 原理：Studio 把"自定义 Terminology Provider 型术语库"持久化在项目 SettingsBundle
    /// 的 TerminologyProviderSettings 分组里（设置键 Termbases / TermbasesOrder），
    /// 条目只需一个 SettingsXml，其中 &lt;Path&gt; 即术语源 URI（tradostoolkit://…），
    /// 由已注册的 NativeTerminologyProviderFactory 实例化，不做文件校验。
    /// 该分组按"分组类型名"存取，因此用公开的 GetSettingsGroup(字符串 id) 即可读写，
    /// 值用本文件定义的 DataContract 兼容类型（序列化形状与 Studio 内部一致）承载。
    ///
    /// 为什么不走 FileBasedProject.UpdateTermbaseConfiguration：
    ///   它只认 LocalTermbase（强制 File.Exists）/ ServerTermbase（强制 TermbaseServerUri），
    ///   且会先 Termbases.Clear() 再重建——无法表达 tradostoolkit:// 条目，
    ///   还会把已有的自定义条目清光（内部 PrepareProjectTermbaseConfiguration 逐字实证）。
    ///
    /// 语言索引（.sdlproj 顶层 &lt;TermbaseConfiguration&gt;&lt;LanguageIndexMappings&gt;）：
    ///   不在 SettingsBundle 里，公开 API 也没有写入它的口子（UpdateTermbaseConfiguration
    ///   写它时会同时清掉自定义条目，不能用）。这里用文件级补丁维护该块：
    ///   挂载成功后按语言显示名 upsert 映射。注意 Studio 后续保存项目可能覆盖此块，
    ///   到时再次挂载会自动补回。
    ///
    /// 查询/新增术语与这里无关：那是插件自己的 TerminologyProvider / TermHttpClient 代码，
    /// Studio 只通过这个挂载登记知道"用哪个术语源"。
    /// </summary>
    public static class ProjectTerminology
    {
        /// <summary>自定义术语库在项目设置里的分组 Id（与 Studio 内部一致）。</summary>
        private const string TermbaseGroupId = "TerminologyProviderSettings";
        private const string TermbasesKey = "Termbases";
        private const string TermbasesOrderKey = "TermbasesOrder";

        /// <summary>Studio 内部契约命名空间（对应 Sdl.ProjectApi.Implementation.Xml）。</summary>
        private const string ContractNamespace =
            "http://schemas.datacontract.org/2004/07/Sdl.ProjectApi.Implementation.Xml";

        /// <summary>
        /// Studio 内部条目的 DataContract 形状：
        /// &lt;ArrayOfTermbase xmlns=…&gt;&lt;Termbase&gt;&lt;enabledField/&gt;&lt;filterField/&gt;
        /// &lt;nameField/&gt;&lt;settingsXmlField/&gt;&lt;/Termbase&gt;。
        /// 成员名保持一致（enabledField 等），保证读旧项目/写新项目都被 Studio 认可。
        /// </summary>
        [DataContract(Name = "Termbase", Namespace = ContractNamespace)]
        private sealed class TermbaseEntry
        {
            [DataMember]
            public bool enabledField = true;
            [DataMember(EmitDefaultValue = false)]
            public TermbaseFilterEntry filterField;
            [DataMember]
            public string nameField;
            [DataMember]
            public string settingsXmlField;
        }

        [DataContract(Name = "TermbaseFilter", Namespace = ContractNamespace)]
        private sealed class TermbaseFilterEntry
        {
            [DataMember]
            public int Id;
            [DataMember]
            public string Name;
        }

        /// <summary>挂载后的术语库显示名前缀。</summary>
        public static string DisplayName(string kind)
        {
            return "TradosToolkit · " + TermSourceKind.Label(kind);
        }

        /// <summary>
        /// 术语库显示名（带语向后缀）。
        /// Studio 的 ProjectTermbases 会拒绝同名条目（Name 或 SettingsXml 相同即拒绝），
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
        ///     不做文件校验。
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

        /// <summary>
        /// 把插件术语源挂到项目术语库配置上（幂等：同语言对+领域已存在则跳过）。
        /// 返回挂载的术语库个数；0 表示没有可挂的（如本地库为空且未配置线上服务）。
        ///
        /// 语言对优先级：
        ///   1) currentPair —— 编辑器当前打开文件的语向（ActiveDocument.ActiveFile.Language）；
        ///   2) 取不到时回退到项目语言对（源语言 × 各目标语言）。
        /// 之所以以"当前编辑语向"为先：Studio 的 LanguageIndexMappings 只映射到默认术语库
        /// （Termbases 列表首项），把项目多个目标语言一股脑挂上去会让首项与当前语向错位，
        /// 表现为"打开英德项目、术语插入点却显示另一个语言对"。
        /// </summary>
        /// <param name="currentPair">编辑器当前语向 [src,tgt]；为 null 时回退项目语言对。</param>
        public static int Mount(FileBasedProject project, string[] currentPair,
                                string onlineBaseUrl, string domain)
        {
            if (project == null) return 0;

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
            var wanted = new List<Tuple<string, string, string, string>>(); // kind, base, src, tgt

            foreach (var pair in allPairs)
            {
                wanted.Add(Tuple.Create(TermSourceKind.Local, (string)null, pair[0], pair[1]));

                // 线上源：配置了服务地址即按项目语言对挂载（服务端自行决定有无数据）。
                if (!string.IsNullOrWhiteSpace(onlineBaseUrl))
                    wanted.Add(Tuple.Create(TermSourceKind.Online, onlineBaseUrl, pair[0], pair[1]));
            }

            if (wanted.Count == 0)
                return 0;

            var bundle = TryReadSettings(project);
            if (bundle == null) return 0;

            var group = TryGetTermbaseGroup(bundle);
            if (group == null) return 0;

            var entries = TryReadEntries(group);
            if (entries == null) return 0;

            // 清掉本项目语言对之外的历史挂载（切换项目/改语向后遗留的旧语言对），
            // 否则 Studio 术语插入点仍会显示上一个项目的目标语言。
            var stale = 0;
            foreach (var t in entries.Where(t => IsPluginTermbase(t) && !IsForProjectPairs(t, allPairs)).ToList())
            {
                entries.Remove(t);
                stale++;
                ToolkitLog.Info("项目术语挂载：移除过期术语库 " + t.nameField + "（不属于当前项目语言对）");
            }

            // 同一语言对只保留一条插件术语库：历史上重复挂载会堆出多条同名记录
            // （Studio 术语库对话框里表现为灰色残留项），一并去重。
            var dedup = 0;
            foreach (var item in wanted)
            {
                var extras = entries
                    .Where(t => IsPluginTermbase(t) && !IsSameKind(t, item.Item1) && IsPairOf(t, new[] { item.Item3, item.Item4 }))
                    .ToList();
                foreach (var t in extras)
                {
                    entries.Remove(t);
                    dedup++;
                    ToolkitLog.Info("项目术语挂载：去重术语库 " + t.nameField + "（同语言对重复）");
                }
            }

            var added = 0;
            foreach (var item in wanted)
            {
                var uri = NativeTerminologyProviderHelper.BuildUri(item.Item1, item.Item2, item.Item3, item.Item4, domain);
                if (HasPairAndKind(entries, item.Item3, item.Item4, item.Item1, domain)) continue;

                entries.Add(new TermbaseEntry
                {
                    nameField = TermbaseName(item.Item1, item.Item3, item.Item4),
                    settingsXmlField = BuildSettingsXml(uri.ToString())
                });
                added++;
            }

            if (added == 0 && stale == 0 && dedup == 0) return 0;

            // 坑位：Terminology 引擎要求至少一个术语库；我们只在有变更时才写回，
            // 此时列表必非空。仍做保护，防止把空配置写回导致异常。
            if (entries.Count == 0) return 0;

            try
            {
                // 公开写入路径：GetSetting(id, 默认值) 返回 Setting<T>，赋 Value 即落盘，
                // 序列化形状与 Studio 内部落库完全一致（见 TermbaseEntry 契约定制）。
                group.GetSetting(TermbasesKey, entries).Value = entries;
                group.GetSetting(TermbasesOrderKey, entries.Select(t => t.nameField).ToList())
                    .Value = entries.Select(t => t.nameField).ToList();

                project.UpdateSettings(bundle);
                project.Save();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：写入项目设置失败", e);
                return 0;
            }

            // 顶层语言索引映射不在 SettingsBundle 里，用文件补丁保证目标语言有映射。
            UpsertLanguageIndexMappings(project, allPairs);

            ToolkitLog.Info($"项目术语挂载：语言对 {string.Join(",", allPairs.Select(p => p[0] + "-" + p[1]))}，"
                            + $"新增 {added} 个、清理 {stale + dedup} 个术语库（domain={domain}）");

            return added;
        }

        /// <summary>通过公开 API 读项目设置副本（改动后再 UpdateSettings 写回）。</summary>
        private static ISettingsBundle TryReadSettings(FileBasedProject project)
        {
            try
            {
                var bundle = project.GetSettings();
                if (bundle == null)
                    ToolkitLog.Error("项目术语挂载：读取项目设置返回空，跳过");
                return bundle;
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：读取项目设置失败", e);

                return null;
            }
        }

        /// <summary>
        /// 取/建 TerminologyProviderSettings 分组。
        /// 公开的 GetSettingsGroup(string id)：不存在时自动新建，
        /// 存在时按泛型分组读入 —— 分组内若已有其它来源类型（其他厂的 Provider），
        /// 公开的泛型分组仍能按原始 XxxML 读出值。
        /// </summary>
        private static SettingsGroup TryGetTermbaseGroup(ISettingsBundle bundle)
        {
            try
            {
                var group = bundle.GetSettingsGroup(TermbaseGroupId) as SettingsGroup;
                if (group == null)
                    ToolkitLog.Error("项目术语挂载：无法获取 " + TermbaseGroupId + " 分组，跳过");
                return group;
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：读取 " + TermbaseGroupId + " 分组失败", e);

                return null;
            }
        }

        private static List<TermbaseEntry> TryReadEntries(SettingsGroup group)
        {
            try
            {
                Setting<List<TermbaseEntry>> setting;
                if (group.GetSetting(TermbasesKey, out setting) && setting != null)
                {
                    var existing = setting.Value;
                    if (existing != null) return existing.ToList();
                }

                return new List<TermbaseEntry>();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("项目术语挂载：读取现有术语库条目失败", e);

                return null;
            }
        }

        private static bool HasPairAndKind(IEnumerable<TermbaseEntry> termbases, string srcLang, string tgtLang, string kind, string domain)
        {
            return termbases.Any(t => IsPairOf(t, new[] { srcLang, tgtLang }) && IsSameKind(t, kind) && HasDomain(t, domain));
        }

        private static bool HasDomain(TermbaseEntry termbase, string domain)
        {
            var path = SettingsPath(termbase);
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var uri = new Uri(path);
                return string.Equals(NativeTerminologyProviderHelper.GetQueryParam(uri, "domain") ?? "",
                                     domain ?? "", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsSameKind(TermbaseEntry termbase, string kind)
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

        private static bool IsPairOf(TermbaseEntry termbase, string[] pair)
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

        /// <summary>是否本插件挂载的术语库（按 Path 是否为 tradostoolkit:// URI 判断）。</summary>
        private static bool IsPluginTermbase(TermbaseEntry termbase)
        {
            var path = SettingsPath(termbase);
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith(NativeTerminologyProviderHelper.SchemeActivation, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>该插件术语库的 src/tgt 是否属于给定项目语言对集合。</summary>
        private static bool IsForProjectPairs(TermbaseEntry termbase, List<string[]> projectPairs)
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

        private static string SettingsPath(TermbaseEntry termbase)
        {
            if (termbase == null || string.IsNullOrEmpty(termbase.settingsXmlField)) return null;
            try
            {
                return XDocument.Parse(termbase.settingsXmlField).Root?.Element("Path")?.Value;
            }
            catch (Exception)
            {
                return null;
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
        /// 语言索引映射名必须与 Studio 期望的一致（用语言显示名 English/German 这类），
        /// 不能用 ISO 代码 de-DE —— 否则项目设置里该目标语言显示"无映射"，
        /// Studio 会直接跳过该目标语言的术语查找（表现为术语识别空白）。
        /// 顶层 &lt;TermbaseConfiguration&gt; 不属于 SettingsBundle，公开 API 无入口，
        /// 这里直接补丁 .sdlproj 文件（仅动 LanguageIndexMappings，保留 RecognitionOptions）。
        /// </summary>
        private static void UpsertLanguageIndexMappings(FileBasedProject project, List<string[]> pairs)
        {
            try
            {
                var path = project.FilePath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return;

                var cfg = root.Element("TermbaseConfiguration");
                if (cfg == null)
                {
                    cfg = new XElement("TermbaseConfiguration");
                    // 放在 LanguageDirections 之后、SettingsBundles 之前（与 Studio 落盘顺序一致）。
                    var anchor = root.Element("LanguageDirections");
                    if (anchor != null) anchor.AddAfterSelf(cfg);
                    else root.Element("SettingsBundles")?.AddBeforeSelf(cfg);
                }

                // 整表重建：项目语言对之外旧行清掉（与术语库条目清理同一逻辑）。
                cfg.Elements("LanguageIndexMappings").Remove();
                foreach (var iso in pairs.Select(p => p[0]).Concat(pairs.Select(p => p[1])).Distinct())
                {
                    var lang = new Language(iso);
                    var name = string.IsNullOrWhiteSpace(lang.DisplayName) ? iso : lang.DisplayName;
                    var item = new XElement("LanguageIndexMappings",
                        new XElement("Language", iso));
                    if (!string.IsNullOrWhiteSpace(name)) item.Add(new XElement("Index", name));
                    cfg.Add(item);
                }

                doc.Save(path);
                ToolkitLog.Info("项目术语挂载：语言索引已写入 " + System.IO.Path.GetFileName(path));
            }
            catch (Exception e)
            {
                // 语言索引补丁失败不阻断挂载：分组条目已在设置里，
                // 用户在 Studio"项目设置→语言对→术语库"里手动确认一次即可生成映射。
                ToolkitLog.Error("项目术语挂载：写语言索引映射失败（可在项目设置里手动补一次）", e);
            }
        }
    }
}
