using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Keyboard input that follows the same basic model as AutoHotkey SendEvent:
    /// characters are translated to virtual-key + modifier combinations using
    /// the active keyboard layout of the target window, then sent as ordinary
    /// key events. Unicode is used only as a fallback for characters that the
    /// active layout cannot produce.
    /// </summary>
    internal static class NativeKeyboardInput
    {
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        private const ushort VK_RETURN = 0x000D;
        private const ushort VK_TAB = 0x0009;
        private const ushort VK_SHIFT = 0x0010;
        private const ushort VK_CONTROL = 0x0011;
        private const ushort VK_MENU = 0x0012;

        private const byte SHIFT_STATE_SHIFT = 0x01;
        private const byte SHIFT_STATE_CTRL = 0x02;
        private const byte SHIFT_STATE_ALT = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        public static bool TypeText(string text, IntPtr targetWindowHandle, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            IntPtr hkl = GetTargetKeyboardLayout(targetWindowHandle);
            int sentCharacters = 0;

            foreach (char ch in text)
            {
                if (!SendMappedCharacter(ch, hkl, log))
                    return false;
                sentCharacters++;
                Thread.Sleep(15);
            }

            log?.Invoke("INPUT: wysłano " + sentCharacters + " znaków przez mapowanie klawiatury Windows.");
            return true;
        }

        private static IntPtr GetTargetKeyboardLayout(IntPtr hwnd)
        {
            try
            {
                uint threadId = hwnd == IntPtr.Zero ? 0 : GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                return GetKeyboardLayout(threadId);
            }
            catch
            {
                return GetKeyboardLayout(0);
            }
        }

        private static bool SendMappedCharacter(char ch, IntPtr hkl, Action<string> log)
        {
            short packed = VkKeyScanEx(ch, hkl);
            if (packed == -1)
                return SendUnicodeCharacter(ch, log);

            ushort vk = (ushort)(packed & 0x00FF);
            byte shiftState = (byte)((packed >> 8) & 0x00FF);

            var inputs = new System.Collections.Generic.List<INPUT>(6);
            if ((shiftState & SHIFT_STATE_CTRL) != 0)
                inputs.Add(CreateVirtualKeyInput(VK_CONTROL, false));
            if ((shiftState & SHIFT_STATE_ALT) != 0)
                inputs.Add(CreateVirtualKeyInput(VK_MENU, false));
            if ((shiftState & SHIFT_STATE_SHIFT) != 0)
                inputs.Add(CreateVirtualKeyInput(VK_SHIFT, false));

            inputs.Add(CreateVirtualKeyInput(vk, false));
            inputs.Add(CreateVirtualKeyInput(vk, true));

            if ((shiftState & SHIFT_STATE_SHIFT) != 0)
                inputs.Add(CreateVirtualKeyInput(VK_SHIFT, true));
            if ((shiftState & SHIFT_STATE_ALT) != 0)
                inputs.Add(CreateVirtualKeyInput(VK_MENU, true));
            if ((shiftState & SHIFT_STATE_CTRL) != 0)
                inputs.Add(CreateVirtualKeyInput(VK_CONTROL, true));

            if (!SendInputs(inputs.ToArray(), log))
            {
                log?.Invoke("INPUT: nie udało się wysłać znaku mapowanego przez VkKeyScanEx (VK=0x" + vk.ToString("X2") + ").");
                return false;
            }
            return true;
        }

        private static bool SendUnicodeCharacter(char ch, Action<string> log)
        {
            INPUT[] pair =
            {
                CreateUnicodeInput(ch, false),
                CreateUnicodeInput(ch, true)
            };
            if (!SendInputs(pair, log))
            {
                log?.Invoke("INPUT: fallback Unicode nie powiódł się dla znaku o kodzie U+" + ((int)ch).ToString("X4") + ".");
                return false;
            }
            return true;
        }

        public static bool Key(ushort virtualKey, Action<string> log = null)
        {
            INPUT[] inputs =
            {
                CreateVirtualKeyInput(virtualKey, false),
                CreateVirtualKeyInput(virtualKey, true)
            };
            return SendInputs(inputs, log);
        }

        private static bool SendInputs(INPUT[] inputs, Action<string> log)
        {
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Length)
            {
                log?.Invoke("INPUT: SendInput zwrócił " + sent + "/" + inputs.Length + ", Win32=" + Marshal.GetLastWin32Error() + ".");
                return false;
            }
            return true;
        }

        private static INPUT CreateVirtualKeyInput(ushort virtualKey, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKey,
                        wScan = 0,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        private static INPUT CreateUnicodeInput(char ch, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        public static bool SendEnter(Action<string> log = null) => Key(VK_RETURN, log);
        public static bool SendTab(Action<string> log = null) => Key(VK_TAB, log);

        public static bool SendShiftTab(Action<string> log = null)
        {
            INPUT[] inputs =
            {
                CreateVirtualKeyInput(VK_SHIFT, false),
                CreateVirtualKeyInput(VK_TAB, false),
                CreateVirtualKeyInput(VK_TAB, true),
                CreateVirtualKeyInput(VK_SHIFT, true)
            };

            return SendInputs(inputs, log);
        }
    }
}
