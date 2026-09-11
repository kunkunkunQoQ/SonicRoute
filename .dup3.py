# -*- coding: utf-8 -*-
import re, io
c = io.open(r'E:\kunkun\SonarSwitch\SonicRoute\L10n.cs', encoding='utf-8').read()
fn_re = re.compile(r'private static Dictionary<string, string> Build(\w+)\(\) => new\(\)')
starts = [(m.start(), m.group(1)) for m in fn_re.finditer(c)]
print('Build 方法数:', len(starts), [s[1] for s in starts])
for i, (pos, lang) in enumerate(starts):
    end = starts[i+1][0] if i+1 < len(starts) else len(c)
    seg = c[pos:end]
    keys = re.findall(r'\["([A-Za-z]+\.[A-Za-z0-9]+)"\]\s*=\s*"([^"]*)"', seg)
    seen, dups = {}, []
    for k, v in keys:
        if k in seen:
            dups.append((k, seen[k], v))
        else:
            seen[k] = v
    if dups:
        print(f'--- {lang}: 总键 {len(keys)}, 重复 {len(dups)}')
        for k, v1, v2 in dups:
            print(f'    {k}: 旧="{v1}" 新="{v2}"')
