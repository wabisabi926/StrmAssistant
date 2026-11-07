namespace StrmAssistant.Options.UIBaseClasses.Store
{
    using System;
    using System.IO;

    using Emby.Web.GenericEdit;

    using MediaBrowser.Common;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Serialization;

    public class SimpleFileStore<TOptionType> : SimpleContentStore<TOptionType>
        where TOptionType : EditableOptionsBase, new()
    {
        private readonly ILogger logger;
        private readonly string pluginFullName;
        private readonly object lockObj = new object();
        private readonly IJsonSerializer jsonSerializer;
        private readonly IFileSystem fileSystem;
        private readonly string pluginconfigPath;

        private TOptionType options;

        public SimpleFileStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
        {
            this.logger = logger;
            this.pluginFullName = pluginFullName;
            this.jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            this.fileSystem = applicationHost.Resolve<IFileSystem>();

            var applicationPaths = applicationHost.Resolve<IApplicationPaths>();
            this.pluginconfigPath = applicationPaths.PluginConfigurationsPath;

            if (!this.fileSystem.DirectoryExists(this.pluginconfigPath))
            {
                this.fileSystem.CreateDirectory(this.pluginconfigPath);
            }

            this.OptionsFileName = string.Format("{0}.json", pluginFullName);
        }

        public event EventHandler<FileSavingEventArgs> FileSaving;

        public event EventHandler<FileSavedEventArgs> FileSaved;

        public virtual string OptionsFileName { get; }

        public string OptionsFilePath => Path.Combine(this.pluginconfigPath, this.OptionsFileName);

        public override TOptionType GetOptions()
        {
            lock (this.lockObj)
            {
                if (this.options == null)
                {
                    return this.ReloadOptions();
                }

                return this.options;
            }
        }

        public TOptionType ReloadOptions()
        {
            lock (this.lockObj)
            {
                var tempOptions = this.options ?? new TOptionType();

                try
                {
                    if (!this.fileSystem.FileExists(this.OptionsFilePath))
                    {
                        // 首次安装或配置文件被删除时必须把默认实例缓存下来：
                        // 否则每次 GetOptions() 都返回一个新对象，凡是「读-改-写」分两次
                        // GetOptions() 的调用都会改在临时对象上，落盘的仍是全默认值
                        // （例：Plugin.cs 里 NoBoxsetsAutoCreation 的自动回写）。
                        this.options = tempOptions;
                        return this.options;
                    }

                    using (var stream = this.fileSystem.OpenRead(this.OptionsFilePath))
                    {
                        var deserialized = tempOptions.DeserializeFromJsonStream(stream, this.jsonSerializer);

                        this.options = deserialized as TOptionType;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("Error loading plugin options for {0} from {1}", ex, this.pluginFullName, this.OptionsFilePath);
                    this.options = tempOptions;
                }

                return this.options ?? new TOptionType();
            }
        }

        public override void SetOptions(TOptionType newOptions)
        {
            if (newOptions == null)
            {
                throw new ArgumentNullException(nameof(newOptions));
            }

            var savingArgs = new FileSavingEventArgs(newOptions);
            this.FileSaving?.Invoke(this, savingArgs);

            if (savingArgs.Cancel)
            {
                return;
            }

            lock (this.lockObj)
            {
                using (var stream = this.fileSystem.GetFileStream(this.OptionsFilePath, FileOpenMode.Create, FileAccessMode.Write))
                {
                    this.jsonSerializer.SerializeToStream(newOptions, stream, new JsonSerializerOptions { Indent = true });
                }
            }

            lock (this.lockObj)
            {
                this.options = newOptions;
            }

            var savedArgs = new FileSavedEventArgs(newOptions);
            this.FileSaved?.Invoke(this, savedArgs);
        }
    }
}
