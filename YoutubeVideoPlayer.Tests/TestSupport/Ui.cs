using System.Drawing;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>Helpers for WinForms tests (run them with <c>UiContext.Run(..., sta: true)</c>).</summary>
public static class Ui
{
    /// <summary>All controls below <paramref name="root"/>, depth first.</summary>
    public static IEnumerable<Control> Descendants(this Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var grandChild in child.Descendants())
                yield return grandChild;
        }
    }

    public static IEnumerable<T> All<T>(this Control root) where T : Control => root.Descendants().OfType<T>();

    public static Button Button(this Control root, string text) => root.All<Button>().Single(b => b.Text == text);

    /// <summary>Shows a form without it appearing on screen (transparent, off-screen, not in the taskbar).</summary>
    public static T ShowHidden<T>(this T form) where T : Form
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Opacity = 0;
        form.Show();
        return form;
    }

    /// <summary>
    /// Answers the next <see cref="MessageBox"/> of this thread with <paramref name="result"/> and reports its text.
    /// The button command is posted to the box as a window message, so neither keyboard nor focus is involved.
    /// </summary>
    public static void AnswerNextMessageBox(DialogResult result, Action<string>? onText = null)
    {
        const uint WM_COMMAND = 0x0111;
        const uint WM_CLOSE = 0x0010;
        const int MessageTextId = 0xFFFF;
        var threadId = NativeMethods.GetCurrentThreadId();
        var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            var box = IntPtr.Zero;
            NativeMethods.EnumThreadWindows(threadId, (hwnd, _) =>
            {
                var className = new System.Text.StringBuilder(64);
                NativeMethods.GetClassName(hwnd, className, className.Capacity);
                if (className.ToString() != "#32770" || !NativeMethods.IsWindowVisible(hwnd))
                    return true;

                box = hwnd;
                return false;
            }, IntPtr.Zero);

            if (box == IntPtr.Zero)
                return;

            timer.Stop();
            timer.Dispose();
            var text = new System.Text.StringBuilder(4096);
            NativeMethods.GetDlgItemText(box, MessageTextId, text, text.Capacity);
            onText?.Invoke(text.ToString());
            // A box with only an OK button does not use IDOK as the button's ID; closing it means "OK".
            if (NativeMethods.GetDlgItem(box, (int)result) != IntPtr.Zero)
                NativeMethods.PostMessage(box, WM_COMMAND, (IntPtr)(int)result, IntPtr.Zero);
            else
                NativeMethods.PostMessage(box, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        };
        timer.Start();
    }

    private static class NativeMethods
    {
        public delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumThreadWindows(uint threadId, EnumThreadWndProc callback, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint GetDlgItemText(IntPtr dialog, int id, System.Text.StringBuilder text, int maxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetDlgItem(IntPtr dialog, int id);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>
    /// Answers the next modal dialog of type <typeparamref name="T"/> as soon as it is open
    /// (a WinForms timer keeps running inside the dialog's message loop).
    /// </summary>
    public static void AnswerNextDialog<T>(Action<T> answer) where T : Form
    {
        var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            // Only dialogs of this test's thread: UI tests of other classes run in parallel on their own threads.
            var dialog = System.Windows.Forms.Application.OpenForms.OfType<T>()
                .FirstOrDefault(f => !f.InvokeRequired && f.Visible && f.Modal);
            if (dialog == null)
                return;

            timer.Stop();
            timer.Dispose();
            answer(dialog);
        };
        timer.Start();
    }
}
