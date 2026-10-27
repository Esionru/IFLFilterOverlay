# IFL FilterOverlay

An ExileApi plugin for Path of Exile that draws a **colored frame around items** in your **inventory**, **stash** and the **NPC vendor window** when they match rules you write.

Rules are written in the **ItemFilterLibrary (IFL)** expression syntax, the same one used by `InvWithLinq` / `NPCInvWithLinq`, so existing expressions can be pasted in as-is. On top of that the plugin adds:

- rules stored in plain **`.txt` files**, split into **multiple categories** (one file = one category)
- a **live tester**: hover any item and see right away whether the rule you are typing matches it
- **click-to-build hints**: click a stat of the hovered item, or search all `GameStat` names and base items, and the condition is inserted for you
- a **label next to the tooltip** that tells you which rule highlighted the item you are hovering

---

## Features

| | |
|---|---|
| Where it draws | Player inventory, stash, NPC vendor window (each can be switched off) |
| Rule language | ItemFilterLibrary expressions (`HasTag`, `ItemStats[GameStat.X]`, `BaseName`, `PlayerInfo.Level`, `.Count(x => x)`, ...) |
| Storage | Plain text files, one file per category, in the plugin config folder |
| Categories | Any number of `.txt` files; enable / disable each one with a checkbox |
| Colors | Every rule has its own color (and alpha) |
| Priority | Categories top to bottom (alphabetical), rules top to bottom, **first match wins** |
| Editing | In the in-game settings window **or** in any text editor (changes are picked up automatically) |
| Testing | Hover an item, the selected rule is evaluated against it live (green frame + `Matches` / `Does not match`) |
| Debugging | Tooltip label `Rule: <category>: <rule name>` on every highlighted item |

---

## Installation

1. Copy the plugin folder to `ExileApi\Plugins\Source\IFLFilterOverlay\` (files: `IFLFilterOverlay.cs`, `IFLFilterOverlay.csproj`).
2. Make sure `ItemFilterLibrary.dll` exists next to `ExileCore.dll` (it is the engine used by `InvWithLinq` / `NPCInvWithLinq`). If it lives elsewhere, adjust the `HintPath` in the `.csproj`.
3. Start ExileApi. The plugin is compiled on launch and shows up as **IFLFilterOverlay** in the plugin list.
4. Tick **Enable**, open the plugin settings and create your first rule.

The project targets `net8.0-windows` and references `ExileCore`, `GameOffsets`, `ImGui.NET`, `SharpDX.Mathematics`, `Newtonsoft.Json` and `ItemFilterLibrary` from `$(exapiPackage)`.

---

## Quick start

1. Open the settings and press **+ Rule**.
2. Hover an item in your inventory. Its stats appear at the bottom of the settings window.
3. Click the stats you care about; each click appends `ItemStats[GameStat.X] >= value` to the rule. Use the **Join with: && / ||** button to choose how conditions are combined.
4. Watch the status line: `Matches: <item>` (green) or `Does not match: <item>` (red). The hovered item also gets a green frame while the rule matches.
5. Pick a color with the swatch next to the rule, name the rule. It is saved to the category file automatically.

---

## Categories and config files

Every `.txt` file inside the plugin config folder (`Filters`) is a **category**:

```
<plugin config folder>/
  Filters/
    filter.txt          <- created on first run
    01-currency.txt
    10-rings-amulets.txt
    20-bows.txt
    30-maps.txt
    90-vendor-junk.txt
