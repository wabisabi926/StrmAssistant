using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 播放进度按库分离的 key 前缀规则。
    ///
    /// 关键约束：Episode/Season 的 UserDataKey 由 series key 拼接而来，
    /// 同一次计算里会被调用多次，因此「幂等」必须靠内容判定而不是计数器。
    /// </summary>
    public class UserDataScopeTests
    {
        [Fact]
        public void Apply_PrependsLibraryPrefix()
        {
            Assert.Equal("42|tmdb-12345", UserDataScope.Apply("tmdb-12345", 42));
        }

        [Fact]
        public void Apply_IsIdempotent()
        {
            var once = UserDataScope.Apply("tmdb-12345", 42);
            var twice = UserDataScope.Apply(once, 42);

            Assert.Equal(once, twice);
            Assert.Equal("42|tmdb-12345", twice);
        }

        [Fact]
        public void Apply_DifferentLibraryGetsDifferentKey()
        {
            var a = UserDataScope.Apply("tmdb-12345", 42);
            var b = UserDataScope.Apply("tmdb-12345", 43);

            Assert.NotEqual(a, b);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Apply_KeepsEmptyKey(string key)
        {
            Assert.Equal(key, UserDataScope.Apply(key, 42));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0L)]
        [InlineData(-1L)]
        public void Apply_KeepsKeyWhenLibraryUnknown(long? libraryId)
        {
            Assert.Equal("tmdb-12345", UserDataScope.Apply("tmdb-12345", libraryId));
        }

        [Fact]
        public void Apply_DoesNotTreatInnerSeparatorAsPrefix()
        {
            // key 里出现 '|' 但不在开头时，仍应加前缀
            Assert.Equal("42|a|b", UserDataScope.Apply("a|b", 42));
        }

        [Fact]
        public void Apply_DoesNotMisjudgeOtherLibraryPrefix()
        {
            // 已带别的库前缀：不误判为「已处理」，但也不会破坏原值
            Assert.Equal("43|42|key", UserDataScope.Apply("42|key", 43));
        }
    }
}
