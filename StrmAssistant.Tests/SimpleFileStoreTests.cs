using Emby.Web.GenericEdit;
using StrmAssistant.Tests.Support;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 回归：配置文件不存在时，GetOptions() 必须返回**同一个**实例。
    ///
    /// 旧实现里 ReloadOptions() 在「文件不存在」分支只 return 不赋值，
    /// 导致 this.options 始终为 null，每次调用都 new 一个新对象。
    /// 后果是「读-改-写」分两次取对象的代码会改在临时对象上，
    /// 落盘的仍是全默认值（Plugin.cs 里 NoBoxsetsAutoCreation 回写就是这种情况）。
    /// </summary>
    public class SimpleFileStoreTests
    {
        public class SampleOptions : EditableOptionsBase
        {
            public override string EditorTitle => "Sample";

            public string Value { get; set; }

            public bool Flag { get; set; }
        }

        [Fact]
        public void GetOptions_WithoutFile_ReturnsSameInstance()
        {
            using var fixture = new SimpleStoreFixture();
            var store = fixture.Create<SampleOptions>("TestPlugin");

            var first = store.GetOptions();
            var second = store.GetOptions();

            Assert.Same(first, second);
        }

        [Fact]
        public void ReloadOptions_WithoutFile_ReturnsSameInstanceAsGetOptions()
        {
            using var fixture = new SimpleStoreFixture();
            var store = fixture.Create<SampleOptions>("TestPlugin");

            var fromGet = store.GetOptions();
            var fromReload = store.ReloadOptions();

            Assert.Same(fromGet, fromReload);
        }

        [Fact]
        public void GetOptions_WithoutFile_DoesNotCreateFile()
        {
            using var fixture = new SimpleStoreFixture();
            var store = fixture.Create<SampleOptions>("TestPlugin");

            store.GetOptions();

            Assert.False(System.IO.File.Exists(fixture.FilePath("TestPlugin")));
        }

        /// <summary>
        /// 复现真实 bug：没有配置文件时，先改再存——改的那份必须被保存下来。
        /// 旧实现在此用例下会写出全默认值。
        /// </summary>
        [Fact]
        public void MutateThenSave_WithoutFile_PersistsMutation()
        {
            using var fixture = new SimpleStoreFixture();
            var store = fixture.Create<SampleOptions>("TestPlugin");

            store.GetOptions().Value = "changed";
            store.SetOptions(store.GetOptions());

            var reread = fixture.Create<SampleOptions>("TestPlugin").GetOptions();
            Assert.Equal("changed", reread.Value);
        }

        [Fact]
        public void SetOptions_ThenGetOptions_ReturnsSameInstance()
        {
            using var fixture = new SimpleStoreFixture();
            var store = fixture.Create<SampleOptions>("TestPlugin");

            var options = new SampleOptions { Value = "v1", Flag = true };
            store.SetOptions(options);

            Assert.Same(options, store.GetOptions());
            Assert.True(System.IO.File.Exists(fixture.FilePath("TestPlugin")));
        }

        [Fact]
        public void GetOptions_WhenFileExists_LoadsValuesAndCachesInstance()
        {
            using var fixture = new SimpleStoreFixture();

            fixture.Create<SampleOptions>("TestPlugin")
                .SetOptions(new SampleOptions { Value = "persisted", Flag = true });

            var store = fixture.Create<SampleOptions>("TestPlugin");
            var first = store.GetOptions();
            var second = store.GetOptions();

            Assert.Same(first, second);
            Assert.Equal("persisted", first.Value);
            Assert.True(first.Flag);
        }
    }
}
