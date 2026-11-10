#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
资源键一致性校验（构建前检查）。

背景：Emby 的 [DisplayNameL] / [DescriptionL] 是通过**反射查找 Resources 的公共静态属性**解析的，
不是走 ResourceManager。而 Resources.Designer.cs 由设计期生成器产出，`dotnet build` 不会重新生成，
因此「往 .resx 加键但忘了同步 Designer」会让设置页直接 500（2026-10-16 踩过一次）。

本脚本校验四者的交集：
  1. 代码里引用的键（DisplayNameL / DescriptionL / ResourceManager.GetString）
  2. Resources.Designer.cs 的属性
  3. Resources.resx
  4. Resources.zh.resx / Resources.zh-hant.resx

用法：
  python tools/check_resx_keys.py            # 有差异时返回码 1
  python tools/check_resx_keys.py --quiet    # 仅打印问题
  python tools/check_resx_keys.py --fix      # 自动把缺失的属性补进 Designer（构建时默认启用）
  python tools/check_resx_keys.py --unused   # 额外报告未被代码引用的资源键（仅告警，不影响返回码）

`--fix` 只做一件事：把「代码引用、resx 有、但 Designer 缺」的属性按 VS 生成器格式追加进去。
它不会新增 resx 键、也不会编造译文 —— 那两类问题仍然报错。
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(ROOT, 'StrmAssistant')
PROPS = os.path.join(PROJ, 'Properties')

RESX_FILES = {
    'base': os.path.join(PROPS, 'Resources.resx'),
    'zh': os.path.join(PROPS, 'Resources.zh.resx'),
    'zh-hant': os.path.join(PROPS, 'Resources.zh-hant.resx'),
}
DESIGNER = os.path.join(PROPS, 'Resources.Designer.cs')

KEY_ATTR = re.compile(r'(?:DisplayNameL|DescriptionL|NameL)\(\s*"([^"]+)"')
# 允许 Resources / 点号 / 方法名之间换行，避免把跨行引用误判成未使用
KEY_GET = re.compile(r'Resources?\s*\.\s*ResourceManager\s*\.\s*GetString\(\s*"([^"]+)"')
# 直接属性访问 Resources.Xxx：编译期就会报 CS0117，但 --fix 也该能把它补上
KEY_PROP = re.compile(r'\bResources\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)')
# Resources 类里除资源键之外的成员（生成器自带），不能当成资源键
NON_RESOURCE_MEMBERS = {'Culture', 'ResourceManager'}
DESIGNER_PROP = re.compile(r'public\s+static\s+string\s+(\w+)\s*\{')

# Visual Studio 设计器写入的占位键（Bitmap1 / Color1 / Icon1 / Name1），不是真实资源
IGNORED_KEYS = {'Bitmap1', 'Color1', 'Icon1', 'Name1', 'Bitmap', 'Color', 'Icon', 'Name'}


def read(path):
    return io.open(path, encoding='utf-8-sig').read()


def code_keys():
    keys = set()
    for dirpath, _, filenames in os.walk(PROJ):
        if 'obj' in dirpath or 'bin' in dirpath:
            continue
        for name in filenames:
            if not name.endswith('.cs') or name == 'Resources.Designer.cs':
                continue
            try:
                src = read(os.path.join(dirpath, name))
            except Exception:
                continue
            keys.update(KEY_ATTR.findall(src))
            keys.update(KEY_GET.findall(src))
            keys.update(k for k in KEY_PROP.findall(src) if k not in NON_RESOURCE_MEMBERS)
    return keys


def resx_keys(path):
    keys = set(re.findall(r'<data\s+name="([^"]+)"', read(path)))
    return keys - IGNORED_KEYS


def designer_props():
    if not os.path.exists(DESIGNER):
        return set()
    return set(DESIGNER_PROP.findall(read(DESIGNER)))


