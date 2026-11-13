using System.Drawing;
using System.Reflection;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>The application icon (<c>LobosJrTvTwitchIcon.ico</c>, embedded as "app.ico"), shared by all dialogs.</summary>
    internal static class AppIcon
    {
        private static Icon? _icon;

        /// <summary>The icon, or <c>null</c> if the resource is missing (the dialog then keeps the default one).</summary>
        public static Icon? Instance
        {
            get
            {
                if (_icon != null)
                    return _icon;

                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                return stream == null ? null : _icon = new Icon(stream);
            }
        }
    }
}
