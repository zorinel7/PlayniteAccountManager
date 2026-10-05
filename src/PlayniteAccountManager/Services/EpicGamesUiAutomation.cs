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
                log("Epic INPUT: odpowiednik AHK Sleep 15000 — czekam 15 sekund na launcher.");
                Thread.Sleep(15000);

                IntPtr hwnd = WaitForMainWindow(10);
                if (hwnd == IntPtr.Zero)
                {
                    error = "Epic Games Launcher nie udostępnił głównego okna w wymaganym czasie.";
                    return false;
                }

                if (!EnsureWindowForeground(hwnd))
                {
                    error = "Nie udało się aktywować okna Epic Games Launcher. Przerywam automatyczne wpisywanie danych.";
                    return false;
                }

                // User's AHK:
                // Send, {Tab}
                // Send, {Tab}
                log("Epic INPUT: AHK krok 1/7 — TAB, TAB.");
                SendTab();
                SendTab();

                Thread.Sleep(2000);

                // Send, username
                log("Epic INPUT: AHK krok 2/7 — wpisuję login.");
                if (!NativeKeyboardInput.TypeText(username, hwnd, log))
                {
                    error = "Nie udało się wpisać loginu Epic Games.";
                    return false;
                }

                Thread.Sleep(300);
                SendTab();
                Thread.Sleep(300);
                SendTab();
                Thread.Sleep(300);

                // Send, {Enter}
                log("Epic INPUT: AHK krok 3/7 — ENTER.");
                NativeKeyboardInput.SendEnter(log);

                // Sleep, 10000
                log("Epic INPUT: AHK krok 4/7 — czekam 10 sekund po zatwierdzeniu loginu.");
                Thread.Sleep(10000);

                // Send, {Tab} x3
                log("Epic INPUT: AHK krok 5/7 — TAB, TAB, TAB.");
                SendTab();
                Thread.Sleep(300);
                SendTab();
                Thread.Sleep(300);
                SendTab();
                Thread.Sleep(300);

                // Send, password
                log("Epic INPUT: AHK krok 6/7 — wpisuję hasło.");
                if (!NativeKeyboardInput.TypeText(password, hwnd, log))
                {
                    error = "Nie udało się wpisać hasła Epic Games.";
                    return false;
                }

                // Send, {Tab} x4
                SendTab();
                Thread.Sleep(300);
                SendTab();
                Thread.Sleep(300);
                SendTab();
                Thread.Sleep(300);
                SendTab();
                Thread.Sleep(300);

                // Send, {Enter}
                log("Epic INPUT: AHK krok 7/7 — ENTER.");
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
