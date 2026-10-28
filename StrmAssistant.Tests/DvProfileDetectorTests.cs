using System.Collections.Generic;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    public class DvProfileDetectorTests
    {
        private static MediaStream Video(int width, int height, string codec = "hevc",
            ExtendedVideoTypes type = ExtendedVideoTypes.None)
        {
            return new MediaStream
            {
                Type = MediaStreamType.Video,
                Width = width,
                Height = height,
                Codec = codec,
                ExtendedVideoType = type
            };
        }

        [Fact]
        public void Detects_4k_Base_And_1080p_Enhancement()
        {
            var baseLayer = Video(3840, 2160, "hevc", ExtendedVideoTypes.Hdr10);
            var enhancementLayer = Video(1920, 1080);

            Assert.True(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, enhancementLayer }, out var found, out var reason));

            Assert.Same(enhancementLayer, found);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void Detects_When_Enhancement_Comes_First()
        {
            var baseLayer = Video(3840, 2160, "hevc", ExtendedVideoTypes.Hdr10);
            var enhancementLayer = Video(1920, 1080);

            Assert.True(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { enhancementLayer, baseLayer }, out var found, out _));

            Assert.Same(enhancementLayer, found);
        }

        [Fact]
        public void Picks_Half_Size_Stream_When_Extra_Video_Tracks_Exist()
        {
            var baseLayer = Video(3840, 2160, "hevc", ExtendedVideoTypes.Hdr10);
            var enhancementLayer = Video(1920, 1080);
            var extra = Video(1280, 720, "hevc", ExtendedVideoTypes.Hdr10);

            Assert.True(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, extra, enhancementLayer }, out var found, out _));

            Assert.Same(enhancementLayer, found);
        }

        [Fact]
        public void Rejects_When_Enhancement_Is_Not_Half_Size()
        {
            var baseLayer = Video(3840, 2160, "hevc", ExtendedVideoTypes.Hdr10);
            var notHalf = Video(1920, 800);

            Assert.False(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, notHalf }, out var found, out _));

            Assert.Null(found);
        }

        [Fact]
        public void Rejects_When_Enhancement_Is_Not_Hevc()
        {
            var baseLayer = Video(3840, 2160, "hevc", ExtendedVideoTypes.Hdr10);
            var avc = Video(1920, 1080, "h264");

            Assert.False(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, avc }, out _, out _));
        }

        [Fact]
        public void Rejects_When_Base_Layer_Is_Not_Hdr10()
        {
            var baseLayer = Video(3840, 2160, "hevc", ExtendedVideoTypes.None);
            var enhancementLayer = Video(1920, 1080);

            Assert.False(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, enhancementLayer }, out _, out _));
        }

        [Fact]
        public void Rejects_Single_Video_Stream()
        {
            Assert.False(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { Video(3840, 2160, "hevc", ExtendedVideoTypes.Hdr10) }, out _, out _));
        }

        [Fact]
        public void Rejects_Empty_Or_Null_Input()
        {
            Assert.False(DvProfileDetector.TryDetectDualLayerProfile7(
                new List<MediaStream>(), out _, out _));
            Assert.False(DvProfileDetector.TryDetectDualLayerProfile7(null, out _, out _));
        }

        [Fact]
        public void Ignores_Non_Video_And_Invalid_Dimensions()
        {
            var baseLayer = Video(3840, 2160, "hevc", ExtendedVideoTypes.Hdr10);
            var audio = new MediaStream { Type = MediaStreamType.Audio, Codec = "aac" };
            var noDimensions = new MediaStream
            {
                Type = MediaStreamType.Video,
                Codec = "hevc",
                ExtendedVideoType = ExtendedVideoTypes.Hdr10
            };

            Assert.False(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, audio, noDimensions }, out _, out _));
        }

        [Fact]
        public void Codec_Comparison_Is_Case_Insensitive()
        {
            var baseLayer = Video(3840, 2160, "HEVC", ExtendedVideoTypes.Hdr10);
            var enhancementLayer = Video(1920, 1080, "HeVc");

            Assert.True(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, enhancementLayer }, out var found, out _));

            Assert.Same(enhancementLayer, found);
        }

        [Fact]
        public void Allows_Two_Pixel_Rounding_Difference()
        {
            var baseLayer = Video(3840, 2160, "hevc", ExtendedVideoTypes.Hdr10);
            var enhancementLayer = Video(1919, 1079);

            Assert.True(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, enhancementLayer }, out var found, out _));

            Assert.Same(enhancementLayer, found);
        }

        [Fact]
        public void Detects_1080p_Base_And_540p_Enhancement()
        {
            var baseLayer = Video(1920, 1080, "hevc", ExtendedVideoTypes.Hdr10);
            var enhancementLayer = Video(960, 540);

            Assert.True(DvProfileDetector.TryDetectDualLayerProfile7(
                new[] { baseLayer, enhancementLayer }, out var found, out _));

            Assert.Same(enhancementLayer, found);
        }
    }
}
