using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace YouTubeVideoPlayer.Infrastructure
{
    /// <summary>Helpers for the settings files the user edits by hand (<c>twitchsettings.json</c>, <c>appsettings.json</c>).</summary>
    public static class JsonSettings
    {
        /// <summary>
        /// The file with every setting of <paramref name="defaults"/> it does not have (or has as <c>null</c>) added with
        /// its default. The result has the order of the defaults (that of the example file), sections included; the file's
        /// values are kept as they are, keys are found whatever their case, and keys the defaults do not know (notes, typos)
        /// are kept after the known ones.
        /// </summary>
        /// <param name="added">Receives the settings that were added, as <c>section.key</c>.</param>
        public static JObject AddMissing(JObject settings, JObject defaults, ICollection<string>? added = null) =>
            AddMissing(settings, defaults, added, prefix: string.Empty);

        private static JObject AddMissing(JObject settings, JObject defaults, ICollection<string>? added, string prefix)
        {
            var result = new JObject();
            foreach (var setting in defaults.Properties())
            {
                var own = settings.Property(setting.Name, StringComparison.OrdinalIgnoreCase)?.Value;
                if (own is null or { Type: JTokenType.Null })
                {
                    result[setting.Name] = setting.Value.DeepClone();
                    added?.Add(prefix + setting.Name);
                }
                else if (setting.Value is JObject section && own is JObject ownSection)
                {
                    result[setting.Name] = AddMissing(ownSection, section, added, prefix + setting.Name + ".");
                }
                else
                {
                    result[setting.Name] = own.DeepClone();
                }
            }

            foreach (var property in settings.Properties())
            {
                if (defaults.Property(property.Name, StringComparison.OrdinalIgnoreCase) == null)
                    result[property.Name] = property.Value.DeepClone();
            }

            return result;
        }
    }
}
