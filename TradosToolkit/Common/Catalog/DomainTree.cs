using System;
using System.Collections.Generic;

namespace TradosToolkit.Common.Catalog
{
    /// <summary>
    /// 领域树的单个节点。Name 为领域名，Children 为子领域（当前占位实现仅两级：主领域/子领域）。
    /// 定义为可 JSON 序列化的普通类，供本地下拉与 GET /api/glossary/domains 同一套数据。
    /// </summary>
    public class DomainNode
    {
        public string Name { get; set; }
        public List<DomainNode> Children { get; set; } = new List<DomainNode>();
    }

    /// <summary>
    /// 全局领域树。领域是树结构（主领域/子领域两级即可），由 /api/glossary/domains 提供，
    /// 服务端未来可用 API 返回真正的词云/行业细分；插件当前先以占位实现（Defaults）跑通接口。
    /// 术语库与翻译/替换严格按"当前领域"过滤：只命中和当前领域一致的术语。
    /// </summary>
    public static class DomainTree
    {
        /// <summary>全局默认领域；未配置领域时术语归入此项。</summary>
        public const string DefaultDomain = "通用";

        /// <summary>占位领域树。后续由服务端经 /api/glossary/domains 返回真实数据。</summary>
        public static List<DomainNode> Defaults()
        {
            return new List<DomainNode>
            {
                new DomainNode { Name = DefaultDomain },
                new DomainNode
                {
                    Name = "法律",
                    Children = new List<DomainNode>
                    {
                        new DomainNode { Name = "合同" },
                        new DomainNode { Name = "诉讼" },
                        new DomainNode { Name = "合规" },
                    },
                },
                new DomainNode
                {
                    Name = "医疗",
                    Children = new List<DomainNode>
                    {
                        new DomainNode { Name = "药品" },
                        new DomainNode { Name = "器械" },
                        new DomainNode { Name = "临床" },
                    },
                },
                new DomainNode
                {
                    Name = "技术",
                    Children = new List<DomainNode>
                    {
                        new DomainNode { Name = "软件" },
                        new DomainNode { Name = "硬件" },
                        new DomainNode { Name = "网络" },
                    },
                },
                new DomainNode
                {
                    Name = "财经",
                    Children = new List<DomainNode>
                    {
                        new DomainNode { Name = "报告" },
                        new DomainNode { Name = "审计" },
                        new DomainNode { Name = "证券" },
                    },
                },
            };
        }

        /// <summary>把树展开为可选领域名列表（含主领域与各子领域），供下拉选择。</summary>
        public static List<string> Flatten(IList<DomainNode> tree)
        {
            var list = new List<string>();
            if (tree == null) return list;
            foreach (var node in tree)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.Name)) continue;
                list.Add(node.Name);
                if (node.Children != null)
                    foreach (var child in node.Children)
                        if (child != null && !string.IsNullOrWhiteSpace(child.Name))
                            list.Add(child.Name);
            }
            if (list.Count == 0) list.Add(DefaultDomain);
            return list;
        }
    }
}