# -*- coding: utf-8 -*-
import re, io
c = io.open(r'E:\kunkun\SonarSwitch\SonicRoute\L10n.cs', encoding='utf-8').read()

# 按语言区块切分：找到所有 ["xx-XX"] = new Dictionary 或 { 起始
# 先看结构：每个语言区块如何界定
# 简单法：找 BuildTables 里所有语言名出现位置，切分
lang_starts = [(m.start(), m.group(1)) for m in re.finditer(r'\["(zh-CN|zh-TW|en-US|ja-JP|ko-KR|fr-FR|de-DE|es-ES|ru-RU)"\]', c)]
print('语言标记位置数:', len(lang_starts))

# 找每语言区块范围：从语言标记到下一个语言标记或文件尾
new10 = ['Ov.NoSession','Ov.VolReadFail','Ov.MutedVol','Ov.NoOutputSession','Ov.VolAdjustFail','Ov.NoDevice','Ov.NoMicDevice','Ov.NoneToReset','Ov.AppMuted','Ov.AppUnmuted']
for i, (pos, lang) in enumerate(lang_starts):
    end = lang_starts[i+1][0] if i+1 < len(lang_starts) else len(c)
    seg = c[pos:end]
    keys = re.findall(r'\["([A-Za-z]+\.[A-Za-z0-9]+)"\]\s*=\s*"([^"]*)"', seg)
    seen = {}
    dups = []
    for k, v in keys:
        if k in seen:
            dups.append((k, seen[k], v))
        else:
            seen[k] = v
    new_in_old = [(k, v) for k, v in keys if k in new10]
    print(f'--- {lang}: 总键 {len(keys)}, 重复 {len(dups)}')
    for k, v1, v2 in dups:
        print(f'    重复键 {k}: 旧="{v1}" 新="{v2}"')
