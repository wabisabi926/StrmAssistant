using StrmAssistant.Common;
using System;
using Xunit;

namespace StrmAssistant.Tests
{
    public class MetadataCacheTtlTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ZeroTtlNeverExpires()
        {
            Assert.False(LocalMetadataCache.IsExpired(Now.AddYears(-10), Now, 0));
        }

        [Fact]
        public void OlderThanTtlExpires()
        {
            Assert.True(LocalMetadataCache.IsExpired(Now.AddDays(-31), Now, 30));
        }

        [Fact]
        public void NewerThanTtlDoesNotExpire()
        {
            Assert.False(LocalMetadataCache.IsExpired(Now.AddDays(-29), Now, 30));
        }

        [Fact]
        public void ExactBoundaryDoesNotExpire()
        {
            Assert.False(LocalMetadataCache.IsExpired(Now.AddDays(-30), Now, 30));
        }
    }
}
