using System;
using System.Collections.Generic;
using System.Linq;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Common
{
    /// <summary>
    /// 语言下拉项：显示名 + 代码。无 DisplayMemberPath 时输入框回显走 <see cref="ToString"/>。
    /// 全插件共用同一类型，避免各窗口各写一份私有 LangItem。
    /// </summary>
    public class LangItem
    {
        public string Code { get; set; }
        public string Label { get; set; }

        public override string ToString()
        {
            return Label;
        }
    }

    /// <summary>
    /// 语种目录：枚举 Studio 支持的全部语言，进程级只枚举一次并缓存。
    /// 术语管理 / 收件箱等所有语种下拉共用这一份清单，二次开窗不再重复枚举（枚举在宿主里很贵）。
    /// </summary>
    public static class LanguageCatalog
    {
        /// <summary>默认源语言。</summary>
        public const string DefaultSource = "zh-CN";
        /// <summary>默认目标语言。</summary>
        public const string DefaultTarget = "en-US";

        private static readonly LazyValue<List<LangItem>> AllValue =
            new LazyValue<List<LangItem>>(Build);

        /// <summary>全部语言（按显示名排序）；首次访问枚举，之后返回同一份缓存。</summary>
        public static List<LangItem> All()
        {
            return AllValue.Value;
        }

        /// <summary>丢弃缓存，下次访问重新枚举（一般无需调用）。</summary>
        public static void Reset()
        {
            AllValue.Reset();
        }

        /// <summary>按代码取语言项（忽略大小写）；找不到返回 null。</summary>
        public static LangItem FindByCode(string code)
        {
            if (StringKit.IsBlank(code)) return null;
            var all = All();
            for (var i = 0; i < all.Count; i++)
                if (StringKit.EqualsIgnoreCase(all[i].Code, code)) return all[i];
            return null;
        }

        /// <summary>取某代码的显示名；找不到返回原代码。</summary>
        public static string DisplayName(string code)
        {
            var item = FindByCode(code);
            return item == null ? code : item.Label;
        }

        private static List<LangItem> Build()
        {
            var list = new List<LangItem>();
            try
            {
                IEnumerable<Sdl.Core.Globalization.Language> all =
                    Sdl.Core.Globalization.Language.GetAllLanguages();
                foreach (var l in all)
                {
                    var code = l.IsoAbbreviation;
                    var name = StringKit.EnglishName(code);
                    list.Add(new LangItem { Code = code, Label = name + "  ·  " + code });
                }
                if (list.Count == 0) throw new Exception("Studio 语言清单为空");
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("语言目录：枚举 Studio 语言失败", ex);
                // 回退：至少给出常用中英两种，保证界面可用
                list.Clear();
                list.Add(new LangItem { Code = DefaultSource, Label = "Chinese simplified  ·  " + DefaultSource });
                list.Add(new LangItem { Code = DefaultTarget, Label = "English (US)  ·  " + DefaultTarget });
            }
            return list.OrderBy(l => l.Label).ToList();
        }
    }
}
