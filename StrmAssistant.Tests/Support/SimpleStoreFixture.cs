using System;
using System.IO;
using Stj = System.Text.Json;
using Emby.Web.GenericEdit;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options.UIBaseClasses.Store;

namespace StrmAssistant.Tests.Support
{
    /// <summary>
    /// 为 <see cref="SimpleFileStore{T}"/> 提供隔离的运行环境：
    /// 临时配置目录 + 基于 System.Text.Json 的序列化器 + 真实文件系统的薄封装。
    /// 只用于单测，不接触真实 Emby 配置目录。
    /// </summary>
    internal sealed class SimpleStoreFixture : IDisposable
    {
        public string ConfigDir { get; }

        private readonly IApplicationHost _host;

        public SimpleStoreFixture()
        {
            ConfigDir = Path.Combine(Path.GetTempPath(), "sa-store-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ConfigDir);
            _host = CreateHost(ConfigDir);
        }

        public SimpleFileStore<T> Create<T>(string pluginName) where T : EditableOptionsBase, new()
            => new SimpleFileStore<T>(_host, CreateLogger(), pluginName);

        public string FilePath(string pluginName) => Path.Combine(ConfigDir, pluginName + ".json");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }

        private static IApplicationHost CreateHost(string configDir)
        {
            var paths = InterfaceProxy.Create<IApplicationPaths>((m, _) =>
                m.Name == "get_PluginConfigurationsPath" ? configDir : null);

            var fileSystem = InterfaceProxy.Create<IFileSystem>((m, a) =>
            {
                var path = a != null && a.Length > 0 ? a[0] as string : null;
                switch (m.Name)
                {
                    case "get_IsUnixFileSystem":
                    case "get_IsRunningOnLinux":
                        return (object)false;
                    case "DirectoryExists":
                        return Directory.Exists(path);
                    case "CreateDirectory":
                        Directory.CreateDirectory(path);
                        return null;
                    case "FileExists":
                        return File.Exists(path);
                    case "OpenRead":
                        return File.OpenRead(path);
                    case "GetFileStream":
                        return File.Open(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    default:
                        return null;
                }
            });

            var serializer = InterfaceProxy.Create<IJsonSerializer>((m, a) =>
            {
                switch (m.Name)
                {
                    case "SerializeToStream":
                    {
                        var stream = a.Length > 1 ? a[1] as Stream : null;
                        if (stream == null || a[0] == null) return null;

                        var embyOptions = a.Length > 2 ? a[2] as MediaBrowser.Model.Serialization.JsonSerializerOptions : null;
                        var stjOptions = new Stj.JsonSerializerOptions { WriteIndented = embyOptions?.Indent ?? false };
                        Stj.JsonSerializer.Serialize(stream, a[0], a[0].GetType(), stjOptions);
                        return null;
                    }
                    case "DeserializeFromStream":
                    {
                        // 单参泛型版本不经过这里；此处是 DeserializeFromStream(Stream, Type)
                        if (a.Length == 2 && a[0] is Stream readStream && a[1] is Type type)
                        {
                            using var reader = new StreamReader(readStream, leaveOpen: true);
                            var text = reader.ReadToEnd();
                            return string.IsNullOrWhiteSpace(text) ? null : Stj.JsonSerializer.Deserialize(text, type);
                        }

                        return null;
                    }
                    default:
                        return null;
                }
            });

            return InterfaceProxy.Create<IApplicationHost>((m, a) =>
            {
                if (m.Name != "Resolve" || !m.IsGenericMethod) return null;

                var requested = m.GetGenericArguments()[0];
                if (requested == typeof(IJsonSerializer)) return serializer;
                if (requested == typeof(IFileSystem)) return fileSystem;
                if (requested == typeof(IApplicationPaths)) return paths;

                return null;
            });
        }

        private static ILogger CreateLogger() =>
            InterfaceProxy.Create<ILogger>((_, __) => null);
    }
}
