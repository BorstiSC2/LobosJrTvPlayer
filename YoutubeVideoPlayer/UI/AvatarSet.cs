using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// The animated wolf avatars of the overlays, read from the <see cref="FolderName"/> folder next to the executable:
    /// <c>current_*.gif</c> for the "now playing" card, <c>voteNow.gif</c> and <c>voteAboutToEnd.gif</c> for the vote
    /// ticker, <c>waiting.gif</c> for the labels in the middle and the loading texts, <c>ok.png</c> for "Ready - press Play
    /// to start". A missing or broken file is logged once and leaves its place to the icon drawn before.
    /// </summary>
    /// <remarks>Lives on the UI thread with the overlays that draw it.</remarks>
    public sealed class AvatarSet : IDisposable
    {
        public const string FolderName = "LobosResources";

        /// <summary>
        /// Per picture, how it sits in its circle, where covering the circle centered on its content is not right.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, AvatarLook> Looks = new Dictionary<string, AvatarLook>(StringComparer.OrdinalIgnoreCase)
        {
            // A round badge with its own wooden frame: whole, without the orange disc.
            ["current_2"] = new AvatarLook(Fit: true, OwnFrame: true),

            // A head with high ears that fills its canvas: a bit smaller and lower, so the ears stay inside.
            ["current_5"] = new AvatarLook(Zoom: 0.85, OffsetY: 0.04),

            // The whole wolf running: only recognisable whole, so fitted (a little bigger, the empty corners are orange).
            ["voteNow"] = new AvatarLook(Fit: true, Zoom: 1.15),
        };

        private readonly Random _random;
        private AnimatedAvatar? _lastCurrent;

        private AvatarSet(IReadOnlyList<AnimatedAvatar> current, AnimatedAvatar? voteNow, AnimatedAvatar? voteAboutToEnd,
            AnimatedAvatar? waiting, AnimatedAvatar? ok, Random random)
        {
            Current = current;
            VoteNow = voteNow;
            VoteAboutToEnd = voteAboutToEnd;
            Waiting = waiting;
            Ok = ok;
            _random = random;
        }

        /// <summary>The card's pictures (<c>current_1.gif</c>, <c>current_2.gif</c>, ...), in the order of their names.</summary>
        public IReadOnlyList<AnimatedAvatar> Current { get; }

        public AnimatedAvatar? VoteNow { get; }

        /// <summary>The vote ticker from its last warning in chat on (<see cref="VoteDisplay.Ending"/>); <see cref="VoteNow"/> without one.</summary>
        public AnimatedAvatar? VoteAboutToEnd { get; }

        public AnimatedAvatar? Waiting { get; }

        /// <summary>The first video is ready: "Ready - press Play to start" (a still picture).</summary>
        public AnimatedAvatar? Ok { get; }

        /// <summary>An empty set: every overlay keeps its icon.</summary>
        public static AvatarSet None { get; } = new(Array.Empty<AnimatedAvatar>(), null, null, null, null, new Random());

        /// <param name="directory">The folder with the pictures; <see cref="FolderName"/> next to the executable by default.</param>
        public static AvatarSet Load(string? directory = null, Random? random = null)
        {
            directory ??= Path.Combine(AppContext.BaseDirectory, FolderName);
            if (!Directory.Exists(directory))
            {
                Logger.Log($"No {FolderName} folder found; the overlays show their icons.");
                return None;
            }

            AnimatedAvatar? Read(string file)
            {
                var path = Path.Combine(directory, file);
                if (!File.Exists(path))
                    return null;

                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    return AnimatedAvatar.Load(path, Looks.TryGetValue(name, out var look) ? look : null);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Could not read {FolderName}/{file}; its overlay shows the icon instead.", ex);
                    return null;
                }
            }

            var current = Directory.GetFiles(directory, "current_*.gif")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => Read(name!))
                .OfType<AnimatedAvatar>()
                .ToList();
            return new AvatarSet(current, Read("voteNow.gif"), Read("voteAboutToEnd.gif"), Read("waiting.gif"), Read("ok.png"),
                random ?? new Random());
        }

        /// <summary>A card picture at random, never the one of the last call (if there are two or more).</summary>
        public AnimatedAvatar? NextCurrent()
        {
            if (Current.Count == 0)
                return null;

            var choices = Current.Count > 1 ? Current.Where(a => !ReferenceEquals(a, _lastCurrent)).ToList() : Current.ToList();
            _lastCurrent = choices[_random.Next(choices.Count)];
            return _lastCurrent;
        }

        /// <summary>The ticker's picture: <see cref="VoteAboutToEnd"/> when the vote is about to end, else <see cref="VoteNow"/>.</summary>
        public AnimatedAvatar? ForVote(bool ending) => ending ? VoteAboutToEnd ?? VoteNow : VoteNow;

        public void Dispose()
        {
            foreach (var avatar in Current)
                avatar.Dispose();
            VoteNow?.Dispose();
            VoteAboutToEnd?.Dispose();
            Waiting?.Dispose();
            Ok?.Dispose();
        }
    }
}
