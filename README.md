# DataForge

Easy way to configure recipes, items, pieces and effects by organized reference system. Item cloning with visual tweaks and localization. <br> 
Weight, stack, amount multiplier for items. Shows comfort group and value in hammer tab.

![](https://i.ibb.co/5xFswvGZ/comfort.gif) <br>
Small qol of marking the comfort number and comfort group within hammer tab.

## Included Quality-Of-Life Tweaks

DataForge also includes a few optional helpers for modpack operation:

- show comfort values in the hammer build UI
- highlight same comfort-group pieces while hovering a comfort piece
- ignore station extension spacing checks
- allow fireplaces to store extra fuel without changing the displayed vanilla max fuel
- profile lobby-to-world startup timing while diagnosing slow joins

## Workflow

It is built around a simple workflow:

1. Let DataForge generate readable reference files from the loaded modpack.
2. Copy only the entries you want to change into an override file.
3. Edit the compact fields you care about.
4. Use full scaffold files only when you need every supported field.

The goal is to make large modpacks easier to tune without turning every item, recipe, or piece into a wall of config.

## Why Use It

- Reference files are generated from the actual loaded game data, including vanilla and modded content.
- Reference output omits common default values, so the files stay useful for browsing.
- Override files are compact and hand-editable.
- Full scaffold generation is available on demand for deep edits.
- YAML payloads are server-synced, so server rules can drive client behavior.
- Owner sections and resource-map sorting make large references easier to scan.
- Cloning, material/icon tweaks, localization, and live-safe refreshes are handled in one place.
- Several modpack stability helpers are included for common Valheim mod conflicts.

## Supported Domains

### Items

DataForge can edit common item fields, including:

- name, description, weight, value, stack size, teleportability, floating behavior
- durability, food values, armor, equip modifiers, damage, block values, attacks
- status effects attached to equip, consume, attack, perfect block, or full adrenaline
- item cloning from an existing prefab
- visual overrides such as material, color, emission, custom icon, and auto icon rendering
- item acquisition multipliers for drops, pickup, crafting, cooking, and smelting

Example:

```yaml
- item: SwordIron
  override: true
  weight: 0.8
  durability: 250, 50
  damage:
    slash: 55, 0
  primaryAttack:
    cost: 12
```

Clone example:

```yaml
- item: SwordIronHeavy
  override: true
  cloneFrom: SwordIron
  name: Heavy iron sword
  weight: 1.4
  visual:
    icon: auto
    iconRotation: 23, 51, 25.8
    material: blackmetal
    color: 0.8, 0.85, 1, 1
    emission: 0.15
  damage:
    slash: 72, 0
```

### Recipes

Recipes use the result prefab as the main key. If the same result has multiple recipes, reference files use suffixes such as `SwordIron;1` and `SwordIron;2`.

DataForge supports:

- compact crafting station syntax
- compact resource syntax
- recipe amount
- recipe removal
- one-of ingredient recipes
- quality-based output bonus fields

Example:

```yaml
- recipe: SwordIron
  override: true
  craftingStation: forge, 2
  resources:
  - Iron: 20, 10
  - Wood: 5
```

### Pieces

Piece overrides focus on the fields that are most useful for modpack tuning:

- build table and category placement
- sort order inside a build tab
- required crafting station
- build resources
- health
- comfort amount and comfort group
- selected component configuration for containers, crafting stations, extensions, smelters, cooking stations, fermenters, sap collectors, and beehives
- `stationExtension` can add a `StationExtension` component to a piece that does not already have one. DataForge also removes StationExtension components it added when they are no longer configured; native/original extension components are restored from baseline instead of being deleted.

Example:

```yaml
- piece: wood_wall
  override: true
  pieceTable: Hammer
  category: Building
  sortOrder: 80
  needStation: None
  health: 250
  resources:
  - Wood: 4
```

Component example:

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

### Status Effects

Status effects can be edited or cloned with compact fields for duration, cooldown, icons, messages, stats, skill modifiers, damage modifiers, and effect prefabs.

Example:

```yaml
- effect: Rested
  override: true
  time: 600, 0
  stats:
    regenMultiplier: 1, 1.5, 1
    raiseSkill: Swords, 1.0
    attackDamage: Swords, 1.25
  damageTakenModifiers:
    fire: Resistant
    poison: Weak
```

## Files

DataForge uses:

```text
BepInEx/config/DataForge/
```

Main files:

```text
items.yml
items_*.yml
items.reference.yml
recipes.yml
recipes_*.yml
recipes.reference.yml
effects.yml
effects_*.yml
effects.reference.yml
pieces.yml
pieces_*.yml
pieces.reference.yml
z_materials.reference.txt
z_resourcemap.txt
localization/*.yml
icon/*.png
```

Files like `items_extra.yml`, `recipes_balance.yml`, `effects_magic.yml`, and `pieces_building.yml` are valid override files. This lets you split large configs by theme without changing the schema.

## Reference And Scaffold

Reference files are generated automatically when game data is ready and the client/server is the source of truth.

Reference files are meant for browsing and copy-paste edits:

- common defaults are omitted
- entries are grouped by owner section when possible
- item and recipe references use resource-map sorting
- piece references use tier sorting

Full scaffold files are generated only by command:

```text
dataforge:full item
dataforge:full recipe
dataforge:full effect
dataforge:full piece
dataforge:full all
```

Full scaffold files expose the supported field surface more completely and are useful when a reference entry hides a default value you want to override.

## Localization

Server-synced localization files live in:

```text
BepInEx/config/DataForge/localization/
```

Example:

```yaml
$df_item_meadhealthtest: "Test item"
$df_item_meadhealthtest_description: "A test item cloned from major healing mead."
```

Use the token in an override field:

```yaml
- item: MeadHealthtest
  override: true
  cloneFrom: MeadHealthMajor
  name: $df_item_meadhealthtest
  description: $df_item_meadhealthtest_description
```

You can also write direct text instead of a `$` token.

## Icons And Materials

Custom item icons are loaded from:

```text
BepInEx/config/DataForge/icon/
```

Use 256x256 PNG files when possible. ServerSync synchronizes the YAML value, but each client still needs the same local PNG file.

`z_materials.reference.txt` is generated as a material lookup list for visual overrides.

## Github
https://github.com/sighsorry1029/DataForge
