using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Reproduces the user's supplied EA App AutoHotkey flow using the
    /// built-in NativeKeyboardInput service. No external AutoHotkey is required.
    /// </summary>
    internal sealed class EAAppUiAutomation
    {
        private const string MainProcessName = "EADesktop";
        private readonly Action<string> log;

        public EAAppUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool Login(string username, string password, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanych dla konta EA App.";
                return false;
            }

            try
            {
                // Match the supplied AHK: wait 15 seconds after launching EA App,
                // then operate on the launcher window.
                log("EA INPUT: odpowiednik AHK Sleep 15000 — czekam 15 sekund na EA App.");
                Thread.Sleep(15000);

                IntPtr hwnd = GetEAForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    error = "Nie udało się znaleźć aktywnego okna EA App po 15 sekundach.";
                    return false;
                }

                log("EA INPUT: aktywne okno należy do EA App.");
                if (!EnsureWindowForeground(hwnd))
                {
                    error = "Nie udało się aktywować okna EA App. Przerywam automatyczne wpisywanie danych.";
                    return false;
                }

                // Exact sequence from the user's AHK script:
                // username, TAB x5, ENTER, wait 1s, password, TAB x2, ENTER.
                log("EA INPUT: AHK krok 1 — wpisuję login.");
                if (!NativeKeyboardInput.TypeText(username, hwnd, log))
                {
                    error = "Nie udało się wpisać loginu EA App.";
                    return false;
                }

                Thread.Sleep(300);

                log("EA INPUT: AHK krok 2 — TAB x5.");
                if (!SendTabs(5, "po loginie EA App", out error))
                    return false;

                log("EA INPUT: AHK krok 3 — ENTER.");
                if (!NativeKeyboardInput.SendEnter(log))
                {
                    error = "Nie udało się wysłać ENTER w EA App po loginie.";
                    return false;
                }

                log("EA INPUT: AHK krok 4 — czekam 1 sekundę.");
                Thread.Sleep(1000);

                log("EA INPUT: AHK krok 5 — wpisuję hasło.");
                if (!NativeKeyboardInput.TypeText(password, hwnd, log))
                {
                    error = "Nie udało się wpisać hasła EA App.";
                    return false;
                }

                Thread.Sleep(300);

                log("EA INPUT: AHK krok 6 — TAB, TAB.");
                if (!SendTabs(2, "po haśle EA App", out error))
                    return false;

                log("EA INPUT: AHK krok 7 — ENTER.");
                if (!NativeKeyboardInput.SendEnter(log))
                {
                    error = "Nie udało się wysłać końcowego ENTER w EA App.";
                    return false;
                }

                Thread.Sleep(2000);
                log("EA INPUT: automatyczna sekwencja logowania została wykonana.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Błąd automatycznego logowania EA App: " + ex.Message;
                return false;
            }
        }

        private bool SendTabs(int count, string context, out string error)
        {
            error = null;

            for (int i = 0; i < count; i++)
            {
                if (!NativeKeyboardInput.SendTab(log))
                {
                    error = "Nie udało się wykonać TAB #" + (i + 1) + " " + context + ".";
                    return false;
                }

                if (i < count - 1)
                    Thread.Sleep(300);
            }

            return true;
        }

        private IntPtr GetEAForegroundWindow()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && IsWindowOwnedByEA(foreground))
                return foreground;

            IntPtr main = FindMainWindowHandle();
            if (main != IntPtr.Zero)
            {
                EnsureWindowForeground(main);
                if (IsWindowOwnedByEA(main))
                    return main;
            }

            log("EA INPUT: aktywne okno nie należy do EA App.");
            return IntPtr.Zero;
        }

        private static bool IsWindowOwnedByEA(IntPtr hwnd)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0)
                    return false;

                using (Process process = Process.GetProcessById((int)pid))
                {
                    return string.Equals(
                               process.ProcessName,
                               MainProcessName,
                               StringComparison.OrdinalIgnoreCase)
                           || string.Equals(
                               process.ProcessName,
                               "EALauncher",
                               StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr FindMainWindowHandle()
        {
            foreach (string processName in new[] { "EADesktop", "EALauncher" })
            foreach (Process process in SafeGetProcesses(processName))
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    IntPtr hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                        return hwnd;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            return IntPtr.Zero;
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { yield break; }

            foreach (Process process in processes)
                yield return process;
        }

        private static bool EnsureWindowForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                ShowWindow(hwnd, 9);

                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (GetForegroundWindow() == hwnd)
                        return true;

                    IntPtr foreground = GetForegroundWindow();
                    uint currentThread = GetCurrentThreadId();
                    uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                    uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
                    bool attachedForeground = false;
                    bool attachedTarget = false;

                    try
                    {
                        if (foregroundThread != 0 && foregroundThread != currentThread)
                            attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

                        if (targetThread != 0 && targetThread != currentThread)
                            attachedTarget = AttachThreadInput(currentThread, targetThread, true);

                        BringWindowToTop(hwnd);
                        SetForegroundWindow(hwnd);
                        SetActiveWindow(hwnd);
                    }
                    finally
                    {
                        if (targetThread != 0 && targetThread != currentThread && attachedTarget)
                            AttachThreadInput(currentThread, targetThread, false);

                        if (foregroundThread != 0 && foregroundThread != currentThread && attachedForeground)
                            AttachThreadInput(currentThread, foregroundThread, false);
                    }

                    Thread.Sleep(150);
                }

                return GetForegroundWindow() == hwnd;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
