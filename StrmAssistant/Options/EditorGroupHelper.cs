using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Options
{
    internal sealed class EditorGroupSpec
    {
        public EditorGroupSpec(string title, string name, params string[] sources)
        {
            Title = title;
            Name = name;
            Sources = sources;
        }

        public string Title { get; }
        public string Name { get; }
        public string[] Sources { get; }
    }

    /// <summary>
    /// 仅重排/合并 UI 分组，不移动选项的存储路径。
    /// 源组中的 EditorBase 原样放进新组，字段 ID 与保存路径保持不变。
    /// </summary>
    internal static class EditorGroupHelper
    {
        public static void Rebuild(EditObjectContainer container, params EditorGroupSpec[] specs)
        {
            if (container?.EditorRoot?.EditorItems == null) return;

            var root = container.EditorRoot;
            var all = root.EditorItems.ToList();
            var groups = all.OfType<EditorGroup>().ToList();
            var nonGroups = all.Where(item => !(item is EditorGroup)).ToList();

            var result = new List<EditorBase>();
            var used = new HashSet<EditorGroup>();

            foreach (var spec in specs)
            {
                var matched = new List<EditorGroup>();
                foreach (var source in spec.Sources)
                {
                    matched.AddRange(groups.Where(group =>
                        !used.Contains(group) && !matched.Contains(group) && NameOrId(group, source)));
                }

                if (matched.Count == 0) continue;

                foreach (var group in matched) used.Add(group);

                var items = matched
                    .SelectMany(group => group.EditorItems ?? Array.Empty<EditorBase>())
                    .ToList();

                // 源组末尾的 SpacerItem 在合并后会变成新组前面的空白，清掉。
                while (items.Count > 0 && items[items.Count - 1] is EditorSpacerItem)
                {
                    items.RemoveAt(items.Count - 1);
                }

                result.Add(new EditorGroup(spec.Title, items.ToArray(), spec.Name, root.Id, null));
            }

            // 没被 spec 覆盖的组保持原顺序，避免功能项静默消失。
            result.AddRange(groups.Where(group => !used.Contains(group)));
            result.AddRange(nonGroups);

            root.EditorItems = result.ToArray();
        }

        private static bool NameOrId(EditorBase editor, string name)
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
