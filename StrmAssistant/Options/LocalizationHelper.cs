using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Options
{
    /// <summary>
    /// Emby 的 DescriptionL 会把第一次取值时的语言缓存到属性描述符里，管理员切换界面语言后
    /// 字段说明不会跟着变。这里在建好页面后，按字段的资源键和当前客户端语言重新取一次说明。
    /// </summary>
    internal static class LocalizationHelper
    {
        public static void PrepareForView()
        {
            Resources.Culture = null;
        }

        public static void ApplyDescriptions(EditObjectContainer container, object root)
        {
            if (container?.EditorRoot?.EditorItems == null || root == null) return;

            foreach (var editor in Walk(container.EditorRoot.EditorItems))
            {
                if (string.IsNullOrEmpty(editor.Id)) continue;

                var description = ResolveDescription(root, editor.Id);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    editor.Description = description;
                }
            }
        }

        private static IEnumerable<EditorBase> Walk(IEnumerable<EditorBase> items)
        {
            foreach (var item in items)
            {
                yield return item;

                if (item is EditorGroup group && group.EditorItems != null)
                {
                    foreach (var child in Walk(group.EditorItems))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static string ResolveDescription(object root, string editorId)
        {
            var parts = editorId.Split('.');
            object owner = root;

            for (var i = 0; i < parts.Length - 1; i++)
            {
                owner = owner?.GetType()
                    .GetProperty(parts[i], BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(owner);

                if (owner == null) return null;
            }

            var property = owner.GetType()
                .GetProperty(parts[parts.Length - 1], BindingFlags.Instance | BindingFlags.Public);
            var attribute = property?.GetCustomAttribute<DescriptionAttribute>();
            if (attribute == null) return null;

            var localizableField = attribute.GetType().BaseType?.GetField(
                "localizableString", BindingFlags.Instance | BindingFlags.NonPublic);
            var localizable = localizableField?.GetValue(attribute);
            var key = localizable?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(localizable) as string;

            return string.IsNullOrWhiteSpace(key)
                ? null
                : Resources.ResourceManager.GetString(key, CultureInfo.CurrentUICulture);
        }
    }
}
