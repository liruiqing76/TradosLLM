using System.Collections.Generic;
using TradosToolkit.Common.Catalog;

namespace TradosToolkit.Common
{
    /// <summary>
    /// 领域目录：领域树与其展开后的可选清单在进程内只构建一次并缓存。
    /// 工作台 / 术语管理 / 术语弹窗等共用，避免每次调用 DomainTree.Defaults()+Flatten 重复构建。
    /// </summary>
    public static class DomainCatalog
    {
        /// <summary>默认领域（未配置时使用）。</summary>
        public const string DefaultDomain = DomainTree.DefaultDomain;

        private static readonly LazyValue<List<DomainNode>> TreeValue =
            new LazyValue<List<DomainNode>>(DomainTree.Defaults);

        private static readonly LazyValue<List<string>> NamesValue =
            new LazyValue<List<string>>(() => DomainTree.Flatten(TreeValue.Value));

        /// <summary>领域树（缓存）。只读使用，勿修改。</summary>
        public static List<DomainNode> Tree()
        {
            return TreeValue.Value;
        }

        /// <summary>展开后的领域名清单（含主/子领域，缓存）；供下拉直接绑定。只读使用，勿修改。</summary>
        public static List<string> Names()
        {
            return NamesValue.Value;
        }

        /// <summary>丢弃缓存，下次访问重新构建。</summary>
        public static void Reset()
        {
            TreeValue.Reset();
            NamesValue.Reset();
        }
    }
}
