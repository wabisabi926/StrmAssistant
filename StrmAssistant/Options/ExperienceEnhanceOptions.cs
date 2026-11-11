using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 体验增强设置根节点。
    /// 设置项按功能拆到 4 个分组类（多版本合并 / 多版本展示 / 播放与清理 / 通知），
    /// 外加既有的「界面功能」子组。本类只负责声明分组。
    /// 注意：分组的属性名与分组类名不同（避免实例成员遮蔽类型名导致初始化表达式无法解析）。
    /// </summary>
    public class ExperienceEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_EditorTitle_Experience_Enhance;

        /// <summary>多版本合并：同文件夹多版本自动合并、范围、分组上限与拆分操作。</summary>
        [DisplayNameL("ExperienceEnhanceOptions_Group_VersionMerge", typeof(Resources))]
        public VersionMergeOptions Merge { get; set; } = new VersionMergeOptions();

        /// <summary>多版本展示：详情页画质角标、标签来源与排序。</summary>
        [DisplayNameL("ExperienceEnhanceOptions_Group_VersionDisplay", typeof(Resources))]
        public VersionDisplayOptions Display { get; set; } = new VersionDisplayOptions();

        /// <summary>播放与清理：自动标记已看、深度删除、字幕路径优化。</summary>
        [DisplayNameL("ExperienceEnhanceOptions_Group_Playback", typeof(Resources))]
        public ExperiencePlaybackOptions Playback { get; set; } = new ExperiencePlaybackOptions();

        /// <summary>安全：演练模式，破坏性操作只记录不执行。</summary>
        [DisplayNameL("ExperienceEnhanceOptions_Group_Safety", typeof(Resources))]
        public SafetyOptions Safety { get; set; } = new SafetyOptions();

        /// <summary>通知：剧集通知显示集详情与触发字段。</summary>
        [DisplayNameL("ExperienceEnhanceOptions_Group_Notification", typeof(Resources))]
        public ExperienceNotificationOptions Notification { get; set; } = new ExperienceNotificationOptions();

        /// <summary>
        /// 显示优化：详情页人物列表、分集标题、缺集视图、剧集卡片上的显示效果。
        /// 顺序按「由内到外」：条目 → 媒体库 → 搜索与日志。
        /// </summary>
        [DisplayNameL("ItemDisplayOptions_EditorTitle_Item_Display", typeof(Resources))]
        public ItemDisplayOptions ItemDisplay { get; set; } = new ItemDisplayOptions();

        /// <summary>媒体库与排序：首页库卡片角标、合集库、条目自然排序、库顺序强制。</summary>
        [DisplayNameL("LibraryOrderOptions_EditorTitle_Library_and_Order", typeof(Resources))]
        public LibraryOrderOptions LibraryOrder { get; set; } = new LibraryOrderOptions();

        /// <summary>搜索与日志：搜索结果卡片信息、搜索页推荐区块、服务器日志列表顺序。</summary>
        [DisplayNameL("SearchLogOptions_EditorTitle_Search_and_Logs", typeof(Resources))]
        public SearchLogOptions SearchLog { get; set; } = new SearchLogOptions();

        /// <summary>Store 在保存时用于判断 patch 是否可用（与各组内的同名 helper 独立）。</summary>
        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public override IEditObjectContainer CreateEditContainer()
        {
            var container = base.CreateEditContainer();
            if (container is EditObjectContainer editContainer)
            {
                EditorGroupHelper.Rebuild(editContainer,
                    new EditorGroupSpec(Resources.ExperienceEnhanceOptions_Group_MultipleVersions,
                        "MultipleVersions", "Merge", "Display"),
                    new EditorGroupSpec(Resources.ExperienceEnhanceOptions_Group_Playback,
                        "Playback", "Playback", "Safety"),
                    new EditorGroupSpec(Resources.ItemDisplayOptions_EditorTitle_Item_Display,
                        "ItemDisplay", "ItemDisplay"),
                    new EditorGroupSpec(Resources.LibraryOrderOptions_EditorTitle_Library_and_Order,
                        "LibraryOrder", "LibraryOrder"),
                    new EditorGroupSpec(Resources.ExperienceEnhanceOptions_Group_NotificationAndLogs,
                        "NotificationsLogs", "Notification", "SearchLog"));
            }

            return container;
        }
    }
}
