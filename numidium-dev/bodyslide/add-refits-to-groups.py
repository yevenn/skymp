# Adds clothing refit projects to the Numidium BodySlide groups and reports
# output files claimed by more than one member: python groups.py <SliderSets> <group xml> [--write]
import glob, os, re, sys
BS = chr(92)
sets_dir, xml_path = sys.argv[1], sys.argv[2]
write = '--write' in sys.argv

outputs = {}   # set name -> normalized output path
source = {}    # set name -> osp file
for osp in glob.glob(os.path.join(sets_dir, '*.osp')):
    text = open(osp, encoding='utf-8', errors='replace').read()
    for m in re.finditer(r'<SliderSet name="([^"]*)".*?</SliderSet>', text, re.S):
        p = re.search(r'<OutputPath>([^<]*)</OutputPath>', m.group(0))
        f = re.search(r'<OutputFile[^>]*>([^<]*)</OutputFile>', m.group(0))
        if p and f:
            outputs[m.group(1)] = (p.group(1).strip() + BS + f.group(1).strip()).lower().replace('/', BS)
            source[m.group(1)] = os.path.basename(osp)

xml = open(xml_path, encoding='utf-8').read()
groups = {g: re.findall(r'<Member name="([^"]*)"/>', body)
          for g, body in re.findall(r'<Group name="([^"]*)">(.*?)</Group>', xml, re.S)}

def pick(pattern, sex_folder=None):
    names = [s for s, osp in source.items() if re.search(pattern, osp)]
    if sex_folder:
        names = [s for s in names if (BS + sex_folder + BS) in outputs[s]]
    return sorted(names)

additions = {
    'Numidium Female': pick(r'^Sentinel - 3BA\.osp$') + pick(r'^0MCE_', 'f'),
    'Numidium Male': pick(r'^HIMBO Sentinel - ') + pick(r'^0MCE_', 'm'),
}
# One project per output file. Sentinel - 3BA has sets writing to another
# set's mesh (Gilded Ebony into Ebony, Berserk and Berserk Light into each
# other); keep the one whose name matches the path. Mage Clothing Expansion
# ships normal and Modest versions; keep Modest, the server is SFW.
SKIP = {
    'Sentinel - Gilded Ebony 3BA', 'Sentinel -Gilded Ebony 1P 3BA',
    'Sentinel - Gilded Ebony Boots 3BA', 'Sentinel - Gilded Ebony Gloves 3BA',
    'Sentinel - Lunar Guard Gloves 3BA',  # its data folder is missing; the misspelled set has it
    'Sentinel - Orc Berserk 1P 3BA', 'Sentinel - Orc Berserk Light 3BA',
    'Sentinel - SoS Dawnstar Heavy 1P 3BA', 'Sentinel - SoS Officer 2 3BA',
    '0MCE_(01f)_Variant1', '0MCE_(1f)_MageOutfit', '0MCE_(1m)_MageOutfit',
    '0MCE_(2m)_Adventurer', '0MCE_2m_variant1', '0MCE_(4m)_OpenVest_Shirtless',
}
additions = {g: [n for n in names if n not in SKIP] for g, names in additions.items()}
for g, names in additions.items():
    new = [n for n in names if n not in groups[g]]
    print(f'{g}: {len(groups[g])} members, adding {len(new)}')
    groups[g] += new
    claimed = {}
    for n in groups[g]:
        if n in outputs: claimed.setdefault(outputs[n], []).append(n)
    clashes = {o: ns for o, ns in claimed.items() if len(ns) > 1}
    print(f'  output files claimed twice: {len(clashes)}')
    for o, ns in list(clashes.items())[:12]:
        print(f'    {o}')
        for n in ns: print(f'        {n}  ({source[n]})')
    missing = [n for n in groups[g] if n not in outputs]
    if missing: print(f'  members with no project found: {len(missing)}, e.g. {missing[:3]}')

if write:
    body = '<SliderGroups>\n' + ''.join(
        f'    <Group name="{g}">\n' + ''.join(f'        <Member name="{n}"/>\n' for n in members) + '    </Group>\n'
        for g, members in groups.items()) + '</SliderGroups>\n'
    open(xml_path, 'w', encoding='utf-8', newline='\n').write(body)
    print('written', xml_path)
