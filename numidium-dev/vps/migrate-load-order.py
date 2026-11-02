#!/usr/bin/env python3
"""Rewrites the form ids saved in the world when the load order changes.

Saved inventories, equipment, appearance and some gamemode properties keep raw
form ids, whose top byte (or, for light plugins, the FE xxx part) is the
plugin's position in the load order. References themselves are saved by name
("1c229:Plugin.esp") and need nothing. When plugins are removed, or
master-flagged ones join (the game loads those before every regular plugin),
these ids have to follow their plugin to its new position.

  migrate-load-order.py <plugins dir> <old settings.json> <new settings.json> <changeForms dir> [--write]

Both settings files' loadOrder must be in the game's real order (master-flagged
plugins first). Plugin flags are read from the plugins dir, which must hold every
plugin of both orders. Without --write it only reports. Ids of a removed plugin
are dropped from inventories and reported; elsewhere they're reported and kept.
Saved references of a removed plugin ("<id>_<plugin>.json", which the server
can't load any more) move to <changeForms dir>-removed-plugins.

The order the saved ids follow is recorded in load-order.json next to the
changeForms dir; a world already in the new order, or in neither, is refused,
since migrating twice would move the ids again.
"""
import json
import os
import struct
import sys

LIGHT_FLAG = 0x200


def read_order(settings_path):
    return [os.path.basename(p) for p in json.load(open(settings_path, encoding='utf-8'))['loadOrder']]


def is_light(plugins_dir, name):
    with open(os.path.join(plugins_dir, name), 'rb') as f:
        header = f.read(12)
    return bool(struct.unpack_from('<I', header, 8)[0] & LIGHT_FLAG) or name.lower().endswith('.esl')


def slots(plugins_dir, order):
    """{plugin name (lower case): ('full', index) or ('light', index)}"""
    result, full, light = {}, 0, 0
    for name in order:
        if is_light(plugins_dir, name):
            result[name.lower()] = ('light', light)
            light += 1
        else:
            result[name.lower()] = ('full', full)
            full += 1
    return result


def mapper(old, new):
    by_slot = {slot: name for name, slot in old.items()}

    def remap(form_id):
        """The id in the new order, the same id when its plugin didn't move, or None
        when its plugin is gone. Ids of the server's own forms (0xFF...) stay."""
        top = form_id >> 24
        if top == 0xFF:
            return form_id
        if top == 0xFE:
            slot, rest = ('light', (form_id >> 12) & 0xFFF), form_id & 0xFFF
        else:
            slot, rest = ('full', top), form_id & 0xFFFFFF
        name = by_slot.get(slot)
        if name is None:
            return form_id  # not a plugin of the old order: leave it
        if name not in new:
            return None
        kind, index = new[name]
        return (0xFE000000 | (index << 12) | rest) if kind == 'light' else ((index << 24) | rest)

    return remap


