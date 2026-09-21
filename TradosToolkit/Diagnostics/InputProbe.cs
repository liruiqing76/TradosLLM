using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TradosToolkit.Diagnostics
{
    /// <summary>
    /// 插件独立窗口键盘链路探针（纯观测，诊断"Studio 里英文敲不进、中文 IME 正常"）。
    /// 2026-09-21 实测日志定案：留在 Studio UI 线程的 Show() 独立页（设不设 Owner 都一样），
    /// WM_KEYDOWN 能到，但宿主消息泵不给本窗口 TranslateMessage，钩子内补发的 WM_CHAR 同样被泵吞掉；
    /// 中文走 TSF 通道不受影响；模态 ShowDialog 的 WPF 嵌套泵自己转译，故配置窗口一直正常。
    /// 最终修复：窗口放专用 STA 线程跑 WPF 自己的 Dispatcher 泵（见 GlossaryManagerWindow.ShowOrActivate）。
    /// 本探针保留作验证手段：修复后日志里 KEYDOWN 与 CHAR 应成对出现（keydown≈char）。
    /// 曾试过"keydown 与 char 脱节时在钩子里 ToUnicode 直接往文本框落字"的补救，弃用：
    /// TSF 中文组字不产生 WM_CHAR/WM_IME_CHAR，计数脱节会把正常中文组字误判成丢字符而插英文。
    /// </summary>
    public static class InputProbe
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_CHAR = 0x0102;
        private const int WM_IME_CHAR = 0x0286;
        private const int GW_OWNER = 4;

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

        private class State
        {
            private readonly Window _w;
            private int _budget = 160;          // 总日志条数上限，防刷屏
            private int _keydown;               // 本窗口收到的裸键数
            private int _char;                  // 收到的字符数（WM_CHAR 或 WM_IME_CHAR）

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
                        if (_budget > 150) // 前几条带完整上下文
                            Log("KEYDOWN vk=0x" + ((int)wParam).ToString("X2") +
                                " active=" + _w.IsActive +
                                " fg=0x" + ((int)GetForegroundWindow()).ToString("X") +
                                " (本hwnd=0x" + ((int)hwnd).ToString("X") + ")");
                        else if (_keydown % 50 == 0) // 之后定期汇总一次，事后可核对 keydown≈char
                            Log("…keydown=" + _keydown + " char=" + _char + " focused=" + FocusedName());
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
                        _char++;
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
