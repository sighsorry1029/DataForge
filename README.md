# DataForge

Edit Valheim recipes, items, build pieces, and status effects with YAML files.
Find an entry in the generated references, copy it, and change only what you need.
DataForge reads vanilla content and content registered by your installed mods.

![Comfort values and groups in the hammer menu](https://i.ibb.co/5xFswvGZ/comfort.gif) <br>
Small qol of showing comfort values and related comfort-group hints in the hammer menu.

## Global settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Stackable Stack Multiplier` | `1` | Multiply stack sizes by 1–10. An item's explicit `maxStackSize` takes priority. |
| `Item Weight Multiplier` | `1` | Multiply item weights by 0–2. An item's explicit `weight` takes priority. |
| `Upgrade Material Scaling` | `Vanilla` | For a base cost of 10, qualities 2/3/4 cost 10/20/30. `Flat` gives 10/10/10; `Reduced` gives 10/15/20. Exact-quality costs stay as written. |
| `Show Comfort In Hammer` | `On` | Show comfort values and related comfort-group hints in the hammer menu. Client setting. |
| `Highlight Station Extensions In Hammer` | `On` | Highlight related stations and extensions on hover. Client setting. |
| `Ignore Station Extension Spacing` | `On` | Allow extensions to be placed close together. Other placement rules still apply. |
| `maxStoredFuel` | `100` | Allow extra fireplace fuel without changing the displayed vanilla maximum. Set `0` to disable. |

## Start here

1. Install DataForge and **BepInExPack Valheim 5.4.2351**. For multiplayer, install the same DataForge version on the server and every client.
2. Start a world, or start your dedicated server and let its game data load.
3. Open `BepInEx/config/DataForge/` in that installation. If you use a mod manager, open the active profile's config folder.
4. Find the entry you want in a reference file. Copy it into the matching edit file below.
5. Keep the entry name, set `override: true`, and keep only the fields you want to change. Save the file. DataForge reloads supported changes automatically after a short delay.

**In multiplayer, edit the host or dedicated server's files.** Connected clients use the server's settings, not their own local YAML.

| I want to change… | Read this generated file | Edit this file | Guide |
| --- | --- | --- | --- |
| Ingredients, output, or crafting station | `recipes.reference.yml` | `recipes.yml` | [Recipes](#recipes) |
| Weight, damage, armor, food, or item visuals | `items.reference.yml` | `items.yml` | [Items](#items) |
| Build costs, health, comfort, or production | `pieces.reference.yml` | `pieces.yml` | [Pieces](#pieces) |
| Buffs, debuffs, or effect visuals | `effects.reference.yml` | `effects.yml` | [Effects](#effects) |
| Build-tool categories | `pieceCategory.reference.yml` | `pieceCategory.yml` | [Categories](#categories) |

For a first edit, put this in `items.yml` to make Wood lighter and stack to 100:

```yaml
- item: Wood
  override: true
  weight: 1
  maxStackSize: 100
```

### A few YAML rules

- Use spaces, not tabs. Each entry starts with `- item:`, `- recipe:`, `- piece:`, or `- effect:`.
- Dots in tables mean nested keys: `visual.icon` means `icon` inside a `visual` block, not a literal field name.
- Use the internal names from the references, such as `SwordIron`, not the translated in-game name.
- Omit fields you do not want to override. `override: false` disables that entry.
- A list such as `resources` or `conversions` replaces the whole list. Include every entry you want to keep. `[]` clears a list where supported.
- Comma-separated values have different meanings for different fields. Follow the examples below; do not assume that leaving out a number always preserves it.
- Use `None` only for fields that support it. A blank value is not a general reset command.

You can split your edits into files such as `items_balance.yml`, `recipes_food.yml`, or `effects_magic.yml`. Keep these files directly inside `DataForge/`, not in subfolders. Files load in alphabetical order; keeping each target in one file makes edits easier to track.

### Find a field that is missing from a reference

References hide many default values. Generate a **full file** to see the supported fields and their baseline values. Enter these commands in the game's console. If needed, enable it with the `-console` launch option.

```text
dataforge:full item
dataforge:full recipe
dataforge:full piece
dataforge:full effect
dataforge:full all
```

For example, `dataforge:full item` writes `items.full.yml`. Copy the fields you need into `items.yml`; **reference and full files are generated guides, not active edit files**. They can be overwritten when regenerated.

To refresh references after loading or changing your modpack:

```text
dataforge:refer all
```

You can also use `item`, `recipe`, `piece`, `effect`, `pieceCategory`, or `material` instead of `all`. Run commands after game data is ready. A remote player must be in the server's `adminlist.txt`; commands write files on the server, and detailed results appear in its console.

## Recipes

Edit `recipes.yml`. Copy the exact recipe key from `recipes.reference.yml`. If an item has several recipes, their keys may end in `;1`, `;2`, and so on.

### Change ingredients and station level

```yaml
- recipe: SwordIron
  override: true
  craftingStation: forge, 2
  resources:
    - Iron: 20, 10
    - Wood: 5, 0
    - SurtlingCore: 0, 5, 2
```

| Field | Meaning |
| --- | --- |
| `craftingStation: forge, 2` | Require a level-2 forge for the base recipe. |
| `Iron: 20, 10` | Use 20 to craft. Base upgrade cost is 10. With vanilla scaling, qualities 2, 3, and 4 cost 10, 20, and 30. |
| `Wood: 5, 0` | Use 5 to craft and none to upgrade. |
| `SurtlingCore: 0, 5, 2` | Use none to craft and exactly 5 when upgrading to quality 2. |

Resource values are **craft amount, upgrade amount, optional exact target quality**. `craftingStation: None` removes the station requirement. Omitting `resources` keeps the current ingredients; `resources: []` removes all ingredient requirements.

### Change output or remove a recipe

The output count goes after the recipe key, not in an `amount` field:

```yaml
- recipe: Sausages, 4
  override: true
```

To disable a recipe:

```yaml
- recipe: SwordIron
  override: true
  remove: true
```

To add a recipe for an existing item or a DataForge item clone, use its prefab name and supply `resources`. Use a unique suffix, such as `Sausages;custom`, to add another recipe for an item that already has one. A recipe does not create the item itself; see [Clone an item](#clone-an-item).

### Upgrader materials

For a recipe that uses an Upgrader station, put its special one-item requirement in the same `resources` list:

```yaml
- recipe: StaffThunderBlood
  override: true
  noCraftOnlyUpgrade: true
  resources:
    - Gold: 10, 5
    - NornThread: 5, 2
    - OrbThunderBlood: 1, 1
    - upgrade: Upgrader7Weapon
```

This example needs the mod that registers `StaffThunderBlood` and its materials. Copy its actual recipe key from your reference.

`upgrade: ItemPrefab` means **one item checked and consumed only by an Upgrader station**. It also accepts custom idol prefabs. Do not add amounts or quality values after it. There is no separate `upgradeResources` block. `noCraftOnlyUpgrade` marks the recipe as upgrade-only; it does not turn an ordinary crafting station into an Upgrader.

Two optional recipe fields control ingredient choice and output bonuses:

| Setting | Meaning |
| --- | --- |
| `requireOnlyOneIngredient: true, 1` | Require only one listed ingredient. The second value controls the game's output bonus from that ingredient's quality. |
| `qualityBonus: [{Fish1: 1}]` | Add output based on the consumed Fish1's quality. Quality 3 adds `ceil((3 - 1) × 1) = 2` items. |

These bonuses are separate. If you enable both, both apply.

## Items

Edit `items.yml`. Item settings change the item itself; recipe settings change how it is made.

### Get more of one item

Add a multiplier after the item name in `items.yml`:

```yaml
- item: Wood, 2
  override: true

- item: Copper, 3
  override: true
```

This doubles newly generated Wood and triples newly generated Copper, including Copper produced by a smelter. Each item has its own multiplier; `1` keeps the normal amount.

- Applies to supported loot, creature drops, gathering, crafting, cooking, and smelting paths. Mods with their own item-spawning code may bypass these paths.
- Multiplies crafting output without increasing ingredient costs. A recipe that produces 4 items produces 8 with a `2` multiplier. Item upgrades are unaffected.
- Changes new output, not stack limits or existing inventory stacks. Dropping and picking up the same stack does not multiply it again.
- Fractions are supported: `1.5` gives an average of 50% more. Fractional results are rounded randomly; a normal one-item output becomes either one or two.

### Change a weapon

```yaml
- item: SwordIron
  override: true
  weight: 0.8
  durability: 250, 50
  damage:
    slash: 55, 5
  primaryAttack:
    cost: 12, 0, 0, 0
```

| Field | Values, in order |
| --- | --- |
| `durability` | Base durability, added durability per quality level. Further options are shown in the full file. |
| `damage.slash` | Base damage, added damage per quality level. Other channels include `blunt`, `pierce`, `fire`, `frost`, `lightning`, `poison`, and `spirit`. |
| `primaryAttack.cost` | Stamina, eitr, health, health percentage (`40` means 40%). |
| `equipment.armor` | Base armor, added armor per quality level. |
| `food` | Health, stamina, eitr, regeneration, duration in seconds. |

For example, `slash: 55, 5` gives 55 base slash damage at quality 1 and 70 at quality 4, before skills and attack multipliers.

Other common fields include `name`, `description`, `value`, `maxStackSize`, `maxQuality`, `teleportable`, and `floating`. Use `true` or `false` for switches. `equipment.iceSkates` and `equipment.iceShoes` control the corresponding equipped movement flags.

### Change food

```yaml
- item: CookedMeat
  override: true
  food: 50, 25, 0, 2, 1200
```

This sets 50 health, 25 stamina, no eitr, 2 regeneration, and a 1,200-second duration.

### Clone an item

Give the clone a unique prefab name in `items.yml`:

```yaml
- item: DF_HeavyIronSword
  override: true
  cloneFrom: SwordIron
  name: Heavy iron sword
  description: A heavier iron sword with a stronger edge.
  weight: 3
  damage:
    slash: 72, 5
  visual:
    scale: 1.1
    icon: auto
```

Then give it a recipe in `recipes.yml`:

```yaml
- recipe: DF_HeavyIronSword
  override: true
  craftingStation: forge, 2
  resources:
    - Iron: 25, 10
    - Wood: 5, 0
```

Cloning copies the source item; it does not automatically copy its recipe. Keep clone names stable once players have made or saved those items.

## Pieces

Edit `pieces.yml` for buildable objects such as walls, furniture, chests, and production stations.

### Change a build cost and health

```yaml
- piece: wood_wall
  override: true
  needStation: None
  health: 250
  resources:
    - Wood: 4, true
```

Piece resource values are **amount, recover on removal**. They are different from recipe upgrade costs. `Wood: 4, false` costs four Wood without returning that resource on removal.

| Field | Use |
| --- | --- |
| `needStation` | Station required to build the piece. Use a station prefab name or `None`. |
| `pieceTable` | Build tool that contains the piece, such as `Hammer`. |
| `category` | Exact category name. An unknown name creates a category. |
| `sortOrder` | Position within its build category. |
| `comfort` | Comfort value, comfort group. Copy the group name from the full file. |
| `container` | Inventory width, height; for example `10, 4`. Requires an existing container. |
| `remove: true` | Hide the piece from build lists; does not delete placed pieces. |
| `visual.scale` | Prefab scale for newly placed pieces. |

**`needStation` and `craftingStation` do different jobs.** `needStation: forge` requires a forge to build this piece. The `craftingStation` block edits or adds a crafting-station component on the piece itself. In `recipes.yml`, `craftingStation: forge, 2` is the recipe's station requirement.

### Change a production station

```yaml
- piece: smelter
  override: true
  smelter:
    input: Coal, 20, 10
    output: 2, 30
    conversions:
      - CopperOre: Copper
      - TinOre: Tin
```

`input` is **fuel prefab, maximum fuel, maximum queued input**. `output` is **fuel per product, seconds per product**. This example replaces the conversion list with only Copper and Tin. Copy all conversions you want to keep.

Fuel-only conversions, such as the Frost Kiln's Liquid Frost production, use `None` as the input:

```yaml
- piece: piece_FrostKiln
  override: true
  smelter:
    conversions:
      - None: FrozenFuel
```

These conversions consume the station's configured fuel. Applying smelter settings does not reset its existing fuel or production queue.

The full file also covers `cookingStation`, `fermenter`, `sapCollector`, `beehive`, `stationExtension`, and `craftingStation`. Most component settings require that component to exist. `stationExtension` and `craftingStation` can also add their component. `stationExtension: None` disables an extension. A container cannot shrink along an axis while it holds items or is in use.

### Categories

Edit `pieceCategory.yml` to order categories or move whole categories between build tools. Start with the exact tool and category names in `pieceCategory.reference.yml`:

```yaml
Hammer:
  - Misc
  - Furniture
  - Furniture: GB_Parchment_Tool
  - Stone Building: GB_Parchment_Tool
GB_Parchment_Tool: []
```

This example requires `GB_Parchment_Tool`. It moves that tool's `Furniture` and `Stone Building` categories into the Hammer. An existing destination category with the same exact name is merged, so Furniture does not need a second tab.

- Names are **case-sensitive**. Copy them from the reference, not just the visible menu label.
- Listed categories come first. Unlisted categories keep their relative order afterward.
- Add a label with `Furniture, My furniture` or `Furniture, $hud_furniture`. Labels can also go before the colon in a move entry.
- `GB_Parchment_Tool: []` does not move anything by itself. The move entries do the work.
- A `pieceTable` assignment in `pieces.yml` takes priority over a whole-category move.
- Removing a move restores the original membership. The reference continues to describe the original tools and categories.

The visible `Categories` filters, including Flooring, Walls, and Roofing, are not all editable build-tool categories. Use the names listed in `pieceCategory.reference.yml`. DataForge handles matching duplicate filters during explicit category moves; a similar display label alone does not make two categories equivalent. `Feasts`, `Food`, and `Meads` are ignored by category configuration. Homestead's own category remains managed by Homestead.

## Effects

Edit `effects.yml`. Use `displayName` for an effect's visible name; items and pieces use `name`.

### Change Rested

```yaml
- effect: Rested
  override: true
  rested:
    baseTtl: 600
    ttlPerComfortLevel: 60
  stats:
    maxStats: 25, 0, 50
    regenMultiplier: 1, 1.5, 1
    attackDamage: Swords, 1.25
  damageTakenModifiers:
    fire: Resistant
    poison: Weak
```

| Field | Meaning |
| --- | --- |
| `rested.baseTtl` | Rested duration at comfort 1, in seconds. |
| `rested.ttlPerComfortLevel` | Extra seconds for each comfort level above 1. |
| `stats.maxStats` | Added maximum health, stamina, eitr. Different effects can stack these bonuses. |
| `stats.regenMultiplier` | Health, stamina, eitr regeneration multipliers. `1` is normal regeneration; `1.5` is 150%. |
| `stats.attackDamage` | Skill type, damage multiplier. |
| `damageTakenModifiers` | Resistance by damage type. Use game values such as `Normal`, `Resistant`, or `Weak`. |

Ordinary effect duration uses `time: duration, cooldown`, in seconds. Cooldown is used by abilities such as Forsaken powers; it is not a universal delay before any effect can be reapplied. Rested calculates its duration from comfort, so use the `rested` block above instead of `time`.

For MagicPlugin effects, an explicit `maxStats` replaces their native maximum-eitr bonus. Their eitr-regeneration bonus uses the third `regenMultiplier` value: `1.2` means +20%.

### Keep or replace effect visuals

```yaml
- effect: Burning
  override: true
  startEffects: vfx_Burning, vfx_Burning_blue, vfx_Burning_green
  time: 5, 0
```

`startEffects` and `stopEffects` are comma-separated prefab lists. Existing matching entries keep their original attachment, tracking, colour variant, and scale settings. Repeated names match in their original order. New prefabs use default settings; DataForge does not automatically attach every new effect to the player.

Omit the field to keep its list. Use `None` or `''` to clear it. An unknown prefab leaves that list unchanged and writes a warning to the log. To see changed visuals, let the active effect end and trigger it again.

You can clone an effect with a new `effect` name and `cloneFrom: ExistingEffect`. The clone keeps the source effect's type. To use it on an item, set an appropriate field under that item's `effects` block, such as `equipStatusEffect` or `consumeStatusEffect`. Creating a clone alone does not apply it to a character.

Type-specific blocks such as `rested`, `poison`, or `shield` only work on the matching effect type. Adding a block does not change the effect's type.

### Reactive effects

For `SE_React` effects such as `Staff_FrostOrbs`:

```yaml
- effect: Staff_FrostOrbs
  override: true
  react:
    minSpawnDamage: 0
    damagePerLevel:
      frost: 8
```

`damagePerLevel` adds damage for each item level above one. It does not edit the shared projectile prefab. Reactive settings apply to newly created effect instances.

## Names, icons, and materials

You can write names and descriptions directly, or use `$` translation tokens. Put translations in `DataForge/localization/English.yml` and the matching language files:

```yaml
$df_heavy_sword: "Heavy iron sword"
$df_heavy_sword_description: "A heavier iron sword with a stronger edge."
```

Then use `name: $df_heavy_sword` and `description: $df_heavy_sword_description` in the item entry. Use `displayName` and `tooltip` for effects. Localization is server-synced.

| Goal | Setting |
| --- | --- |
| Use a PNG for an item or piece | Put `MyIcon.png` in `DataForge/icon/`, then set `visual.icon: MyIcon`. |
| Render an item or piece icon | Set `visual.icon: auto`. Adjust `visual.iconRotation` with `x, y, z` angles if needed. |
| Use a PNG for an effect | Set `icon: MyIcon`. |
| Reuse an item icon for an effect | Set `icon: item:SwordIron`. |
| Reuse a material | Copy a name from `z_materials.reference.txt` into `visual.material`. |

Use 256×256 PNGs when possible. Store explicit PNGs on the server in multiplayer; clients download referenced icons automatically. Automatic item and piece icons are rendered by clients. **Effects do not support automatic icon rendering:** `icon: auto` looks for `auto.png`.

## If a change does not work

| Symptom | Check |
| --- | --- |
| Nothing changes | Edit the host/server's active profile, use the correct plural filename, and check `override` plus the domain's enable switch. |
| A file stops reloading | Check indentation and field names in `BepInEx/LogOutput.log`. A YAML parse failure keeps the last successfully loaded configuration. |
| An item or effect is missing from references | Confirm the other mod actually registers it in that process. Some mods skip server prefab loading; enable their server-loading option if provided. DataForge cannot generate entries from an unloaded prefab just because its DLL is installed. |
| A field is missing | Generate the domain's full file. Some defaults are hidden in compact references, and some components or game fields are not supported. |
| A visual or effect looks unchanged | Existing instances may keep their state. Reapply the effect or place a new piece as appropriate. Restart the game after replacing a DLL. |
| A weapon's damage differs from the item values | Projectiles, area effects, ammunition, and spawned creatures may own additional behaviour. Read the generated attack comments; they are diagnostics, not editable YAML. |
| An icon is missing | Check the name, server PNG, and log. Icon sync has size and count limits; use small PNGs. |

References describe available baseline data, not every active override or every runtime damage calculation. Projectile/Aoe comments marked `prefabDamage` are not total attack damage. Shared projectile and area-effect prefabs are not directly editable through those comments.

`z_resourcemap.txt` controls reference and full-file sorting only. It does not change costs, unlocks, or stats. The default map includes Deep North after Ashlands. Existing customized maps are preserved on update; back up yours before merging entries from the [default map](DataForgeResourceMap.cs). Section order defines tiers, and the first occurrence of a resource wins.

## For mod authors

- [Integration API](API.md): read-only change notifications and queries for other mods.
- [Build and verification guide](tests/README.md): developer checks and in-game test checklists.
- [Source code](https://github.com/sighsorry1029/DataForge)
