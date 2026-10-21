using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ForgePad.Core;

/// <summary>
/// Глобальный перехват клавиш через WH_KEYBOARD_LL: срабатывает, когда фокус в Discord,
/// игре или браузере, то есть там, где саундпад и нужен. Клавиша не «съедается» —
/// уходит дальше в приложение, которое в фокусе.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_QUIT = 0x0012;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly HookProc _proc;
    private readonly Action<int, uint> _onKeyDown;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private IntPtr _hook;
    private uint _threadId;
    private bool _disposed;

    public GlobalHotkeys(Action<int, uint> onKeyDown)
    {
        _onKeyDown = onKeyDown;
        _proc = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "hotkeys" };
        _thread.Start();

        // Ждём установки хука, иначе первые нажатия потеряются.
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>Диагностический вывод: см. --hotkey-debug.</summary>
    public static bool Verbose { get; set; }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        IntPtr module = GetModuleHandle(null);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module, 0);

        if (Verbose)
            Console.WriteLine($"[hook] поток {_threadId}, hmod={module}, hook={_hook}, ошибка={Marshal.GetLastWin32Error()}");

        _ready.Set();

        if (_hook == IntPtr.Zero)
            return;

        int result;
        while ((result = GetMessage(out MSG _, IntPtr.Zero, 0, 0)) > 0)
        {
        }

        if (Verbose)
            Console.WriteLine($"[hook] цикл сообщений завершён, код={result}, ошибка={Marshal.GetLastWin32Error()}");

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            int key = Marshal.ReadInt32(lParam);

            // Бит 30 в flags — автоповтор от зажатой клавиши. Повторы не нужны.
            bool repeated = (Marshal.ReadInt32(lParam, 8) & 0x40000000) != 0;

            if (Verbose)
                Console.WriteLine($"[hook] клавиша 0x{key:X2}, повтор={repeated}");

            if (!repeated)
            {
                uint modifiers = HotkeyBinding.CurrentModifiers();

                try { _onKeyDown(key, modifiers); }
                catch { /* обработчик не должен ломать хук */ }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
