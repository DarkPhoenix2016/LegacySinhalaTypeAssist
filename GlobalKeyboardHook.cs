using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Application = System.Windows.Application;

namespace SinhalaTypeBypasser
{
    public class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint LLKHF_INJECTED = 0x00000010;

        private const ushort VK_BACK = 0x08;
        private const ushort VK_F12 = 0x7B;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;

        private readonly TranslationEngine _engine;

        public event Action<string>? OnTelemetryUpdated;
        public event Action? OnToggleRequested;

        public GlobalKeyboardHook(TranslationEngine engine)
        {
            _engine = engine;
            _proc = HookCallback;
            _hookId = SetHook(_proc);
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // 1. Ignore synthetic keystrokes injected by our own SendInput calls
                if ((hookStruct.flags & LLKHF_INJECTED) != 0)
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                int vkCode = (int)hookStruct.vkCode;

                // Ignore standalone modifier key presses (Shift, Ctrl, Alt, Win)
                if (vkCode is 0x10 or 0xA0 or 0xA1 or 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C)
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                // 2. F12 Quick-Toggle Hotkey
                if (vkCode == VK_F12)
                {
                    OnToggleRequested?.Invoke();
                    return (IntPtr)1;
                }

                // 3. Pass through system shortcuts if Ctrl, Alt, or Win is held
                if (IsModifierHeld())
                {
                    _engine.ResetBuffer();
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                // 4. Navigation / editing keys reset the internal cluster buffer
                if (vkCode is 0x08 or 0x0D or 0x1B or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2E)
                {
                    _engine.ResetBuffer();
                    OnTelemetryUpdated?.Invoke("Ready");
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                // 5. Normal, Shifted, and '\' Escaped Sinhala typing
                char c = TranslateVkToChar(vkCode);
                if (c != '\0')
                {
                    var action = _engine.ProcessKeyPress(c);
                    if (action.Suppress)
                    {
                        int backspaces = action.BackspaceCount;
                        string text = action.TextToInject;
                        string preview = action.ActiveBufferPreview;

                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            if (backspaces > 0)
                                SendBackspaces(backspaces);

                            if (!string.IsNullOrEmpty(text))
                                SendUnicodeString(text);

                            OnTelemetryUpdated?.Invoke(preview);
                        });

                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private bool IsModifierHeld()
        {
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool lwin = (GetAsyncKeyState(0x5B) & 0x8000) != 0;
            bool rwin = (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            return ctrl || alt || lwin || rwin;
        }

        private char TranslateVkToChar(int vkCode)
        {
            byte[] keyState = new byte[256];
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0) // Shift
                keyState[0x10] = 0x80;
            if ((GetKeyState(0x14) & 0x0001) != 0) // Caps Lock
                keyState[0x14] = 0x01;

            uint scanCode = MapVirtualKey((uint)vkCode, 0);
            IntPtr hkl = GetKeyboardLayout(0);

            var sb = new StringBuilder(4);
            int res = ToUnicodeEx((uint)vkCode, scanCode, keyState, sb, sb.Capacity, 0, hkl);
            return res == 1 ? sb[0] : '\0';
        }

        private void SendBackspaces(int count)
        {
            INPUT[] inputs = new INPUT[count * 2];
            for (int i = 0; i < count; i++)
            {
                inputs[i * 2].type = INPUT_KEYBOARD;
                inputs[i * 2].u.ki.wVk = VK_BACK;

                inputs[i * 2 + 1].type = INPUT_KEYBOARD;
                inputs[i * 2 + 1].u.ki.wVk = VK_BACK;
                inputs[i * 2 + 1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private void SendUnicodeString(string text)
        {
            INPUT[] inputs = new INPUT[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                inputs[i * 2].type = INPUT_KEYBOARD;
                inputs[i * 2].u.ki.wVk = 0;
                inputs[i * 2].u.ki.wScan = c;
                inputs[i * 2].u.ki.dwFlags = KEYEVENTF_UNICODE;

                inputs[i * 2 + 1].type = INPUT_KEYBOARD;
                inputs[i * 2 + 1].u.ki.wVk = 0;
                inputs[i * 2 + 1].u.ki.wScan = c;
                inputs[i * 2 + 1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        #region Win32 Interop (x64 & x86 Compatible)
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }
        #endregion
    }
}