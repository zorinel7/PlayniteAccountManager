using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Accessibility;

namespace PlayniteAccountManager.Services
{
    internal enum MsaaLoginResult
    {
        Success,
        Failed,
        FieldsNotFound
    }

    /// <summary>
    /// Launcher-specific settings for <see cref="ChromiumLoginAutomation"/>.
    /// </summary>
    internal sealed class ChromiumLoginOptions
    {
        /// <summary>Prefix used in log messages, e.g. "EA MSAA".</summary>
        public string LogPrefix;

        /// <summary>User-facing launcher name used in error messages.</summary>
        public string LauncherName;

        /// <summary>Process names that own the launcher window.</summary>
        public string[] ProcessNames;

        /// <summary>Optional: identifies the login window among several browser windows.</summary>
        public Func<IntPtr, bool> IsLoginWindow;

        /// <summary>
        /// Only the window accepted by <see cref="IsLoginWindow"/> may be used. Prevents
        /// typing into the main launcher window when the launcher is already signed in.
        /// </summary>
        public bool RequireLoginWindowMatch;

        /// <summary>
        /// When the page opens on the password step of a remembered account, go
        /// back first so the e-mail of the assigned account is entered.
        /// </summary>
        public bool GoBackFromRememberedAccount;

        /// <summary>Login succeeded when the login window closes or hides.</summary>
        public bool SuccessWhenWindowCloses = true;

        /// <summary>
        /// Login succeeded when the window stays open but no login field has been
        /// visible for this long (e.g. the launcher switched to the library).
        /// Zero disables the rule.
        /// </summary>
        public TimeSpan SuccessWhenFormGoneFor = TimeSpan.Zero;

        public TimeSpan WindowTimeout = TimeSpan.FromSeconds(60);
        public TimeSpan FieldsTimeout = TimeSpan.FromSeconds(30);
        public TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
        public TimeSpan ResultTimeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Automatic login for launchers whose sign-in page is a Chromium/CEF page
    /// rendered in a window (EA App, Ubisoft Connect).
    ///
    /// The page is not exposed through UI Automation, but Chromium exposes it
    /// through MSAA (IAccessible). Fields are found by role and state, not by
    /// caption or TAB order, so the flow is independent of the launcher language:
    ///   e-mail field:   visible editable text field (default action "activate")
    ///   password field: visible protected text field
    ///   submit button:  first full-width button below the last filled field
    ///   back button:    top-most button above the form
    /// Hidden honeypot inputs placed off-screen by the page are ignored.
    /// Credentials are typed as keyboard input, never read back and never logged.
    /// </summary>
    internal sealed class ChromiumLoginAutomation
    {
        private const string RenderWidgetClass = "Chrome_RenderWidgetHostHWND";

        private const int RoleText = 42;
        private const int RolePushButton = 43;

        private const int StateFocused = 0x4;
        private const int StateInvisible = 0x8000;
        private const int StateOffscreen = 0x10000;
        private const int StateFocusable = 0x100000;
        private const int StateProtected = 0x20000000;

        private const int SelflagTakeFocus = 0x1;
        private const uint ObjIdClient = 0xFFFFFFFC;
        private const int MaxNodes = 3000;
        private const int MaxSubmitDistance = 320;
        private const int MaxSubmitOffset = 24;

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        private readonly ChromiumLoginOptions options;
        private readonly Action<string> log;
        private bool inputSent;

        public ChromiumLoginAutomation(ChromiumLoginOptions options, Action<string> log)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.log = log ?? (_ => { });
        }

        /// <summary>
        /// Runs the login on a dedicated STA thread. Returns FieldsNotFound only
        /// when no credential has been typed yet, so the caller can safely fall
        /// back to its keyboard sequence.
        /// </summary>
        public MsaaLoginResult Login(string username, string password, out string error)
        {
            MsaaLoginResult result = MsaaLoginResult.FieldsNotFound;
            string workerError = null;
            Exception workerException = null;

            RunSta(() => result = LoginCore(username, password, out workerError), ex => workerException = ex);

            if (workerException != null)
            {
                error = options.LauncherName + " (MSAA): błąd automatyzacji: " + workerException.Message;
                return inputSent ? MsaaLoginResult.Failed : MsaaLoginResult.FieldsNotFound;
            }

            error = workerError;
            return result;
        }

