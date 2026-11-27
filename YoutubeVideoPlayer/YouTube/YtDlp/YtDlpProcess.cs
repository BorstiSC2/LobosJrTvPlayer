using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.YouTube.YtDlp
{
    /// <summary>Runs yt-dlp.exe as a hidden process and collects what it prints.</summary>
    public sealed class YtDlpProcess : IYtDlpRunner
    {
        public YtDlpProcess(string executablePath)
        {
            ExecutablePath = executablePath;
        }

        public string ExecutablePath { get; }

        public async Task<YtDlpOutput> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            // A yt-dlp run by an installed Python writes UTF-8 with these. The standalone yt-dlp.exe ignores them and
            // writes in the Windows code page (see Decode).
            info.Environment["PYTHONUTF8"] = "1";
            info.Environment["PYTHONIOENCODING"] = "utf-8";

            var directory = Path.GetDirectoryName(ExecutablePath);
            if (!string.IsNullOrEmpty(directory))
                info.WorkingDirectory = directory;

            using var process = new Process { StartInfo = info };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("The process did not start.");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                throw new YtDlpException($"yt-dlp could not be started ({ExecutablePath}): {ex.Message}", ex);
            }

            // Nothing is ever typed in; a closed input makes a prompt end at once instead of waiting forever.
            process.StandardInput.Close();

            var output = ReadAllBytesAsync(process.StandardOutput.BaseStream);
            var errors = ReadAllBytesAsync(process.StandardError.BaseStream);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The one-file yt-dlp.exe unpacks itself and runs Python as a child process: end both.
                Kill(process);
                _ = output.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _ = errors.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }

            return new YtDlpOutput(
                process.ExitCode, Decode(await output.ConfigureAwait(false)), Decode(await errors.ConfigureAwait(false)));
        }

        /// <summary>
        /// The text yt-dlp printed. The standalone yt-dlp.exe writes in the Windows ANSI code page whatever it is told
        /// (measured: <c>ä</c> as E4, <c>’</c> as 92; read as UTF-8 that showed as "you�re" in its bot-check error); a
        /// yt-dlp run by an installed Python writes UTF-8. Text that is valid UTF-8 is read as UTF-8 (ASCII, like the JSON,
        /// which escapes everything else, is the same in both), anything else in the ANSI code page.
        /// </summary>
        internal static string Decode(byte[] bytes)
        {
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return AnsiCodePage.Value.GetString(bytes);
            }
        }

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>The system's ANSI code page (e.g. 1252), which Python uses for a redirected output.</summary>
        internal static readonly Lazy<Encoding> AnsiCodePage = new(() =>
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                return Encoding.GetEncoding((int)GetACP());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.Latin1;
            }
        });

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer).ConfigureAwait(false);
            return buffer.ToArray();
        }

        private static void Kill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // It has ended meanwhile, or cannot be ended: nothing more to do.
            }
        }
    }
}