PROP_BLOCK = '''
        /// <summary>
        ///   Looks up a localized string.
        /// </summary>
        public static string {name} {{
            get {{
                return ResourceManager.GetString("{name}", resourceCulture);
            }}
        }}
'''


def append_missing_props(missing):
    """把缺失的属性按 VS 生成器格式追加到 Designer 类的末尾（类闭合前）。

    只追加属性，不动 ResourceManager / Culture 等既有成员；幂等（已存在则跳过）。
    返回实际追加的键列表。
    """
    if not missing or not os.path.exists(DESIGNER):
        return []

    text = io.open(DESIGNER, encoding='utf-8-sig').read()

    # 定位类闭合：文件以 "        }\n    }\n}\n" 结束，插入点在内层 "    }" 之前
    marker = '\n    }\n}'
    index = text.rfind(marker)
    if index < 0:
        return []

    block = ''.join(PROP_BLOCK.format(name=name) for name in missing)
    patched = text[:index] + block + text[index:]

    # 保留 BOM：VS 生成的 Designer 带 BOM，去掉会让人以为文件被重写过
    with io.open(DESIGNER, 'w', encoding='utf-8-sig', newline='') as handle:
        handle.write(patched)

    return list(missing)


def main():
    quiet = '--quiet' in sys.argv
    fix = '--fix' in sys.argv
    used = code_keys()
    base = resx_keys(RESX_FILES['base'])
    zh = resx_keys(RESX_FILES['zh'])
    hant = resx_keys(RESX_FILES['zh-hant'])
    props = designer_props()

    # 2) 代码引用但 Designer 缺属性（DisplayNameL/DescriptionL 会解析失败 → 设置页 500）
    #    构建时默认 --fix：自动补齐，彻底消除「忘了同步 Designer」这一类 500。
    missing_props = sorted(used - props)
    if missing_props and fix:
        fixed = append_missing_props(missing_props)
        if fixed:
            print('[FIX] auto-added %d missing property(ies) to Resources.Designer.cs: %s'
                  % (len(fixed), ', '.join(fixed)))
            props = designer_props()
            missing_props = sorted(used - props)

    problems = []
    unused = sorted(base - used)

    # 4) resx 有、代码未引用：多为已删除功能的残留，仅告警不阻断
    if unused and not quiet:
        if '--unused' in sys.argv:
            print('\n[WARN] %d resource key(s) not referenced by code:' % len(unused))
            for key in unused:
                print('  ' + key)
        else:
            print('\n[WARN] %d resource key(s) not referenced by code (run with --unused to list).'
                  % len(unused))

    # 1) 代码引用但 resx 缺失（运行时会回退成键名，页面上直接看到乱码键）
    for tag, keys in (('base', base), ('zh', zh), ('zh-hant', hant)):
        missing = sorted(used - keys)
        if missing:
            problems.append('代码引用但 %s.resx 缺失 %d 个键:\n    %s'
                            % (tag, len(missing), '\n    '.join(missing)))

    if missing_props:
        problems.append('代码引用但 Resources.Designer.cs 缺属性 %d 个（用 --fix 可自动补齐）:\n    %s'
                        % (len(missing_props), '\n    '.join(missing_props)))

    # 3) resx 之间有键差异（缺翻译会回退英文，仅提示）
    for tag, keys in (('zh', zh), ('zh-hant', hant)):
        diff = sorted(base - keys)
        if diff:
            problems.append('%s.resx 相对 base 缺 %d 个键（会回退英文）:\n    %s'
                            % (tag, len(diff), '\n    '.join(diff)))

    if not quiet:
        print('code keys: %d | base: %d | zh: %d | zh-hant: %d | designer props: %d'
              % (len(used), len(base), len(zh), len(hant), len(props)))

    if problems:
        print('\n[FAIL] %d problem group(s) found:\n' % len(problems))
        for p in problems:
            print(' - ' + p + '\n')
        return 1

    print('[OK] resx keys consistent')
    return 0


if __name__ == '__main__':
    sys.exit(main())