```

The exact path is printed under the category list in the settings window, and the **Open folder** button opens it in Explorer.

How categories behave:

- **One file = one category.** Create a new one with the text box + **+ Category** or just drop a new `.txt` into the folder.
- **Enable / disable** a whole category with the checkbox next to its name (the state is stored in the plugin settings, the file is untouched).
- **Order matters.** Categories are evaluated alphabetically, rules inside a file top to bottom, and the **first matching rule decides the color**. Prefix file names with numbers (`01-`, `10-`, `90-`) to control priority.
- **Auto-save.** Changes made in the settings window are written to the file about 0.7 s after you stop typing.
- **Hot reload.** The folder is re-scanned every ~1.5 s. Edit a file in Notepad / VS Code and the in-game rules update on their own; new files appear, deleted files disappear. **Reload** forces a rescan.
- **Delete a category** by deleting its file in Explorer.

### File format

```
// Comments before the first rule are kept as they are.
// Every rule starts with a header line:
//   ## <name> | <color> | <on|off>
// followed by an IFL expression (any number of lines).

## Life + any 2 resists ring | #FF0000 | on
HasTag("Ring") && (
  new [] {
    ItemStats[GameStat.BaseMaximumLife] >= 80,
    ItemStats[GameStat.BaseFireDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseColdDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseLightningDamageResistancePct] >= 20,
  }.Count(x => x) >= 3
)

## Currency worth a look | #FFD700AA | on
BaseName == "Divine Orb" || BaseName == "Mirror of Kalandra"

## Disabled rule | #00FF00 | off
// expressions can contain // comments
HasTag("Jewel")
```

Rules of the format:

- **Color**: `#RRGGBB` or `#RRGGBBAA` (the last two digits are alpha).
- **State**: `on` or `off` (`0` / `false` also count as off).
- The rule name must not contain the `|` character (it is replaced with `/`).
- A rule ends at the next `##` line or at the end of the file.
- Files are UTF-8.

### Example category files

**`01-currency.txt`**

```
// Currency and valuable bases, evaluated first

## Top currency | #FF2020 | on
BaseName == "Mirror of Kalandra"
|| BaseName == "Divine Orb"
|| BaseName == "Fracturing Orb"

## Useful currency | #FFD700 | on
BaseName == "Exalted Orb"
|| BaseName == "Orb of Annulment"
|| BaseName == "Chaos Orb"
```

**`10-rings-amulets.txt`**

```
## Life + resists ring | #FF0000 | on
HasTag("Ring") && (
  new [] {
    ItemStats[GameStat.BaseMaximumLife] >= 80,
    ItemStats[GameStat.BaseFireDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseColdDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseLightningDamageResistancePct] >= 20,
  }.Count(x => x) >= 3
)

## Resist-heavy amulet | #FF8800 | on
HasTag("Amulet") && (
  ItemStats[GameStat.BaseFireDamageResistancePct]
  + ItemStats[GameStat.BaseColdDamageResistancePct]
  + ItemStats[GameStat.BaseLightningDamageResistancePct] >= 60
)
```

**`30-maps.txt`**

```
## Good map | #00FF66 | on
HasTag("Map") && PlayerInfo.Level >= 65 && (
  new [] {
    ItemStats[GameStat.MapItemDropQuantityPct] >= 10,
    ItemStats[GameStat.MapItemDropRarityPct] >= 15,
  }.Count(x => x) >= 1
)

## Dangerous map | #FF00FF | on
HasTag("Map") && (
  new [] {
    ItemStats[GameStat.MapPlayerHasLevelXTemporalChains] >= 1,
    ItemStats[GameStat.MapPlayerHasLevelXEnfeeble] >= 1,
    ItemStats[GameStat.MapPlayerHasLevelXVulnerability] >= 1,
  }.Count(x => x) >= 1
)
```

**`90-vendor-junk.txt`** (enable only when selling)

```
## Sell: low value rings | #808080 | on
HasTag("Ring") && ItemStats[GameStat.BaseMaximumLife] < 40
```

---

## The settings window

