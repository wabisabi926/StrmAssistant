using System;
using System.IO;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    public class IsoMounterPathTests
    {
        [Theory]
        [InlineData(@"D:\media\disc.iso", true)]
        [InlineData("file:\"D:\\media\\disc.iso\"", true)]
        [InlineData("\"D:\\media\\disc.ISO\"", true)]
        [InlineData("http://host/disc.iso?sign=abc", true)]
        [InlineData("http://host/download?filename=disc%2Eiso", true)]
        [InlineData("http://host/disc.mkv", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void LooksLikeIsoPath_RecognizesWrappedAndRemoteForms(string raw, bool expected)
        {
            Assert.Equal(expected, IsoMounter.LooksLikeIsoPath(raw));
        }

        [Fact]
        public void TryNormalizeIsoPath_AcceptsFileWrapperForExistingFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "sa-" + Guid.NewGuid().ToString("N") + ".iso");
            File.WriteAllBytes(path, new byte[] { 0 });

            try
            {
                Assert.True(IsoMounter.TryNormalizeIsoPath("file:\"" + path + "\"", out var normalized));
                Assert.Equal(path, normalized);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("http://host/disc.iso")]
        [InlineData("file://host/disc.iso")]
        [InlineData("concat:disc.iso|disc2.iso")]
        [InlineData(@"D:\media\disc.mkv")]
        [InlineData("")]
        public void TryNormalizeIsoPath_RejectsRemoteCompositeOrNonIso(string raw)
        {
            Assert.False(IsoMounter.TryNormalizeIsoPath(raw, out _));
        }

        [Theory]
        [InlineData("http://host/disc.iso?sign=abc")]
        [InlineData("https://host/disc.iso")]
        public void TryNormalizeRemoteIso_AcceptsHttpIso(string url)
        {
            Assert.True(IsoMounter.TryNormalizeRemoteIso(url, out var normalized));
            Assert.Equal(url, normalized);
        }

        [Theory]
        [InlineData("smb://host/disc.iso")]
        [InlineData("concat:http://host/a.iso|http://host/b.iso")]
        [InlineData("http://host/disc.mkv")]
        [InlineData("")]
        public void TryNormalizeRemoteIso_RejectsNonHttpOrComposite(string raw)
        {
            Assert.False(IsoMounter.TryNormalizeRemoteIso(raw, out _));
        }
    }
}