        // ------------------------------------------------------------ diagnostics

        /// <summary>Read-only: waits for the login page and describes it. Types nothing.</summary>
        internal string Probe(int timeoutSeconds)
        {
            string description = null;
            RunSta(() =>
            {
                IntPtr window = WaitForWindow(TimeSpan.FromSeconds(timeoutSeconds));
                description = window == IntPtr.Zero
                    ? "window-not-found"
                    : WaitForScreen(window, s => s.HasForm, TimeSpan.FromSeconds(timeoutSeconds)).Describe();
            }, ex => description = "error: " + ex.Message);
            return description;
        }

        /// <summary>Brings the window to the front and focuses the first field. Types nothing.</summary>
        internal string ProbeFocus(int timeoutSeconds)
        {
            string description = null;
            RunSta(() =>
            {
                IntPtr window = WaitForWindow(TimeSpan.FromSeconds(timeoutSeconds));
                Screen screen = window == IntPtr.Zero ? Screen.Empty : ReadScreen(window);
                IAccessible field = screen.EmailField ?? screen.PasswordField;
                if (!screen.HasForm)
                {
                    description = "no-field: " + screen.Describe();
                    return;
                }

                bool foreground = EAAppUiAutomation.EnsureWindowForeground(window);
                bool focused = FocusField(field);
                description = "foreground=" + foreground + " focused=" + focused + " " + screen.Describe();
            }, ex => description = "error: " + ex.Message);
            return description;
        }

        /// <summary>
        /// Diagnostic: types a harmless test text into the e-mail field, reports
        /// the resulting field length and clears the field again. Submits nothing.
        /// </summary>
        internal string ProbeType(string testText)
        {
            return ProbeType(testText, true);
        }

        internal string ProbeType(string testText, bool clearAfter, bool passwordField = false)
        {
            string description = null;
            RunSta(() =>
            {
                IntPtr window = WaitForWindow(TimeSpan.FromSeconds(10));
                Screen screen = window == IntPtr.Zero ? Screen.Empty : ReadScreen(window);
                IAccessible target = passwordField ? screen.PasswordField : screen.EmailField;
                if (target == null)
                {
                    description = "no-field: " + screen.Describe();
                    return;
                }

                bool foreground = EAAppUiAutomation.EnsureWindowForeground(window);
                bool focused = FocusField(target);
                string strategy;
                TypeResult typed = TypeIntoField(window, target, testText, out strategy);
                int length = GetValueLength(target);
                description = "strategy=" + strategy + " ";
                if (clearAfter)
                {
                    NativeKeyboardInput.SendSelectAll(log);
                    NativeKeyboardInput.Key(0x2E, log); // Delete
                    Thread.Sleep(200);
                }
                description += "foreground=" + foreground + " focused=" + focused + " result=" + typed +
                              " lengthAfterTyping=" + length + " lengthAfterClear=" + GetValueLength(target);
            }, ex => description = "error: " + ex.Message);
            return description;
        }

        // ------------------------------------------------------------------ flow

        private MsaaLoginResult LoginCore(string username, string password, out string error)
        {
            error = null;
            inputSent = false;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanych dla konta " + options.LauncherName + ".";
                return MsaaLoginResult.Failed;
            }

            Log("czekam na okno logowania (do " + (int)options.WindowTimeout.TotalSeconds + " s).");
            IntPtr window = WaitForWindow(options.WindowTimeout);
            if (window == IntPtr.Zero)
            {
                error = options.LauncherName + " (MSAA): nie znaleziono okna logowania.";
                return MsaaLoginResult.FieldsNotFound;
            }

            Log("okno logowania znalezione, czekam na formularz.");
            Screen screen = WaitForScreen(window, s => s.HasForm, options.FieldsTimeout);
            if (!screen.HasForm)
            {
                error = options.LauncherName + " (MSAA): formularz logowania nie udostępnił pól w drzewie dostępności.";
                return MsaaLoginResult.FieldsNotFound;
            }