```
In Inventory / In Stash / In Vendor      toggles for where frames are drawn
Thickness                                frame width

Categories (.txt files, top to bottom)
  [x] filter  (3)        <- checkbox = enabled, click the name to select
  [x] 01-currency  (2)
  [ ] 90-vendor-junk  (1)
  [____________] [+ Category]
  [Open folder] [Reload]
  <config folder path>

Rules in "filter" (first match sets the color)
  [x] [color] Life + 2 resists ring
  [x] [color] Currency worth a look
  [+ Rule]

Name:       ___________
Expression: (multi-line editor)
Matches: Vortex Talons - Chain Gloves        <- live test on the hovered item
[Join with: &&] [Up] [Delete]

Search stat / base: ___________
  (empty search -> stats of the hovered item, click to add)
  (2+ characters -> search all GameStat names and base items)
```

Notes:

- **Selecting a rule** in the list makes it the *test rule*: it is evaluated live against the last hovered item and the hovered item gets a **green frame** while it matches.
- The hovered item is remembered, so you can move the mouse into the settings window and keep editing the expression against it.
- Clicking a hint inserts a condition into the selected rule. With no rule selected, a new rule is created for you.
- **Up** moves the selected rule up (higher priority), **Delete** removes it.

### Tooltip label

Hover any highlighted item and a small label appears above the tooltip corner:

```
Rule: 10-rings-amulets: Life + resists ring
```

It shows the category file and rule name that matched, which makes overlapping rules easy to debug.

---

## 1) Expression building blocks

- Logical: `&&`, `||`, `!`
- Numeric compare: `==`, `!=`, `>=`, `<=`, `>`, `<`
- Strings: `BaseName == "..."`, `BaseName.Contains("...")`, `BaseName.StartsWith("...")`
- Tags: `HasTag("Bow")`, `HasTag("Ring")`, `HasTag("Map")`, etc.
- Player info: `PlayerInfo.Level` (your character level)
- Item stats: `ItemStats[GameStat.X]` exposes parsed numeric values of explicit / implicit stats
- Combinations: aggregate booleans and require N-of-M using `Count`

Example N-of-M pattern:

```
new [] {
  ItemStats[GameStat.BaseMaximumLife] >= 80,
  ItemStats[GameStat.BaseFireDamageResistancePct] >= 20,
  ItemStats[GameStat.BaseColdDamageResistancePct] >= 20,
}.Count(x => x) >= 2
```

> **Stat names depend on your game version.** `GameStat` is generated from the game data, so a name used in an example may not exist in your build. If an expression shows a red error in the editor, search the real name with the **Search stat / base** box, or hover an item that has the stat and click it in the list.

---

## 2) Core helpers and conventions

### 2.1 Stat sums and grouped thresholds

Use direct arithmetic when you need a total:

```
ItemStats[GameStat.BaseFireDamageResistancePct]
  + ItemStats[GameStat.BaseColdDamageResistancePct]
  + ItemStats[GameStat.BaseLightningDamageResistancePct]
  + ItemStats[GameStat.BaseChaosDamageResistancePct] >= 45
```

Use arrays for N-of-M thresholds:

```
new [] {
  ItemStats[GameStat.BaseFireDamageResistancePct] >= 15,
  ItemStats[GameStat.BaseColdDamageResistancePct] >= 15,
  ItemStats[GameStat.BaseLightningDamageResistancePct] >= 15,
}.Count(x => x) >= 2
```

Do not use `.Value` on `ItemStats[...]`; use the indexer directly. A stat that is not on the item returns `0`.

### 2.2 Open affix slots (optional)

If your `ItemFilterLibrary` build exposes `ModsInfo`:

```
// at least one open suffix and one open prefix
HasTag("Ring") && ModsInfo.HasOpenSuffix && ModsInfo.OpenPrefixCount >= 1
```

### 2.3 Sockets and item quality (optional)

```
SocketInfo.SocketNumber >= 2
```

```
ItemQuality >= 1
```

Combine quality with specific bases:

```
ItemQuality >= 1
|| BaseName == "Gold Ring"
|| BaseName == "Prismatic Ring"
|| BaseName == "Amethyst Ring"
|| BaseName == "Sapphire Ring"
```

