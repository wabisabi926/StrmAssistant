using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Mod;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace StrmAssistant.Options
{
    public class PluginOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_EditorTitle_Basic_Settings", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Basic_Settings;

        public override string EditorDescription => string.Empty;

        // 注意：这里刻意不设 Icon。标题是一整句话（36 字），带图标时在手机窄屏换行后
        // 图标会被容器裁掉一半；纯文字按钮会正常换行，桌面端也更清爽。
        public ButtonItem DisclaimerButton { get; set; } = new ButtonItem
        {
            Data1 = "DisclaimerDialog"
        };

        /// <summary>品牌信息：插件名组头 + 版本/支持版本行，位于免责声明之下、总开关之上。</summary>
        [DisplayNameL("PluginOptions_EditorTitle_Strm_Assistant", typeof(Resources))]
        public BrandOptions BrandOptions { get; set; } = new BrandOptions();

        /// <summary>
        /// 运行概览（一行状态条）：内存 / 任务 / 队列 / 索引。
        /// 用 <see cref="StatusItem"/> 而不是独立分组——只读状态不该把「总开关 → 追更模式」这条配置流截断，
        /// 放在品牌之后、总开关之前，进页面第一眼就能看到，往下全是设置。
        /// </summary>
        public StatusItem OverviewStatus { get; set; } = new StatusItem();

        /// <summary>
        /// 插件总开关。关闭后 <c>Plugin.Initialize</c> 会整段跳过（不 patch、不订阅事件、
        /// 不初始化队列），定时任务与 Web 端点也会早退；修改后需重启服务器才生效。
        /// 之所以不做即时生效，是因为要撤销 36 个 Harmony patch 并逐个验证幂等性，
        /// 一旦有遗漏会出现「关了其实没关」——比没有开关更糟。
        /// </summary>
        [DisplayNameL("PluginOptions_EnablePlugin_Enable_Plugin", typeof(Resources))]
        [DescriptionL("PluginOptions_EnablePlugin_Description", typeof(Resources))]
        [Required]
        public bool EnablePlugin { get; set; } = true;

        [VisibleCondition(nameof(ShowConflictPluginLoadedStatus), SimpleCondition.IsTrue)]
        public StatusItem ConflictPluginLoadedStatus { get; set; } = new StatusItem();

        [VisibleCondition(nameof(IsModSuccess), SimpleCondition.IsFalse)]
        public StatusItem ModStatus { get; set; } = new StatusItem();


        /// <summary>中文搜索：功能设置，放在并发/性能参数之前。</summary>
        [DisplayNameL("ModOptions_Group_ChineseSearch", typeof(Resources))]
        public ChineseSearchOptions ChineseSearch { get; set; } = new ChineseSearchOptions();

        // ---- 分组直接挂在页面根上（不再套 GeneralOptions / ModOptions 两层容器）----
        // 之前的结构是「任务与内存 → 任务并发 → 6 项」，三层嵌套只为一个标题；
        // 拍平后变成「任务并发 → 6 项」，页面层级从 3 层降到 2 层。

        [DisplayNameL("GeneralOptions_Group_TaskConcurrency", typeof(Resources))]
        public TaskConcurrencyOptions TaskConcurrency { get; set; } = new TaskConcurrencyOptions();

        [DisplayNameL("GeneralOptions_Group_Memory", typeof(Resources))]
        public MemoryOptions Memory { get; set; } = new MemoryOptions();

        /// <summary>
        /// 常用任务：插件自己的任务快捷入口，每行可直接执行（含上次耗时/状态）。
        /// 放在日常设置之后、维护与备份之前。
        /// </summary>
        [DisplayNameL("ScheduledTasks_EditorTitle_Scheduled_Tasks", typeof(Resources))]
        public ScheduledTasksOptions ScheduledTasks { get; set; } = new ScheduledTasksOptions();

        /// <summary>
        /// 旧版容器，仅用于把已存在的配置读进来后拍平（见 <c>Plugin.Initialize</c> 的 FlattenOptions）。
        /// **没有初始化器**：JSON 里没有该节点时保持 null，据此判断要不要迁移。
        /// </summary>
        [Browsable(false)]
        public GeneralOptions GeneralOptions { get; set; }

        /// <inheritdoc cref="GeneralOptions"/>
        [Browsable(false)]
        public ModOptions ModOptions { get; set; }



        /// <summary>
        /// 运行状态与维护（清内存/重建索引/优化索引/重启）。
        /// 放在常用任务之后、备份与恢复之前；重启服务器等危险操作保持在组内末尾。
        /// 该组没有用户配置（列表是运行时生成的），所以搬家不需要配置迁移。
        /// </summary>
        [DisplayNameL("GeneralOptions_Group_RuntimeStatus", typeof(Resources))]
        public RuntimeStatusOptions RuntimeStatus { get; set; } = new RuntimeStatusOptions();

        /// <summary>备份与恢复：配置导出/导入，放在维护工具之后、关于之前。</summary>
        [DisplayNameL("GeneralOptions_Group_ConfigTransfer", typeof(Resources))]
        public ConfigTransferOptions ConfigTransfer { get; set; } = new ConfigTransferOptions();

        /// <summary>插件更新：检查 GitHub Release，可选自动下载；放在关于之前。</summary>
        [DisplayNameL("PluginUpdateOptions_EditorTitle_Plugin_Update", typeof(Resources))]
        public PluginUpdateOptions PluginUpdate { get; set; } = new PluginUpdateOptions();

        [DisplayNameL("AboutOptions_EditorTitle_About", typeof(Resources))]
        public AboutOptions AboutOptions { get; set; } = new AboutOptions();

        [Browsable(false)]
        public bool IsModSuccess => PatchManager.IsModSuccess();

        [Browsable(false)]
        public bool ShowConflictPluginLoadedStatus =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .Any(n => n == "StrmExtract" || n == "InfuseSync");

        /// <summary>
        /// 拍平后子组校验需要手动委派：Emby 不会自动调用嵌套选项的 Validate。
        /// </summary>
        protected override void Validate(ValidationContext context)
        {
            ValidateChildGroup(context, nameof(TaskConcurrency));
        }

        public override IEditObjectContainer CreateEditContainer()
        {
            LocalizationHelper.PrepareForView();
            var container = base.CreateEditContainer();
            if (!(container is EditObjectContainer editContainer) ||
                editContainer.EditorRoot?.EditorItems == null) return container;

            var root = editContainer.EditorRoot;
            var rootItems = root.EditorItems.ToList();
            var taskGroup = rootItems.OfType<EditorGroup>()
                .FirstOrDefault(g => IsEditor(g, nameof(TaskConcurrency)));
            if (taskGroup?.EditorItems == null) return container;

            var taskItems = taskGroup.EditorItems.ToList();
            var catchupNames = new[]
            {
                nameof(TaskConcurrencyOptions.CatchupMode),
                nameof(TaskConcurrencyOptions.CatchupMinWaitSeconds),
                nameof(TaskConcurrencyOptions.CatchupTaskScope)
            };
            var catchupItems = taskItems
                .Where(i => catchupNames.Any(n => IsEditor(i, n)))
                .ToList();
            if (catchupItems.Count == 0) return container;

            var remaining = taskItems.Except(catchupItems).ToList();
            var concurrencyGroup = new EditorGroup(Resources.GeneralOptions_Group_TaskConcurrency,
                remaining.ToArray(), taskGroup.Name, taskGroup.ParentId, null);

            var newItems = new List<EditorBase>();
            foreach (var item in rootItems)
            {
                if (ReferenceEquals(item, taskGroup))
                {
                    newItems.Add(concurrencyGroup);
                    continue;
                }

                newItems.Add(item);
                if (IsEditor(item, nameof(EnablePlugin)))
                {
                    newItems.AddRange(catchupItems);
                }
            }

            root.EditorItems = newItems.ToArray();
            LocalizationHelper.ApplyDescriptions(editContainer, this);
            return container;
        }

        private static bool IsEditor(EditorBase editor, string name)
        {
            if (editor == null) return false;
            if (string.Equals(editor.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            var id = editor.Id;
            return !string.IsNullOrEmpty(id) &&
                   (string.Equals(id, name, StringComparison.OrdinalIgnoreCase) ||
                    id.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        }

        public void Initialize()
        {
            DisclaimerButton.Caption = Resources.DisclaimerButtonText;

            // 旧配置里存过 Icon（privacy_tip），反序列化会把它带回来，必须在运行期清掉，
            // 否则手机端窄屏换行后图标仍会被裁切。这里显式置空以兼容老配置文件。
            DisclaimerButton.Icon = null;

            if (ShowConflictPluginLoadedStatus)
            {
                ConflictPluginLoadedStatus.Caption = Resources
                    .PluginOptions_IncompatibleMessage_Please_uninstall_the_conflict_plugin_Strm_Extract;
                ConflictPluginLoadedStatus.StatusText = string.Empty;
                ConflictPluginLoadedStatus.Status = ItemStatus.Warning;
            }
            else
            {
                ConflictPluginLoadedStatus.Caption = string.Empty;
                ConflictPluginLoadedStatus.StatusText = string.Empty;
                ConflictPluginLoadedStatus.Status = ItemStatus.None;
            }

            if (IsModSuccess is false)
            {
                ModStatus.Caption = Resources.PluginOptions_IsHarmonyModFailed_Harmony_Mod_Failed;
                ModStatus.StatusText = string.Empty;
                ModStatus.Status = ItemStatus.Warning;
            }
            else
            {
                ModStatus.Caption = string.Empty;
                ModStatus.StatusText = string.Empty;
                ModStatus.Status = ItemStatus.None;
            }
        }
    }
}
