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
        /// 条目显示：详情页人物列表、分集标题、缺集视图、剧集卡片上的显示效果。
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
            if (!(container is EditObjectContainer editContainer) ||
                editContainer.EditorRoot?.EditorItems == null) return container;

            var root = editContainer.EditorRoot;
            var items = root.EditorItems.ToList();
            var merge = items.OfType<EditorGroup>().FirstOrDefault(g => IsEditor(g, nameof(Merge)));
            var display = items.OfType<EditorGroup>().FirstOrDefault(g => IsEditor(g, nameof(Display)));
            if (merge == null && display == null) return container;

            var mergedItems = new List<EditorBase>();
            if (merge?.EditorItems != null) mergedItems.AddRange(merge.EditorItems);
            if (display?.EditorItems != null) mergedItems.AddRange(display.EditorItems);

            var multipleVersions = new EditorGroup(Resources.ExperienceEnhanceOptions_Group_MultipleVersions,
                mergedItems.ToArray(), "MultipleVersions", root.Id, null);
            var anchor = merge ?? display;

            var newItems = new List<EditorBase>();
            foreach (var item in items)
            {
                if (ReferenceEquals(item, anchor))
                {
                    newItems.Add(multipleVersions);
                    continue;
                }

                if (ReferenceEquals(item, merge) || ReferenceEquals(item, display)) continue;

                newItems.Add(item);
            }

            root.EditorItems = newItems.ToArray();
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
    }
}
