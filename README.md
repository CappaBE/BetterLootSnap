# BetterLootSnap

In-game CUI editor for [BetterLoot](https://umod.org/plugins/better-loot) 4.5.x loot tables (Rust / Oxide).

Look at a loot container, run `/blsnap`, see everything it can spawn, scale slot and group probabilities, edit min/max amounts and rarity, and save safely with an automatic backup. Admin only.

> BetterLootSnap is an independent add-on. BetterLoot is made by MagicServices.co // TGWA and is not affiliated with this project.

---

## Features

- **Loot overview:** shows the full loot configuration of the container you are looking at (Vanilla Loot Slots, nested SubSpawn groups, Guaranteed Items and Ungrouped Items), not just what is currently inside it.
- **Probability multipliers:** scale the `Probability` of a loot slot or the `Weight` of a loot group relative to the original value. Example: `0.5` on a weapon slot makes it half as likely.
- **Live chance preview:** every row shows the original chance (BEFORE) and the resulting chance (NOW).
- **Item editor:** edit `Item Minimum`, `Item Maximum` and `Rarity` per loot entry.
- **Duplicate-safe:** the same item can occur in several slots or groups with different settings. Each entry is edited on its own and is never merged by shortname.
- **Safe saving:**
  - A backup of the original file is copied before anything is written.
  - Only the values you changed are written.
  - If the file changed since you opened the UI, nothing is saved.
- **Paged, scrollable UI** that copes with large loot tables (30 rows per page).

## Requirements

| Component | Version used during development |
|---|---|
| Oxide.Rust | 2.0.7894 |
| BetterLoot | 4.5.4 |
| Rust protocol | 2636.290.1 |

Other Oxide or Rust builds may need small API adjustments.

## Installation

1. Copy `BetterLootSnap.cs` to `oxide/plugins/`. The file name must stay `BetterLootSnap.cs`.
2. The plugin loads automatically. Check the server console for compile errors.
3. BetterLoot must be installed and must have generated `oxide/data/BetterLoot/LootTables.json`.

## Permissions

| Permission | Purpose |
|---|---|
| `betterlootsnap.admin` | Allows use of `/blsnap` |

Server admins (`player.IsAdmin`) have access automatically, without the permission.

```
oxide.grant user <name|steamid> betterlootsnap.admin
oxide.grant group <group> betterlootsnap.admin
```

## Usage

1. Stand within 5 metres of a loot container (supply drop, crate, barrel, ...) and look at it.
2. Run `/blsnap`.
3. Use the **LOOT** tab to adjust probabilities and the **ITEMS** tab to adjust amounts and rarity.
4. Press **SAVE** to write the changes, or **CANCEL** to discard them.
5. Reload BetterLoot if it does not pick up the change on its own: `oxide.reload BetterLoot`.

The container must have an entry in BetterLoot's `LootTables.json`. Ordinary storage boxes and other objects without a BetterLoot configuration are rejected.

### LOOT tab

| Column | Meaning |
|---|---|
| ENTRY | Loot slot, group or sub-group (indented per level) |
| CONTAINS | Items in that group, or the number of sub-groups |
| BEFORE | Original chance per slot roll |
| NOW | Resulting chance after your multipliers |
| PROB x | Multiplier on the original value |

- `1` = unchanged, `0.5` = half as likely, `0` = never.
- **Slot rows** scale `Probability`. This is an absolute chance, so `0.5` on a slot with probability `1.0` gives a 50% chance.
- **Group rows** scale `Weight`. This is relative to the other groups at the same level, so halving one group's weight lowers its share by less than half. Use the NOW column to see the actual result.
- Weights are whole numbers, so scaled weights are rounded (for example `5 x 0.5 = 3`).

### ITEMS tab

One row per item entry, with its source (for example `Slot 3 / GunRoll / GunRoll_Medium / ...`), minimum, maximum and a rarity button that cycles through `None`, `Common`, `Uncommon`, `Rare` and `VeryRare`.

## Backups

Before every save, the original file is copied to:

```
oxide/data/BetterLoot/LootTables_BetterLootSnap_backup.json
```

The backup is overwritten on each save, so copy it elsewhere if you want to keep several generations.

## Limitations

- **Loot Profiles** are not supported.
- The chance percentages are calculated from the structure of `LootTables.json` (slot probability times the weight share of each group down the tree). They do not take `Number To Spawn`, `Extra Spawns`, other BetterLoot settings or rarity bias into account. Verify the in-game behaviour on your own server.
- BetterLoot normally reads its data when it loads, so changes may only apply after a reload.
- Chat messages are currently in Dutch; UI buttons are in English.

## Internal console commands

These are used by the UI buttons and are not meant to be run manually: `blui.set`, `blui.mult`, `blui.rarity`, `blui.tab`, `blui.page`, `blui.save`, `blui.close`.

## License

This plugin is open source 