            if (screen.EmailField == null && options.GoBackFromRememberedAccount)
            {
                // A remembered e-mail may belong to a different account, so go
                // back and enter the e-mail of the assigned account.
                Log("formularz pokazuje krok hasła dla zapamiętanego konta, wracam do kroku e-mail.");
                if (!Press(screen.BackButton))
                {
                    error = options.LauncherName + " (MSAA): nie udało się wrócić do kroku e-mail z zapamiętanego konta.";
                    return MsaaLoginResult.Failed;
                }

                screen = WaitForScreen(window, s => s.EmailField != null, options.StepTimeout);
                if (screen.EmailField == null)
                {
                    error = options.LauncherName + " (MSAA): po powrocie nie pojawiło się pole e-mail.";
                    return MsaaLoginResult.Failed;
                }
            }

            if (screen.EmailField != null)
            {
                bool samePage = screen.PasswordField != null;
                Log(samePage ? "wpisuję e-mail." : "krok 1/2, wpisuję e-mail.");
                if (!FillField(window, screen.EmailField, username, "e-mail", out error))
                    return inputSent ? MsaaLoginResult.Failed : MsaaLoginResult.FieldsNotFound;

                if (!samePage)
                {
                    Submit(window, screen.SubmitButton, "e-mail");

                    screen = WaitForScreen(window, s => s.PasswordField != null, options.StepTimeout);
                    if (screen.PasswordField == null)
                    {
                        error = options.LauncherName + " nie przeszedł do kroku hasła (np. nieprawidłowy e-mail, CAPTCHA lub dodatkowa weryfikacja). Dokończ logowanie w oknie launchera.";
                        return MsaaLoginResult.Failed;
                    }
                }
            }

            Log(screen.EmailField == null ? "krok 2/2, wpisuję hasło." : "wpisuję hasło.");
            if (!FillField(window, screen.PasswordField, password, "hasło", out error))
                return MsaaLoginResult.Failed;

            Submit(window, screen.SubmitButton, "hasło");

            return WaitForResult(window, out error);
        }

        private bool FillField(IntPtr window, IAccessible field, string value, string fieldName, out string error)
        {
            error = null;

            if (!EAAppUiAutomation.EnsureWindowForeground(window))
            {
                error = options.LauncherName + " (MSAA): nie udało się aktywować okna logowania.";
                return false;
            }

            if (!FocusField(field))
            {
                error = options.LauncherName + " (MSAA): nie udało się ustawić fokusu na polu „" + fieldName + "”.";
                return false;
            }

            string strategy;
            TypeResult result = TypeIntoField(window, field, value, out strategy);
            if (result == TypeResult.Typed)
            {
                Log("pole „" + fieldName + "” wypełnione (" + strategy + ").");
                return true;
            }

            error = result == TypeResult.InputFailed
                ? options.LauncherName + " (MSAA): nie udało się wpisać pola „" + fieldName + "”."
                : options.LauncherName + " (MSAA): pole „" + fieldName + "” pozostało puste po wpisaniu.";
            return false;
        }

        private enum TypeResult
        {
            Typed,
            FieldStayedEmpty,
            InputFailed
        }

        /// <summary>
        /// Types the value and checks the field length (the value itself is never
        /// read back). Some launchers (Ubisoft Connect controller navigation) only
        /// highlight a focused field until it is activated, so further attempts
        /// activate the field first: MSAA default action, then ENTER.
        /// ENTER is only sent while the field is still empty, so it cannot submit
        /// a filled form.
        /// </summary>
        private TypeResult TypeIntoField(IntPtr window, IAccessible field, string value, out string strategy)
        {
            string[] strategies = { "fokus", "aktywacja pola", "ENTER" };

            for (int attempt = 0; attempt < strategies.Length; attempt++)
            {
                strategy = strategies[attempt];

                if (attempt == 1)
                {
                    try { field.accDoDefaultAction(0); } catch { }
                    Thread.Sleep(200);
                    FocusField(field);
                }
                else if (attempt == 2)
                {
                    FocusField(field);
                    if (!EAAppUiAutomation.EnsureWindowForeground(window) || !NativeKeyboardInput.SendEnter(log))
                        return TypeResult.InputFailed;
                    Thread.Sleep(300);
                }

                // Replace anything the page pre-filled (e.g. a remembered e-mail).
                if (!NativeKeyboardInput.SendSelectAll(log))
                    return TypeResult.InputFailed;

                Thread.Sleep(100);

                inputSent = true;
                if (!NativeKeyboardInput.TypeText(value, window, log))
                    return TypeResult.InputFailed;

                Thread.Sleep(300);

                int length = GetValueLength(field);
                if (length < 0 || length == value.Length)
                    return TypeResult.Typed;

                if (length > 0)
                {
                    // Partially typed or merged with old content: never submit that.
                    Log("pole zawiera " + length + " znaków zamiast " + value.Length + ".");
                    return TypeResult.FieldStayedEmpty;
                }

                Log("pole pozostało puste po próbie: " + strategy + ".");
            }

            strategy = null;
            return TypeResult.FieldStayedEmpty;
        }

