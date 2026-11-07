using StrmAssistant.Common;
using StrmAssistant.Options;
using System.Collections.Generic;
using Xunit;

namespace StrmAssistant.Options
{
    public class OptionTransferSampleRoot
    {
        public OptionTransferSampleChild Child { get; set; } = new OptionTransferSampleChild();
        public string ApiKey { get; set; } = "secret";
        public string ReadOnly => "read-only";
    }

    public class OptionTransferSampleChild
    {
        public bool Enabled { get; set; }
        public int Count { get; set; }
        public OptionTransferSampleMode Mode { get; set; }
    }

    public enum OptionTransferSampleMode
    {
        A,
        B
    }
}

namespace StrmAssistant.Tests
{
    public class ConfigTransferTests
    {
        [Fact]
        public void FlattenIncludesWritableScalarsAndSkipsReadOnly()
        {
            var values = OptionTransfer.Flatten(new OptionTransferSampleRoot(), "Root");

            Assert.Equal("secret", values["Root.ApiKey"]);
            Assert.Equal("False", values["Root.Child.Enabled"]);
            Assert.Equal("0", values["Root.Child.Count"]);
            Assert.Equal("A", values["Root.Child.Mode"]);
            Assert.DoesNotContain("Root.ReadOnly", values.Keys);
        }

        [Fact]
        public void FlattenHonoursTopLevelWhitelist()
        {
            var values = OptionTransfer.Flatten(new OptionTransferSampleRoot(), "Root",
                new HashSet<string> { nameof(OptionTransferSampleRoot.Child) });

            Assert.Contains("Root.Child.Count", values.Keys);
            Assert.DoesNotContain("Root.ApiKey", values.Keys);
        }

        [Fact]
        public void SensitiveKeysAreDetected()
        {
            Assert.True(OptionTransfer.IsSensitive("MetadataEnhanceOptions.DoubanAssist.DoubanCookie"));
            Assert.True(OptionTransfer.IsSensitive("MetadataEnhanceOptions.DoubanAssist.AltMovieDbApiKey"));
            Assert.True(OptionTransfer.IsSensitive("NetworkProxyOptions.Network.ProxyServerUrl"));
            Assert.False(OptionTransfer.IsSensitive("MetadataEnhanceOptions.DoubanAssist.AltMovieDbApiUrl"));
        }

        [Fact]
        public void TryApplyParsesScalarsAndEnum()
        {
            var root = new OptionTransferSampleRoot();

            Assert.True(OptionTransfer.TryApply(root, "Child.Enabled", "true", out _));
            Assert.True(root.Child.Enabled);

            Assert.True(OptionTransfer.TryApply(root, "Child.Count", "42", out _));
            Assert.Equal(42, root.Child.Count);

            Assert.True(OptionTransfer.TryApply(root, "Child.Mode", "B", out _));
            Assert.Equal(OptionTransferSampleMode.B, root.Child.Mode);
        }

        [Fact]
        public void TryApplyRejectsUnknownAndReadOnlyPaths()
        {
            var root = new OptionTransferSampleRoot();

            Assert.False(OptionTransfer.TryApply(root, "Child.Missing", "1", out var missingError));
            Assert.NotNull(missingError);

            Assert.False(OptionTransfer.TryApply(root, "ReadOnly", "x", out _));
        }
    }
}
