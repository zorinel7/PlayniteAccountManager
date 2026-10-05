using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Reproduces the user's supplied Epic Games AutoHotkey flow using the
    /// built-in NativeKeyboardInput service. No external AutoHotkey process
    /// is required.
    /// </summary>
    internal sealed class EpicGamesUiAutomation
    {
        private const string MainProcessName = "EpicGamesLauncher";
        private readonly Action<string> log;

        public EpicGamesUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool Login(string username, string password, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanych dla konta Epic Games.";
                return false;
            }

            try
            {
                // Match the supplied AHK closely: wait 15 seconds after launching
                // Epic, then operate on the foreground window.
                log("Epic INPUT: odpowiednik AHK Sleep 15000 — czekam 15 sekund na launcher.");
                Thread.Sleep(15000);

                IntPtr hwnd = GetEpicForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    error = "Nie udało się znaleźć aktywnego okna Epic Games Launcher po 15 sekundach.";
                    return false;
                }

                log("Epic INPUT: aktywne okno należy do Epic Games Launcher.");
                if (!EnsureWindowForeground(hwnd))
                {
                    error = "Nie udało się aktywować okna Epic Games Launcher. Przerywam automatyczne wpisywanie danych.";
                    return false;
                }

                // Exact sequence from the user's AHK script.
                log("Epic INPUT: AHK krok 1 — TAB, TAB.");
                if (!SendTab() || !SendTab())
                {
                    error = "Nie udało się wykonać początkowej sekwencji TAB w Epic Games Launcher.";
                    return false;
                }

                Thread.Sleep(2000);

                log("Epic INPUT: AHK krok 2 — wpisuję login.");
                if (!NativeKeyboardInput.TypeText(username, hwnd, log))
                {
                    error = "Nie udało się wpisać loginu Epic Games.";
                    return false;
                }

                Thread.Sleep(300);
                if (!SendTab()) return Fail("Nie udało się przejść do kolejnego pola Epic Games.", out error);
                Thread.Sleep(300);
                if (!SendTab()) return Fail("Nie udało się przejść do kolejnego pola Epic Games.", out error);
                Thread.Sleep(300);

                log("Epic INPUT: AHK krok 3 — ENTER.");
                if (!NativeKeyboardInput.SendEnter(log))
                    return Fail("Nie udało się wysłać ENTER w Epic Games Launcher.", out error);

                log("Epic INPUT: AHK krok 4 — czekam 10 sekund.");
                Thread.Sleep(10000);

                log("Epic INPUT: AHK krok 5 — TAB, TAB, TAB.");
                if (!SendTab()) return Fail("Nie udało się wykonać TAB #1 po loginie Epic Games.", out error);
                Thread.Sleep(300);
                if (!SendTab()) return Fail("Nie udało się wykonać TAB #2 po loginie Epic Games.", out error);
                Thread.Sleep(300);
                if (!SendTab()) return Fail("Nie udało się wykonać TAB #3 po loginie Epic Games.", out error);
                Thread.Sleep(300);

                log("Epic INPUT: AHK krok 6 — wpisuję hasło.");
                if (!NativeKeyboardInput.TypeText(password, hwnd, log))
                {
                    error = "Nie udało się wpisać hasła Epic Games.";
                    return false;
                }

                Thread.Sleep(300);
                log("Epic INPUT: AHK — TAB, TAB, TAB, TAB.");
                if (!SendTab()) return Fail("Nie udało się wykonać TAB #1 po haśle Epic Games.", out error);
                Thread.Sleep(300);
                if (!SendTab()) return Fail("Nie udało się wykonać TAB #2 po haśle Epic Games.", out error);
                Thread.Sleep(300);
                if (!SendTab()) return Fail("Nie udało się wykonać TAB #3 po haśle Epic Games.", out error);
                Thread.Sleep(300);
                if (!SendTab()) return Fail("Nie udało się wykonać TAB #4 po haśle Epic Games.", out error);
                Thread.Sleep(300);

                log("Epic INPUT: AHK krok 7 — ENTER.");
                if (!NativeKeyboardInput.SendEnter(log))
                {
                    error = "Nie udało się wysłać końcowego ENTER w Epic Games Launcher.";
                    return false;
                }

                Thread.Sleep(2000);
                log("Epic INPUT: automatyczna sekwencja logowania została wykonana.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Błąd automatycznego logowania Epic Games: " + ex.Message;
                return false;
            }
        }

        private bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }

        private IntPtr GetEpicForegroundWindow()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && IsWindowOwnedByEpic(foreground))
                return foreground;

            IntPtr main = FindMainWindowHandle();
            if (main != IntPtr.Zero)
            {
                EnsureWindowForeground(main);
                if (IsWindowOwnedByEpic(main))
                    return main;
            }

            log("Epic INPUT: aktywne okno nie należy do Epic Games Launcher.");
            return IntPtr.Zero;
        }

        private static bool IsWindowOwnedByEpic(IntPtr hwnd)
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
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private bool SendTab()
        {
            return NativeKeyboardInput.SendTab(log);
        }

        private IntPtr WaitForMainWindow(int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));

            while (DateTime.UtcNow < deadline)
            {
                IntPtr hwnd = FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                    return hwnd;

                Thread.Sleep(250);
            }

            return FindMainWindowHandle();
        }

        private static IntPtr FindMainWindowHandle()
        {
            foreach (Process process in SafeGetProcesses(MainProcessName))
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
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