---

## 3) Name, class, and tags

Match by item name: exact, contains, prefix:

```
BaseName == "Imperial Bow"
// or
BaseName.Contains("Bow")
// or
BaseName.StartsWith("Imperial ")
```

Group by a set of names:

```
(
  BaseName == "Imperial Bow" ||
  BaseName == "Harbinger Bow" ||
  BaseName == "Maraketh Bow"
)
```

Alphabet buckets (put them in separate files to simulate a sort-by-name priority):

```
BaseName.StartsWith("A") || BaseName.StartsWith("B")
```

ClassName and tags:

```
ClassName == "Jewel"
// or, more portable across builds:
HasTag("Jewel")
```

---

## 4) Level ranges (character level and item level)

By character level (for example while mapping at 65+):

```
PlayerInfo.Level >= 65
```

Level bracket:

```
PlayerInfo.Level >= 35 && PlayerInfo.Level <= 65
```

If your build exposes `ItemLevel`:

```
ItemLevel >= 75 && ItemLevel <= 86
```

A rule that only turns on at a given level is a good fit for leveling presets, for example a `01-leveling.txt` category you disable once you are done.

---

## 5) Affix / stat patterns (N-of-M, thresholds)

Rings with life + any two resists:

```
HasTag("Ring") && (
  new [] {
    ItemStats[GameStat.BaseMaximumLife] >= 80,
    ItemStats[GameStat.BaseFireDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseColdDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseLightningDamageResistancePct] >= 20,
  }.Count(x => x) >= 3
)
```

Approximating tiers with thresholds (treat Life >= 100 as a top-tier roll):

```
HasTag("Glove") && (
  (ItemStats[GameStat.BaseMaximumLife] >= 100) &&
  new [] {
    ItemStats[GameStat.BaseFireDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseColdDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseLightningDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseChaosDamageResistancePct] >= 10,
  }.Count(x => x) >= 1
)
```

Life **or** energy shield on body armour:

```
HasTag("Body_armour") && (
  ItemStats[GameStat.BaseMaximumLife] >= 90
  || ItemStats[GameStat.BaseMaximumEnergyShield] >= 90
)
```

Belts: life with at least one resist:

```
HasTag("Belt") && ItemStats[GameStat.BaseMaximumLife] >= 70 && (
  ItemStats[GameStat.BaseFireDamageResistancePct] >= 20
  || ItemStats[GameStat.BaseColdDamageResistancePct] >= 20
  || ItemStats[GameStat.BaseLightningDamageResistancePct] >= 20
)
```

---

## 6) Weapon-focused rules

Bows with speed, %phys or added elemental damage (any two):

```
HasTag("Bow") && (
  new [] {
    ItemStats[GameStat.LocalPhysicalDamagePct] >= 120,
    ItemStats[GameStat.LocalAttackSpeedPct] >= 15,
    ItemStats[GameStat.LocalMinimumAddedFireDamage] >= 30,
    ItemStats[GameStat.LocalMinimumAddedColdDamage] >= 15,
  }.Count(x => x) >= 2
)
```

Combine a name filter with a threshold cluster:

```
HasTag("Bow") && BaseName.Contains("Imperial") && (
  new [] {
    ItemStats[GameStat.LocalPhysicalDamagePct] >= 120,
    ItemStats[GameStat.LocalAttackSpeedPct] >= 12,
  }.Count(x => x) >= 2
)
```

---

## 7) Jewels

Any jewel with at least one useful stat:

```
HasTag("Jewel") && (
  new [] {
    ItemStats[GameStat.ProjectileDamagePct] >= 1,
    ItemStats[GameStat.LightningDamagePct] >= 1,
    ItemStats[GameStat.ElementalDamagePct] >= 1,
    ItemStats[GameStat.AttackSpeedPct] >= 1,
  }.Count(x => x) >= 1
)
```

