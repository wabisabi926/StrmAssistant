using System;
using System.Linq;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    public class PlaylistSelectionRegressionTests
    {
        private static UdfTitleResolver.PlaylistEntry Entry(int count, bool sameFile, bool sameSegment)
        {
            var entry = new UdfTitleResolver.PlaylistEntry();
            for (var i = 0; i < count; i++)
                entry.Clips.Add(new UdfTitleResolver.ClipEntry
                {
                    Name = sameFile ? "00001" : i.ToString("D5"),
                    InTime = sameSegment ? 10 : 10 + i * 30,
                    Duration = 30
                });
            entry.TotalSeconds = entry.Clips.Sum(c => c.Duration);
            return entry;
        }

        [Fact]
        public void ExcessiveExactReplay_IsRejectedBeforeLongestTitleSelection()
        {
            var entry = Entry(501, true, true);
            entry.Clips[0].Name = "00114";
            for (var i = 1; i < entry.Clips.Count; i++) entry.Clips[i].Name = "00115";
            Assert.Contains("repeated segments", UdfTitleResolver.GetPlaylistRejectionReason(entry));
        }

        [Fact]
        public void OrdinaryMultiPartTitle_IsKept()
        {
            Assert.Null(UdfTitleResolver.GetPlaylistRejectionReason(Entry(15, false, false)));
        }

        [Fact]
        public void ReusedFileWithDifferentInOutTimes_IsKept()
        {
            Assert.Null(UdfTitleResolver.GetPlaylistRejectionReason(Entry(300, true, false)));
        }

        [Fact]
        public void LimitedLegitimateSegmentReplay_IsKept()
        {
            Assert.Null(UdfTitleResolver.GetPlaylistRejectionReason(Entry(2, true, true)));
        }

        [Fact]
        public void InvalidOrEmptyPlaylist_IsRejected()
        {
            Assert.NotNull(UdfTitleResolver.GetPlaylistRejectionReason(new UdfTitleResolver.PlaylistEntry()));
            var entry = Entry(1, true, true);
            entry.Clips[0].Duration = double.NaN;
            Assert.NotNull(UdfTitleResolver.GetPlaylistRejectionReason(entry));
        }

        [Theory]
        [InlineData("http://127.0.0.1/download?filename=disc%2Eiso&sign=a%2Bb%2Fc")]
        [InlineData("http://127.0.0.1/download?path=%2Ffolder%2Fdisc.ISO&sign=a%2Fb")]
        public void IsoFilenameHint_DoesNotRewriteSignedUrl(string url)
        {
            Assert.True(IsoMounter.TryNormalizeRemoteIso(url, out var normalized));
            Assert.Equal(url, normalized);
        }
    }
}