        private bool FocusField(IAccessible field)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                try { field.accSelect(SelflagTakeFocus, 0); } catch { }
                Thread.Sleep(100);

                if ((GetState(field) & StateFocused) != 0)
                    return true;
            }

            return false;
        }

        private void Submit(IntPtr window, IAccessible button, string afterField)
        {
            if (Press(button))
            {
                Log("wysłano formularz przyciskiem (po polu: " + afterField + ").");
                return;
            }

            Log("nie znaleziono przycisku wysyłania, wysyłam ENTER.");
            if (EAAppUiAutomation.EnsureWindowForeground(window))
                NativeKeyboardInput.SendEnter(log);
        }

        private MsaaLoginResult WaitForResult(IntPtr window, out string error)
        {
            error = null;
            Screen last = Screen.Empty;
            DateTime deadline = DateTime.UtcNow + options.ResultTimeout;
            DateTime formGoneSince = DateTime.MaxValue;

            while (DateTime.UtcNow < deadline)
            {
                if (options.SuccessWhenWindowCloses && (!IsWindow(window) || !IsWindowVisible(window)))
                {
                    Log("okno logowania zostało zamknięte, launcher jest zalogowany.");
                    return MsaaLoginResult.Success;
                }

                last = ReadScreen(window);

                if (options.SuccessWhenFormGoneFor > TimeSpan.Zero)
                {
                    // A page with content but without any login field.
                    if (!last.HasForm && last.NodeCount > 1)
                    {
                        if (formGoneSince == DateTime.MaxValue)
                            formGoneSince = DateTime.UtcNow;
                        else if (DateTime.UtcNow - formGoneSince >= options.SuccessWhenFormGoneFor)
                        {
                            Log("formularz logowania zniknął, launcher jest zalogowany.");
                            return MsaaLoginResult.Success;
                        }
                    }
                    else
                    {
                        formGoneSince = DateTime.MaxValue;
                    }
                }

                Thread.Sleep(PollInterval);
            }

            if (last.PasswordField != null)
                error = options.LauncherName + " nie zaakceptował logowania (sprawdź zapisane hasło). Dokończ logowanie w oknie launchera.";
            else if (last.EmailField != null)
                error = options.LauncherName + " wymaga dodatkowej weryfikacji (np. kodu 2FA). Dokończ logowanie w oknie launchera.";
            else
                error = options.LauncherName + " nie potwierdził zalogowania w ciągu " + (int)options.ResultTimeout.TotalSeconds + " s.";

            return MsaaLoginResult.Failed;
        }

        private void Log(string message)
        {
            log(options.LogPrefix + ": " + message);
        }

        // ---------------------------------------------------------------- screen

        private sealed class Node
        {
            public IAccessible Acc;
            public int Role;
            public int State;
            public string DefaultAction;
            public int X, Y, Width, Height;

            public bool IsVisible =>
                (State & (StateInvisible | StateOffscreen)) == 0 &&
                Width >= 20 && Height >= 10 && X > -1000 && Y > -1000;

            public int Bottom => Y + Height;
        }

        private sealed class Screen
        {
            public static readonly Screen Empty = new Screen();

            public IAccessible EmailField;
            public IAccessible PasswordField;
            public IAccessible SubmitButton;
            public IAccessible BackButton;
            public int NodeCount;

            // A login form: a password field, or an e-mail field with its own submit
            // button right below it (rules out e.g. a search box in the main window).
            public bool HasForm => PasswordField != null || (EmailField != null && SubmitButton != null);

            public string Describe()
            {
                return "nodes=" + NodeCount +
                       " email=" + (EmailField != null) +
                       " pwdField=" + (PasswordField != null) +
                       " submit=" + (SubmitButton != null) +
                       " back=" + (BackButton != null);
            }
        }

        private Screen WaitForScreen(IntPtr window, Func<Screen, bool> accept, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            DateTime nextWake = DateTime.MinValue;
            Screen screen = Screen.Empty;

            while (DateTime.UtcNow < deadline)
            {
                if (!IsWindow(window))
                    return Screen.Empty;

                screen = ReadScreen(window);
                if (accept(screen))
                    return screen;

                // An empty page tree means Chromium renderer accessibility is still
                // off. A UI Automation client walking the window switches it on;
                // a plain MSAA request alone does not.
                if (screen.NodeCount <= 1 && DateTime.UtcNow >= nextWake)
                {
                    WakeAccessibility(window);
                    nextWake = DateTime.UtcNow.AddSeconds(2);
                }

                Thread.Sleep(PollInterval);
            }

            return accept(screen) ? screen : Screen.Empty;
        }

        private void WakeAccessibility(IntPtr window)
        {
            try
            {
                // An MSAA request on the top-level window makes Chromium create
                // its render widget; a UI Automation walk switches the page tree on.
                IAccessible top = GetRoot(window);
                if (top != null)
                {
                    try { int unused = top.accChildCount; } catch { }
                }

                AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }
            catch (Exception ex)
            {
                Log("nie udało się aktywować drzewa dostępności: " + ex.Message);
            }
        }

        private static Screen ReadScreen(IntPtr window)
        {
            IntPtr render = FindRenderWidget(window);
            if (render == IntPtr.Zero)
                return Screen.Empty;

            IAccessible root = GetRoot(render);
            if (root == null)
                return Screen.Empty;

            var nodes = new List<Node>();
            Collect(root, 0, nodes);

            Node password = null;
            foreach (Node node in nodes)
            {
                if (node.Role == RoleText && node.IsVisible && (node.State & StateProtected) != 0)
                {
                    password = node;
                    break;
                }
            }

            // Real inputs have the "activate" default action; clickable wrappers
            // that Chromium also reports as text ("click", "click ancestor") do not.
            // On a combined form the e-mail field is the closest one above the password.
            Node email = null;
            foreach (Node node in nodes)
            {
                if (node.Role != RoleText || !node.IsVisible ||
                    (node.State & StateProtected) != 0 ||
                    (node.State & StateFocusable) == 0 ||
                    IsClickWrapper(node))
                    continue;

                if (password != null && node.Bottom > password.Y)
                    continue;

                if (email == null || (password != null && node.Y > email.Y))
                    email = node;
            }

            var screen = new Screen { NodeCount = nodes.Count, EmailField = email?.Acc, PasswordField = password?.Acc };
            Node top = email ?? password;
            Node last = password ?? email;
            if (top == null)
                return screen;

            Node submit = null;
            Node back = null;
            foreach (Node node in nodes)
            {
                if (node.Role != RolePushButton || !node.IsVisible)
                    continue;

                // Primary action: first (nearly) full-width button below the form,
                // left-aligned with the field and close to it. Skips "show password"
                // inside the field and small links such as "Can't log in?".
                if (node.Y >= last.Bottom && node.Y - last.Bottom <= MaxSubmitDistance &&
                    Math.Abs(node.X - last.X) <= MaxSubmitOffset &&
                    node.Width >= last.Width * 0.8 &&
                    (submit == null || node.Y < submit.Y))
                    submit = node;

                // Back: top-most button above the form.
                if (node.Bottom <= top.Y && (back == null || node.Y < back.Y))
                    back = node;
            }

            screen.SubmitButton = submit?.Acc;
            screen.BackButton = back?.Acc;
            return screen;
        }

        private static bool IsClickWrapper(Node node)
        {
            return node.DefaultAction != null &&
                   node.DefaultAction.StartsWith("click", StringComparison.OrdinalIgnoreCase);
        }

        private static void Collect(IAccessible acc, int depth, List<Node> nodes)
        {
            if (acc == null || depth > 60 || nodes.Count >= MaxNodes)
                return;

            var node = new Node { Acc = acc, Role = GetRole(acc), State = GetState(acc) };
            try { node.DefaultAction = acc.get_accDefaultAction(0); } catch { }
            try { acc.accLocation(out node.X, out node.Y, out node.Width, out node.Height, 0); } catch { }
            nodes.Add(node);

            int count = 0;
            try { count = acc.accChildCount; } catch { }
            if (count <= 0)
                return;

            var children = new object[count];
            int obtained;
            if (AccessibleChildren(acc, 0, count, children, out obtained) != 0)
                return;

            for (int i = 0; i < obtained; i++)
                Collect(children[i] as IAccessible, depth + 1, nodes);
        }

        private static bool Press(IAccessible button)
        {
            if (button == null)
                return false;

            try
            {
                button.accDoDefaultAction(0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Length of the field value; the value itself is never kept or logged.</summary>
        private static int GetValueLength(IAccessible acc)
        {
            try { return (acc.get_accValue(0) ?? string.Empty).Length; }
            catch { return -1; }
        }

        private static int GetRole(IAccessible acc)
        {
            try { return Convert.ToInt32(acc.get_accRole(0)); }
            catch { return 0; }
        }

        private static int GetState(IAccessible acc)
        {
            try { return unchecked((int)Convert.ToInt64(acc.get_accState(0))); }
            catch { return 0; }
        }

        // --------------------------------------------------------------- windows

        private IntPtr WaitForWindow(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                IntPtr hwnd = FindWindow();
                if (hwnd != IntPtr.Zero)
                    return hwnd;

                Thread.Sleep(PollInterval);
            }

            return FindWindow();
        }

        /// <summary>
        /// Visible top-level window of the launcher that hosts a Chromium page;
        /// the one accepted by <see cref="ChromiumLoginOptions.IsLoginWindow"/> wins.
        /// </summary>
        private IntPtr FindWindow()
        {
            var processIds = new HashSet<uint>();
            foreach (string name in options.ProcessNames)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (Process process in processes)
                {
                    try { processIds.Add((uint)process.Id); }
                    catch { }
                    finally { process.Dispose(); }
                }
            }

            if (processIds.Count == 0)
                return IntPtr.Zero;

            IntPtr preferred = IntPtr.Zero;
            IntPtr anyBrowser = IntPtr.Zero;

            EnumWindows((hwnd, lParam) =>
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (!processIds.Contains(pid) || !IsWindowVisible(hwnd))
                    return true;

                // Chromium drops its render widget while the window is minimized.
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SwRestore);

                if (!IsBrowserWindow(hwnd))
                    return true;

                if (anyBrowser == IntPtr.Zero)
                    anyBrowser = hwnd;

                if (options.IsLoginWindow != null)
                {
                    bool match = false;
                    try { match = options.IsLoginWindow(hwnd); } catch { }
                    if (match)
                    {
                        preferred = hwnd;
                        return false;
                    }
                }

                return true;
            }, IntPtr.Zero);

            if (preferred != IntPtr.Zero)
                return preferred;

            return options.RequireLoginWindowMatch ? IntPtr.Zero : anyBrowser;
        }

        /// <summary>
        /// A Chromium/CEF browser window. The render widget itself may not exist
        /// yet: Chromium creates it only after an accessibility client asks for it.
        /// </summary>
        private static bool IsBrowserWindow(IntPtr hwnd)
        {
            if (GetClass(hwnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
                return true;

            bool found = false;
            EnumChildWindows(hwnd, (child, lParam) =>
            {
                string className = GetClass(child);
                found = className == RenderWidgetClass ||
                        className == "CefBrowserWindow" ||
                        className.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);
                return !found;
            }, IntPtr.Zero);
            return found;
        }

        private static string GetClass(IntPtr hwnd)
        {
            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            return className.ToString();
        }

        internal static IntPtr FindRenderWidget(IntPtr parent)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (hwnd, lParam) =>
            {
                var className = new StringBuilder(64);
                GetClassName(hwnd, className, className.Capacity);
                if (className.ToString() == RenderWidgetClass && IsWindowVisible(hwnd))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static IAccessible GetRoot(IntPtr hwnd)
        {
            Guid iid = typeof(IAccessible).GUID;
            IAccessible accessible;
            return AccessibleObjectFromWindow(hwnd, ObjIdClient, ref iid, out accessible) == 0 ? accessible : null;
        }

        private static void RunSta(Action action, Action<Exception> onError)
        {
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { onError(ex); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        private const int SwRestore = 9;

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            uint objectId,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleChildren(
            IAccessible container,
            int childStart,
            int childCount,
            [Out] object[] children,
            out int obtained);
    }
}
