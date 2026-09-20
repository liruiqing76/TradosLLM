using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TradosToolkit.Server
{
    /// <summary>一行双语段（来自 .sdlxliff 的 trans-unit）。</summary>
    public class BilingualSegment
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Origin { get; set; }
        public string Percent { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
    }

    /// <summary>
    /// 双语 .sdlxliff（XLIFF 1.2 + SDL 扩展）只读解析：按 local name 遍历 trans-unit（不依赖前缀声明），
    /// 提取纯文本（标签占位符忽略、空白折叠）与 sdl:seg-defs&gt;seg 的 conf/origin/percent。
    /// translate="no" 的结构单元跳过；一个 trans-unit 多 seg 时出一行、取首个 seg 属性。
    /// </summary>
    public static class BilingualParser
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        public static List<BilingualSegment> Parse(string sdlxliffPath)
        {
            var doc = XDocument.Load(sdlxliffPath);
            var result = new List<BilingualSegment>();
            foreach (var tu in doc.Descendants().Where(e => e.Name.LocalName == "trans-unit"))
            {
                if ((string)tu.Attribute("translate") == "no") continue;

                var seg = tu.Descendants().FirstOrDefault(e => e.Name.LocalName == "seg");
                result.Add(new BilingualSegment
                {
                    Id = (string)tu.Attribute("id"),
                    Source = TextOf(Child(tu, "source")),
                    Target = TextOf(Child(tu, "target")),
                    Status = Attr(seg, "conf") ?? Attr(seg, "status") ?? string.Empty,
                    Origin = Attr(seg, "origin") ?? string.Empty,
                    Percent = Attr(seg, "percent") ?? string.Empty,
                });
            }
            return result;
        }

        /// <summary>CSV 导出：UTF-8（BOM 由调用方加），带引号转义，Excel 双击可开。</summary>
        public static string ToCsv(IEnumerable<BilingualSegment> rows)
        {
            var sb = new StringBuilder();
            sb.Append("id,status,origin,percent,source,target\r\n");
            foreach (var r in rows)
                sb.Append(Csv(r.Id)).Append(',').Append(Csv(r.Status)).Append(',').Append(Csv(r.Origin))
                  .Append(',').Append(Csv(r.Percent)).Append(',').Append(Csv(r.Source)).Append(',').Append(Csv(r.Target))
                  .Append("\r\n");
            return sb.ToString();
        }

        public static string Csv(string value)
        {
            value = value ?? string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static XElement Child(XElement parent, string localName)
            => parent == null ? null : parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

        private static string Attr(XElement el, string name)
            => el == null ? null : (string)el.Attribute(name);

        private static string TextOf(XElement el)
        {
            if (el == null) return string.Empty;
            return Whitespace.Replace(string.Concat(el.DescendantNodes().OfType<XText>().Select(t => t.Value)), " ").Trim();
        }
    }
}
