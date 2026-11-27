using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Infrastructure
{
    /// <summary>
    /// The app's own settings in <c>appsettings.json</c> next to the exe (not Twitch: those are in
    /// <c>twitchsettings.json</c>, which turns the bot off when it is missing). The file is created with every setting on
    /// the first start; settings it lacks are added with their defaults. Read once at startup, so a change takes effect
    /// on the next start.
    /// </summary>
    public sealed class AppSettings
    {
        public const string FileName = "appsettings.json";

        /// <summary>The layout of the file. Raise it (and add a migration) when a setting is renamed or moved.</summary>
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        [JsonProperty("youtube")]
        public YouTubeSettings YouTube { get; set; } = new();

        public sealed class YouTubeSettings
        {
            /// <summary><c>youtubeExplode</c> or <c>ytDlp</c>; see <see cref="SelectedBackend"/>.</summary>
            public string Backend { get; set; } = YouTubeBackend.YoutubeExplode.SettingValue();

            /// <summary>Tries a failed lookup once more with the other backend (if it is available).</summary>
            public bool Fallback { get; set; } = true;

            public YtDlpSettings YtDlp { get; set; } = new();

            /// <summary><see cref="Backend"/> as read; YoutubeExplode if it names none.</summary>
            [JsonIgnore]
            public YouTubeBackend SelectedBackend { get; set; }
        }

        public sealed class YtDlpSettings
        {
            /// <summary>yt-dlp.exe: absolute, or relative to the app folder; a bare file name is also looked for on <c>PATH</c>.</summary>
            public string Path { get; set; } = "yt-dlp.exe";

            /// <summary>Passed to every yt-dlp run after the app's own arguments (e.g. <c>--cookies-from-browser firefox</c>).</summary>
            public List<string> ExtraArguments { get; set; } = new();

            /// <summary>At startup, says so in a message box when a newer yt-dlp has been released (the app never updates it).</summary>
            public bool CheckForUpdates { get; set; } = true;
        }

        /// <summary>
        /// Loads the settings. Never fails: if the file cannot be read or understood, the defaults are used (and the file is
        /// left as it is). A missing file is created with every setting; settings the file lacks are added with their
        /// defaults and the file is written back. A failed write is only logged.
        /// </summary>
        /// <param name="baseDirectory">Folder containing the file; the application folder by default.</param>
        public static AppSettings Load(string? baseDirectory = null)
        {
            var path = System.IO.Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, FileName);
            try
            {
                var text = JsonFile.ReadText(path);
                JObject file;
                if (text == null)
                {
                    file = new JObject();
                }
                else if (JToken.Parse(text) is JObject json)
                {
                    file = json;
                }
                else
                {
                    Logger.Log($"{FileName} does not contain a JSON object; the default settings are used.");
                    return Defaults();
                }

                var added = new List<string>();
                var completed = JsonSettings.AddMissing(file, DefaultJson(), added);

                // Read before writing anything: a file with a value of the wrong type is left as the user wrote it.
                var settings = completed.ToObject<AppSettings>(JsonSerializer.CreateDefault()) ?? new AppSettings();
                settings.Normalize();

                var version = completed["version"] is JValue { Type: JTokenType.Integer } value ? value.Value<int>() : CurrentVersion;
                if (version > CurrentVersion)
                    Logger.Log($"{FileName} has version {version}, written by a newer version of the app; it is read as far as understood and not changed.");
                else if (text == null)
                    Save(path, completed, $"Created {FileName} with the default settings.");
                else if (!JToken.DeepEquals(completed, file))
                    Save(path, completed, $"Wrote {FileName} with every setting. Added with their defaults: {string.Join(", ", added)}.");

                return settings;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read {FileName}; the default settings are used.", ex);
                return Defaults();
            }
        }

        /// <summary>Every setting with its default, in the order of the file.</summary>
        internal static JObject DefaultJson() =>
            JObject.FromObject(new AppSettings(), JsonSerializer.Create(new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            }));

        private static AppSettings Defaults()
        {
            var settings = new AppSettings();
            settings.Normalize();
            return settings;
        }

        private static void Save(string path, JObject settings, string message)
        {
            try
            {
                JsonFile.WriteAtomic(path, settings);
                Logger.Log(message);
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not write {FileName}; the settings are used anyway, and it is tried again on the next start.", ex);
                try
                {
                    File.Delete(path + ".tmp"); // what WriteAtomic may have left behind
                }
                catch (Exception)
                {
                    // nothing more to do
                }
            }
        }

        /// <summary>Fills sections the file set to <c>null</c>, trims the values and reads the backend.</summary>
        private void Normalize()
        {
            YouTube ??= new();
            YouTube.YtDlp ??= new();

            Version = CurrentVersion;
            var ytDlp = YouTube.YtDlp;
            ytDlp.Path = (ytDlp.Path ?? string.Empty).Trim();
            if (ytDlp.Path.Length == 0)
                ytDlp.Path = new YtDlpSettings().Path;
            ytDlp.ExtraArguments = (ytDlp.ExtraArguments ?? new())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .ToList();

            var backend = YouTubeBackendNames.Parse(YouTube.Backend);
            if (backend == null)
                Logger.Log($"youtube.backend '{YouTube.Backend}' in {FileName} is neither 'youtubeExplode' nor 'ytDlp'; YoutubeExplode is used.");
            YouTube.SelectedBackend = backend ?? YouTubeBackend.YoutubeExplode;
        }
    }
}
