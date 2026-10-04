using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Steam login UI automation.
    /// The code uses UI Automation only to find/focus visible edit controls;
    /// actual text is sent through the native keyboard helper, never ValuePattern.
    /// </summary>
    internal sealed class SteamUiAutomation
    {
        private const string MainProcessName = "steam";
        private const string MainWindowName = "Steam";

        private readonly Action<string> log;

        public SteamUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool Login(string username, string password, int timeoutSeconds, out string error)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return LoginCore(username, password, timeoutSeconds, out error);

            bool result = false;
            string workerError = null;
            Exception workerException = null;

            Thread thread = new Thread(() =>
            {
                try
                {
                    result = LoginCore(username, password, timeoutSeconds, out workerError);
                }
                catch (Exception ex)
                {
                    workerException = ex;
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (workerException != null)
            {
                error = "Błąd wątku automatyzacji Steam: " + workerException.Message;
                return false;
            }

            error = workerError;
            return result;
        }

        private bool LoginCore(string username, string password, int timeoutSeconds, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Brak nazwy konta Steam lub hasła.";
                return false;
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, timeoutSeconds));
            IntPtr hwnd = IntPtr.Zero;

            log("Steam INPUT: czekam na okno klienta Steam...");
            while (DateTime.UtcNow < deadline)
            {
                hwnd = FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    EnsureWindowForeground(hwnd);
                    if (LooksLikeLoginScreen(hwnd))
                        break;
                }
                Thread.Sleep(350);
            }

            if (hwnd == IntPtr.Zero)
            {
                error = "Nie znaleziono okna Steam w czasie oczekiwania.";
                return false;
            }

            if (!LooksLikeLoginScreen(hwnd))
            {
                DateTime uiDeadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < uiDeadline)
                {
                    hwnd = FindMainWindowHandle();
                    if (hwnd != IntPtr.Zero)
                    {
                        EnsureWindowForeground(hwnd);
                        if (LooksLikeLoginScreen(hwnd))
                            break;
                    }
                    Thread.Sleep(400);
                }
            }

            if (LooksLikeSteamGuard(hwnd))
            {
                error = "Steam zatrzymał logowanie na ekranie Steam Guard. Kod trzeba dokończyć ręcznie na tym etapie.";
                return false;
            }

            if (!LooksLikeLoginScreen(hwnd))
            {
                error = LooksAuthenticated(hwnd)
                    ? "Steam jest już zalogowany albo nie udało się wymusić ekranu logowania. Nie mogę bezpiecznie potwierdzić, że aktywne jest przypisane konto."
                    : "Steam nie pokazał rozpoznawalnego ekranu logowania. Zatrzymuję automatyzację, aby nie wpisać danych do niewłaściwego okna.";
                return false;
            }

            log("Steam INPUT: wykryto ekran logowania.");
            EnsureWindowForeground(hwnd);
            Thread.Sleep(1000);
            if (GetForegroundWindow() != hwnd)
            {
                error = "Steam nie jest aktywnym oknem. Przerywam wpisywanie danych, aby nie trafiły do innej aplikacji.";
                return false;
            }

            AutomationElement firstEdit = null;
            AutomationElement secondEdit = null;
            TryFindLoginEdits(hwnd, out firstEdit, out secondEdit);

            if (firstEdit != null && TryFocus(firstEdit))
            {
                log("Steam INPUT: ustawiam fokus na pole nazwy konta.");
                Thread.Sleep(150);
                if (!NativeKeyboardInput.TypeText(username, hwnd, log))
                {
                    error = "Nie udało się wpisać nazwy konta Steam.";
                    return false;
                }

                Thread.Sleep(300);
                if (secondEdit != null && TryFocus(secondEdit))
                {
                    log("Steam INPUT: ustawiam fokus na pole hasła.");
                }
                else
                {
                    log("Steam INPUT: drugie pole nie zostało znalezione, używam TAB.");
                    if (!NativeKeyboardInput.SendTab(log))
                    {
                        error = "Nie udało się przejść do pola hasła Steam.";
                        return false;
                    }
                }

                Thread.Sleep(200);
                if (!NativeKeyboardInput.TypeText(password, hwnd, log))
                {
                    error = "Nie udało się wpisać hasła Steam.";
                    return false;
                }

                Thread.Sleep(300);
                if (!NativeKeyboardInput.SendEnter(log))
                {
                    error = "Nie udało się wysłać zatwierdzenia logowania Steam.";
                    return false;
                }
            }
            else
            {
                log("Steam INPUT: UIA nie udostępniło pola edycji, używam awaryjnego przebiegu klawiaturowego.");
                EnsureWindowForeground(hwnd);
                if (!NativeKeyboardInput.TypeText(username, hwnd, log))
                {
                    error = "Nie udało się wpisać nazwy konta Steam.";
                    return false;
                }
                Thread.Sleep(300);
                if (!NativeKeyboardInput.SendTab(log))
                {
                    error = "Nie udało się przejść do pola hasła Steam.";
                    return false;
                }
                Thread.Sleep(200);
                if (!NativeKeyboardInput.TypeText(password, hwnd, log))
                {
                    error = "Nie udało się wpisać hasła Steam.";
                    return false;
                }
                Thread.Sleep(300);
                if (!NativeKeyboardInput.SendEnter(log))
                {
                    error = "Nie udało się wysłać zatwierdzenia logowania Steam.";
                    return false;
                }
            }

            log("Steam INPUT: dane wysłane. Czekam na wynik logowania...");
            DateTime verifyDeadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < verifyDeadline)
            {
                hwnd = FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    if (LooksLikeSteamGuard(hwnd))
                    {
                        error = "Steam wymaga kodu Steam Guard. Automatyczne wpisanie kodu nie jest jeszcze zaimplementowane.";
                        return false;
                    }

                    if (LooksAuthenticated(hwnd))
                    {
                        log("Steam INPUT: Steam wygląda na zalogowany.");
                        return true;
                    }
                }
                Thread.Sleep(500);
            }

            error = "Steam nie potwierdził zalogowania po wysłaniu danych.";
            return false;
        }

        private static bool TryFocus(AutomationElement element)
        {
            try
            {
                if (element == null || element.Current.IsOffscreen || !element.Current.IsEnabled)
                    return false;
                element.SetFocus();
                Thread.Sleep(100);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryFindLoginEdits(IntPtr hwnd, out AutomationElement first, out AutomationElement second)
        {
            first = null;
            second = null;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return;

                AutomationElementCollection edits = root.FindAll(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));

                var ordered = new List<Tuple<double, AutomationElement>>();
                foreach (AutomationElement edit in edits)
                {
                    try
                    {
                        var rect = edit.Current.BoundingRectangle;
                        if (rect.Width > 0 && rect.Height > 0)
                            ordered.Add(Tuple.Create(rect.Top * 10000 + rect.Left, edit));
                    }
                    catch { }
                }

                ordered.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                if (ordered.Count > 0) first = ordered[0].Item2;
                if (ordered.Count > 1) second = ordered[1].Item2;
            }
            catch { }
        }

        private static bool LooksLikeLoginScreen(IntPtr hwnd)
        {
            try
            {
                string text = GetVisibleAutomationText(hwnd).ToLowerInvariant();
                bool accountHint = ContainsAny(text, "account name", "username", "nazwa konta", "sign in", "zaloguj się");
                bool passwordHint = ContainsAny(text, "password", "hasło");
                bool signInHint = ContainsAny(text, "sign in", "zaloguj się");
                return (accountHint && passwordHint) || (signInHint && CountVisibleEdits(hwnd) >= 2);
            }
            catch
            {
                return false;
            }
        }

        public bool IsAccountSelectionOrLoginVisible(out string state)
        {
            state = null;
            IntPtr hwnd = FindMainWindowHandle();
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                if (LooksLikeAccountChooser(hwnd))
                {
                    state = "account chooser";
                    return true;
                }
                if (LooksLikeSteamGuard(hwnd))
                {
                    state = "Steam Guard";
                    return true;
                }
                if (LooksLikeLoginScreen(hwnd))
                {
                    state = "login";
                    return true;
                }
            }
            catch { }

            return false;
        }

        public bool IsAuthenticatedWindow()
        {
            IntPtr hwnd = FindMainWindowHandle();
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                if (LooksLikeAccountChooser(hwnd) || LooksLikeSteamGuard(hwnd) || LooksLikeLoginScreen(hwnd))
                    return false;
                return LooksAuthenticated(hwnd);
            }
            catch
            {
                return false;
            }
        }

        public bool WaitUntilAuthenticated(int timeoutSeconds, out string error)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return WaitUntilAuthenticatedCore(timeoutSeconds, out error);

            bool result = false;
            string workerError = null;
            Exception workerException = null;

            Thread thread = new Thread(() =>
            {
                try
                {
                    result = WaitUntilAuthenticatedCore(timeoutSeconds, out workerError);
                }
                catch (Exception ex)
                {
                    workerException = ex;
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (workerException != null)
            {
                error = "Błąd wątku automatyzacji Steam: " + workerException.Message;
                return false;
            }

            error = workerError;
            return result;
        }

        private bool WaitUntilAuthenticatedCore(int timeoutSeconds, out string error)
        {
            error = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, timeoutSeconds));
            log("Steam INPUT: czekam na potwierdzenie zalogowania wybranego konta...");
            while (DateTime.UtcNow < deadline)
            {
                IntPtr hwnd = FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    if (LooksLikeAccountChooser(hwnd))
                    {
                        error = "Steam pokazuje ekran „Kto gra?”. Wyłącz w Steam opcję „Pytaj, którego konta używać przy każdym uruchomieniu Steam”, aby Menadżer Kont mógł przełączać zapisane konto automatycznie.";
                        return false;
                    }

                    if (LooksLikeSteamGuard(hwnd))
                    {
                        error = "Steam wymaga Steam Guard. Automatyczne przełączenie zatrzymano.";
                        return false;
                    }

                    if (LooksAuthenticated(hwnd))
                        return true;
                }
                Thread.Sleep(450);
            }

            error = "Steam nie potwierdził zalogowania wybranego konta w wymaganym czasie.";
            return false;
        }

        public IntPtr GetMainWindowHandle()
        {
            return FindMainWindowHandle();
        }

        public string GetVisibleText(IntPtr hwnd)
        {
            return GetVisibleAutomationText(hwnd);
        }

        private static bool LooksLikeAccountChooser(IntPtr hwnd)
        {
            try
            {
                string text = GetVisibleAutomationText(hwnd).ToLowerInvariant();
                return ContainsAny(text,
                    "kto gra",
                    "who is playing",
                    "who's playing",
                    "choose an account",
                    "wybierz konto",
                    "choose account");
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksLikeSteamGuard(IntPtr hwnd)
        {
            try
            {
                string text = GetVisibleAutomationText(hwnd).ToLowerInvariant();
                return ContainsAny(
                    text,
                    "steam guard",
                    "steam guard code",
                    "enter the code",
                    "authentication code",
                    "wprowadź kod",
                    "kod uwierzytelniający");
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksAuthenticated(IntPtr hwnd)
        {
            try
            {
                string text = GetVisibleAutomationText(hwnd).ToLowerInvariant();
                if (ContainsAny(text, "account name", "nazwa konta", "sign in", "zaloguj się", "password", "hasło"))
                    return false;

                return ContainsAny(text, "library", "biblioteka", "store", "sklep", "friends & chat", "znajomi i czat");
            }
            catch
            {
                return false;
            }
        }

        private static int CountVisibleEdits(IntPtr hwnd)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return 0;
                AutomationElementCollection edits = root.FindAll(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
                return edits.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static string GetVisibleAutomationText(IntPtr hwnd)
        {
            var names = new List<string>();
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return string.Empty;

            AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in all)
            {
                try
                {
                    if (element.Current.IsOffscreen)
                        continue;
                    string name = element.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
                catch { }
            }

            return string.Join(" ", names);
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
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
                    if (hwnd == IntPtr.Zero)
                        continue;
                    string title = null;
                    try { title = process.MainWindowTitle; } catch { }
                    if (!string.IsNullOrWhiteSpace(title) &&
                        title.IndexOf(MainWindowName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return hwnd;
                }
                catch { }
                finally { process.Dispose(); }
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

        private bool EnsureWindowForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                ShowWindow(hwnd, 9);
                for (int attempt = 0; attempt < 5; attempt++)
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

                    Thread.Sleep(120);
                }

                return GetForegroundWindow() == hwnd;
            }
            catch (Exception ex)
            {
                log("Steam INPUT: błąd aktywacji okna: " + ex.Message);
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
