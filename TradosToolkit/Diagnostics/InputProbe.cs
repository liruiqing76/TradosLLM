using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TradosToolkit.Diagnostics
{
    /// <summary>
    /// 插件独立窗口键盘链路探针（诊断"Studio 里英文敲不进、中文 IME 正常"）：
    /// 在窗口 HWND 过程里记录 WM_KEYDOWN/WM_CHAR/WM_IME_CHAR 是否到达、前台/焦点 HWND 归属，
    /// 并可试验性摘除 Win32 属主窗口（GWLP_HWNDPARENT=0）。
    /// 若观察到"有 WM_KEYDOWN 无 WM_CHAR"（宿主消息泵没对本窗口 TranslateMessage），
    /// 自动进入补救模式：在钩子里自己调 TranslateMessage，把裸键补成 WM_CHAR。
    /// </summary>
    public static class InputProbe
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_CHAR = 0x0102;
        private const int WM_IME_CHAR = 0x0286;
        private const int GW_OWNER = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        /// <summary>给窗口挂键盘消息探针（构造期调用，SourceInitialized 后生效）。</summary>
        public static void Attach(Window w)
        {
            if (w == null) return;
            w.SourceInitialized += (s, e) =>
            {
                try
                {
                    var src = PresentationSource.FromVisual(w) as HwndSource;
                    if (src == null) return;
                    var st = new State(w);
                    src.AddHook(st.WndProc);
                    ToolkitLog.Info("输入探针已挂载 hwnd=0x" + ((int)src.Handle).ToString("X") +
                                    " owner=0x" + ((int)GetWindow(src.Handle, GW_OWNER)).ToString("X"));
                }
                catch (Exception ex) { ToolkitLog.Error("输入探针挂载失败", ex); }
            };
        }

        /// <summary>
        /// 把窗口挂到 Studio 主窗口下（Show 之前调用）。
        /// 依据：配置窗口（Owner=Studio主窗）英文输入正常；无 Owner 的 Show() 独立页
        /// 全窗口英文进不去、中文 IME 可——Studio 消息环对无属主窗口不 TranslateMessage。
        /// 探针的 RESCUE 分支（自行补 TranslateMessage）作为兜底，日志可分辨谁起了作用。
        /// </summary>
        public static void SetStudioOwner(Window w)
        {
            try
            {
                var main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (main == IntPtr.Zero)
                {
                    ToolkitLog.Info("输入探针：取不到 Studio 主窗句柄，不设 owner");
                    return;
                }
                new WindowInteropHelper(w).Owner = main;
                ToolkitLog.Info("输入探针：owner 已设为 Studio 主窗 0x" + ((int)main).ToString("X"));
            }
            catch (Exception ex) { ToolkitLog.Error("输入探针：设 owner 失败", ex); }
        }

        private class State
        {
            private readonly Window _w;
            private int _budget = 160;          // 总日志条数上限，防刷屏
            private int _keydown;               // 本窗口收到的裸键数
            private int _char;                  // 收到的 WM_CHAR 数（含补救产生的）
            private bool _rescue;               // true=确认宿主不 TranslateMessage，逐条补
            private MSG _last;                  // 上一条 WM_KEYDOWN，供补救翻译

            public State(Window w) { _w = w; }

            private void Log(string msg)
            {
                if (_budget-- <= 0) return;
                ToolkitLog.Info("输入探针: " + msg);
            }

            public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                try
                {
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        _keydown++;
                        _last.hwnd = hwnd;
                        _last.message = (uint)msg;
                        _last.wParam = wParam;
                        _last.lParam = lParam;
                        if (_keydown - _char > 6 && !_rescue)
                        {
                            _rescue = true;
                            Log("RESCUE ON: keydown=" + _keydown + " char=" + _char +
                                " → 宿主未对本窗口 TranslateMessage，开始自行补字符");
                        }
                        if (_budget > 150) // 前几条带完整上下文
                            Log("KEYDOWN vk=0x" + ((int)wParam).ToString("X2") +
                                " active=" + _w.IsActive +
                                " fg=0x" + ((int)GetForegroundWindow()).ToString("X") +
                                " (本hwnd=0x" + ((int)hwnd).ToString("X") + ")");
                        if (_rescue)
                        {
                            var m = _last;
                            TranslateMessage(ref m);   // 把这条按键补成 WM_CHAR 投进本线程队列
                        }
                    }
                    else if (msg == WM_CHAR)
                    {
                        _char++;
                        if (_budget > 140)
                            Log("CHAR ch='" + (char)((int)wParam & 0xFFFF) +
                                "' code=0x" + ((int)wParam).ToString("X") +
                                " focused=" + FocusedName());
                    }
                    else if (msg == WM_IME_CHAR)
                    {
                        if (_budget > 140)
                            Log("IME_CHAR code=0x" + ((int)wParam).ToString("X") +
                                " focused=" + FocusedName());
                    }
                }
                catch (Exception ex) { ToolkitLog.Error("输入探针异常", ex); }
                return IntPtr.Zero;
            }

            private string FocusedName()
            {
                var f = System.Windows.Input.Keyboard.FocusedElement;
                return f == null ? "null" : f.GetType().Name;
            }
        }
    }
}