---

## 8) Maps: good vs bad

Good maps (any one beneficial mod):

```
HasTag("Map") && PlayerInfo.Level >= 65 && (
  new [] {
    ItemStats[GameStat.MapItemDropQuantityPct] >= 10,
    ItemStats[GameStat.MapItemDropRarityPct] >= 15,
  }.Count(x => x) >= 1
)
```

Bad maps (deny-list; matches when any one bad mod is present). Keep these in their own category with a different color, so a map can be flagged both ways without conflicts:

```
HasTag("Map") && (
  new [] {
    ItemStats[GameStat.MapPlayerHasLevelXTemporalChains] >= 1,
    ItemStats[GameStat.MapPlayerHasLevelXElementalWeakness] >= 1,
    ItemStats[GameStat.MapPlayerHasLevelXEnfeeble] >= 1,
    ItemStats[GameStat.MapPlayerHasLevelXVulnerability] >= 1,
  }.Count(x => x) >= 1
)
```

Put the "bad map" file **before** the "good map" file (for example `29-maps-bad.txt`, `30-maps-good.txt`): the first match wins, so a dangerous map is flagged as dangerous even when it also has good mods.

---

## 9) Currency and specific items

By exact names:

```
BaseName == "Divine Orb"
|| BaseName == "Mirror of Kalandra"
|| BaseName == "Fracturing Orb"
```

By pattern (everything with "Orb" in the name):

```
BaseName.Contains("Orb")
```

Valuable bases in one list:

```
BaseName == "Stygian Vise"
|| BaseName == "Astral Plate"
|| BaseName == "Sorcerer Boots"
```

---

## 10) Vendor / NPC window

The same rules apply to the NPC vendor window (**In Vendor** toggle, both the town vendor and the hideout vendor window are covered). Practical ideas:

- A dedicated `90-vendor-*.txt` category you enable only while shopping.
- "Is this worth buying": reuse the ring / amulet / belt rules from section 5 as they are.
- Socketed items, if your build exposes `SocketInfo`:

```
SocketInfo.SocketNumber >= 1
```

Special stash tabs (currency tab, fragment tab, map tab, ...) have a different layout than normal tabs and are not covered by the stash highlighting yet.

---

## 11) Tips

- **Put the rare / important rules first** (small number prefix in the file name). The first match wins.
- **One idea per rule.** Many small rules with names are easier to debug than one huge expression, and the tooltip label tells you which one fired.
- **Use comments.** `//` works inside expressions and in the header of the file.
- **Keep experiments in a disabled category** instead of deleting them.
- Back up or version the `Filters` folder. It is just text, so it plays well with git and can be shared between characters or friends.

---

## Troubleshooting

| Problem | What to check |
|---|---|
| Red `Error: ...` under the expression | Syntax error or an unknown `GameStat` name. Use the search box to find the real name. |
| Nothing is highlighted | `Enable` is on? The category checkbox is on? The rule is `on`? Is another rule above it matching first? |
| Test says `Syntax OK. Hover an item to test it.` | No item has been hovered yet. Hover one in the inventory, stash or vendor window. |
| Plugin does not build: `ItemFilterLibrary` not found | Put `ItemFilterLibrary.dll` next to `ExileCore.dll` or fix the `HintPath` in the `.csproj`. |
| Edits in Notepad do not show up | Wait ~1.5 s or press **Reload**. Make sure the file ends in `.txt` and is inside the `Filters` folder. |
| Rules from an older version are gone | The config folder depends on the plugin name. Copy your old `.txt` files into the new `Filters` folder (use **Open folder**). |

---

## Credits

- Expression engine and syntax: `ItemFilterLibrary` and the [`InvWithLinq`](https://github.com/DaxxTrias/InvWithLinq) / `NPCInvWithLinq` family of plugins.
- Built on ExileApi.
