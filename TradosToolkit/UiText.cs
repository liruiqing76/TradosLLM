using System;
using System.Globalization;
using System.IO;
using System.Resources;
using TradosToolkit.Diagnostics;

namespace TradosToolkit
{
    /// <summary>
    /// 窗口运行时文案：读随包分发的 TradosToolkit.plugin[.culture].resources
    /// （SDL 打包任务把 PluginResources.*.resx 编译成文件型资源，主程序集内没有嵌入资源，
    /// 所以不能用 PluginResources.Designer 的强类型属性）。
    /// 当前 UI 文化缺项自动回退中性（英文）；管理器初始化失败回退键名。
    /// </summary>
    public static class UiText
    {
        private static readonly ResourceManager Rm = TryCreate();

        private static ResourceManager TryCreate()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(UiText).Assembly.Location);
                return ResourceManager.CreateFileBasedResourceManager("TradosToolkit.plugin", dir, null);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("UiText: 文件型资源管理器初始化失败", e);
                return null;
            }
        }

        public static string T(string key)
        {
            if (Rm == null) return key;
            try
            {
                return Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
            }
            catch (Exception e)
            {
                ToolkitLog.Error("UiText: 读取资源键失败 " + key, e);
                return key;
            }
        }

        public static string Tf(string key, params object[] args)
        {
            var fmt = T(key);
            try
            {
                return string.Format(fmt, args);
            }
            catch (FormatException)
            {
                return fmt;
            }
        }
    }
}
