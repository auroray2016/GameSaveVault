using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace GameSaveManager.Services;

/// <summary>
/// 全局热键注册（Win32 RegisterHotKey/UnregisterHotKey）。
/// 通过 HwndSource.AddHook 拦截 WM_HOTKEY 消息，触发回调。
/// </summary>
public static class GlobalHotKeyService
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x9000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static IntPtr _hwnd;
    private static HwndSource? _source;
    private static Action? _callback;
    private static bool _registered;

    /// <summary>解析 "Ctrl+Shift+B" 格式字符串为 (modifiers, vk)。</summary>
    public static (uint mods, uint vk, bool ok) Parse(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return (0, 0, false);
        uint mods = 0, vk = 0;
        bool hasKey = false;
        foreach (var part in combo.Split('+'))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0002;       // MOD_CONTROL
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase) || p.Equals("Menu", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0001;       // MOD_ALT
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0004;       // MOD_SHIFT
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) || p.Equals("Super", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0008;       // MOD_WIN
            else
            {
                // 单个字母/数字/F键
                vk = MapKey(p);
                if (vk == 0) return (0, 0, false);
                hasKey = true;
            }
        }
        return (mods, vk, hasKey);
    }

    private static uint MapKey(string s)
    {
        if (s.Length == 1 && char.IsLetterOrDigit(s[0]))
        {
            char c = char.ToUpper(s[0]);
            return c;  // A=0x41, B=0x42 ...
        }
        if (s.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[1..], out int fn) && fn is >= 1 and <= 12)
            return (uint)(0x6F + fn);  // F1=0x70 ... F12=0x7B
        return 0;
    }

    /// <summary>WPF 按键 + 修饰键 → 规范组合字符串（如 "Ctrl+Shift+B"）。
    /// 只接受字母/数字/F1–F12 且必须带至少一个修饰键；其余情况返回 false。</summary>
    public static bool TryFormat(Key key, ModifierKeys mods, out string combo)
    {
        combo = "";
        string n = key.ToString();
        string? keyPart =
            n.Length == 1 && char.IsLetter(n[0]) ? n :
            n.Length == 2 && n[0] == 'D' && char.IsDigit(n[1]) ? n[1..] :
            n.StartsWith("F", StringComparison.Ordinal) && int.TryParse(n[1..], out int fn) && fn is >= 1 and <= 12 ? n :
            null;
        if (keyPart == null || mods == ModifierKeys.None) return false;
        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(keyPart);
        combo = string.Join("+", parts);
        return true;
    }

    /// <summary>注册全局热键并挂载消息钩子。combo 为空=禁用（视为成功的空操作）。</summary>
    public static bool Register(IntPtr hwnd, string? combo, Action callback)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(combo)) return true;
        var (mods, vk, ok) = Parse(combo);
        // 必须带至少一个修饰键（Ctrl/Alt/Shift/Win），否则会全局劫持普通按键
        if (!ok || mods == 0) return false;
        _hwnd = hwnd;
        _callback = callback;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        _registered = RegisterHotKey(hwnd, HOTKEY_ID, mods, vk);
        return _registered;
    }

    /// <summary>注销热键并移除钩子。</summary>
    public static void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
        _callback = null;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            _callback?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }
}
