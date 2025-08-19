using System;
using System.Runtime.InteropServices;

namespace leituraWPF.Utils
{
    internal static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