def migrate(form, remap, problems, where):
    """Rewrites one saved form's ids in place; returns how many changed."""
    changed = 0

    def fix(value, field):
        nonlocal changed
        if not isinstance(value, int) or isinstance(value, bool) or value == 0:
            return value
        new_value = remap(value)
        if new_value is None:
            problems.append(f'{where}: {field} {value:#010x} belongs to a removed plugin')
            return value
        if new_value != value:
            changed += 1
        return new_value

    def fix_entries(inv, field):
        nonlocal changed
        if not isinstance(inv, dict) or not isinstance(inv.get('entries'), list):
            return
        kept = []
        for entry in inv['entries']:
            base = entry.get('baseId')
            new_id = remap(base) if isinstance(base, int) and base else base
            if new_id is None:
                problems.append(f'{where}: dropped {entry.get("count")} x {base:#010x} ({field}), its plugin is removed')
                continue
            if new_id != base:
                entry['baseId'] = new_id
                changed += 1
            kept.append(entry)
        inv['entries'] = kept

    fix_entries(form.get('inv'), 'inventory')
    equipment = form.get('equipmentDump')
    if isinstance(equipment, dict):
        fix_entries(equipment.get('inv'), 'equipment')
        for key in ('leftSpell', 'rightSpell', 'voiceSpell', 'instantSpell'):
            if key in equipment:
                equipment[key] = fix(equipment[key], key)
    appearance = form.get('appearanceDump')
    if isinstance(appearance, dict):
        for key in ('raceId', 'headTextureSetId'):
            if key in appearance:
                appearance[key] = fix(appearance[key], key)
        if isinstance(appearance.get('headpartIds'), list):
            appearance['headpartIds'] = [fix(v, 'headpart') for v in appearance['headpartIds']]
    if isinstance(form.get('learnedSpells'), list):
        form['learnedSpells'] = [fix(v, 'spell') for v in form['learnedSpells']]
    dynamic = form.get('dynamicFields')
    if isinstance(dynamic, dict):
        perks = dynamic.get('rpPerks')
        if isinstance(perks, dict) and isinstance(perks.get('perks'), list):
            for pair in perks['perks']:
                if isinstance(pair, list) and pair:
                    pair[0] = fix(pair[0], 'perk')
        ui = dynamic.get('rpUi')
        if isinstance(ui, dict) and isinstance(ui.get('ownedLocks'), list):
            ui['ownedLocks'] = [fix(v, 'owned lock') for v in ui['ownedLocks']]
        menu = dynamic.get('rpMenu')
        if isinstance(menu, dict) and 'target' in menu:
            menu['target'] = fix(menu['target'], 'menu target')
    return changed


def main():
    args = [a for a in sys.argv[1:] if a != '--write']
    write = '--write' in sys.argv
    if len(args) != 4:
        sys.exit(__doc__)
    plugins_dir, old_settings, new_settings, forms_dir = args
    old_order, new_order = read_order(old_settings), read_order(new_settings)
    marker = os.path.join(os.path.dirname(os.path.normpath(forms_dir)), 'load-order.json')
    if os.path.exists(marker):
        current = json.load(open(marker, encoding='utf-8'))
        if current == new_order:
            sys.exit(f'{marker}: the world is already in the new order; nothing to do.')
        if current != old_order:
            sys.exit(f'{marker}: the world is in another order than the old settings give; not migrating.')
    old, new = slots(plugins_dir, old_order), slots(plugins_dir, new_order)
    moved = sorted(name for name in old if name in new and old[name] != new[name])
    removed = sorted(name for name in old if name not in new)
    print(f'{len(moved)} plugins move, {len(removed)} removed: {", ".join(removed) or "none"}')
    remap = mapper(old, new)
    problems, total, files = [], 0, 0
    aside = os.path.normpath(forms_dir) + '-removed-plugins'
    for file_name in sorted(os.listdir(forms_dir)):
        if not file_name.endswith('.json'):
            continue
        path = os.path.join(forms_dir, file_name)
        plugin = file_name[:-len('.json')].split('_', 1)[-1].lower()
        if '_' in file_name and plugin in removed:
            print(f'  {file_name}: a reference of a removed plugin, {"moved aside" if write else "would move aside"}')
            if write:
                os.makedirs(aside, exist_ok=True)
                os.replace(path, os.path.join(aside, file_name))
            continue
        form = json.load(open(path, encoding='utf-8'))
        changed = migrate(form, remap, problems, file_name)
        if changed:
            total += changed
            files += 1
            print(f'  {file_name}: {changed} ids')
            if write:
                temp = path + '.migrating'
                with open(temp, 'w', encoding='utf-8') as f:
                    json.dump(form, f, indent=2)
                os.replace(temp, path)
    for line in problems:
        print('  !', line)
    if write:
        with open(marker, 'w', encoding='utf-8') as f:
            json.dump(new_order, f, indent=1)
    print(f'{total} ids in {files} files {"rewritten" if write else "would be rewritten (dry run)"}')


if __name__ == '__main__':
    main()
