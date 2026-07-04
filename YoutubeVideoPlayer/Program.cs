using System;
using System.Windows.Forms;
using LibVLCSharp.Shared;

namespace YoutubeVideoPlayer
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Initialize LibVLC native libraries
            Core.Initialize();

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new VideoPlayerForm());
        }
    }
}
