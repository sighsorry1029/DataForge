# Archived attack-reference diagnostics

This feature was removed from runtime code at the user's request. It is retained
here only as implementation reference, not as an optional or disabled feature.
`AttackReference.cs.txt` is an unchanged copy from commit
`7e0df631ef040b38f5c6b3d1878db88eea8b814f` (DataForge 1.3.8).
Its SHA-256 is `47CE5F9CB221EAB4187D89DD9B79696DCC5F9EC08AC46A3C42D98EEEF2DABD35`.
The `.txt` extension and absence from the project compile list keep it out of the
DLL. The release package also excludes this directory.

The former integration captured `AttackReference.Capture(shared)` in
`ItemOverrideManager.ItemDefinition.From` and `AttackReference.Capture(reaction)`
for `SE_React` in `StatusEffectOverrideManager.StatusEffectDefinition.From`.
Each definition stored an internal `List<string> ReferenceComments`. Both compact
references and full scaffolds passed it to the optional `getComments` callback
of `DataForgeReferenceSections.SerializeReferenceSections`. That callback and
`AppendEntryComments` were removed with the feature; the normal file and owner
section headers remain. The old comment writer prefixed every line with `# `,
including lines split on CR/LF, U+0085, U+2028 and U+2029.

The traversal read linked Projectile, Aoe and SpawnAbility prefab data. It did not
instantiate, modify or register prefabs, calculate final combat damage, or expose
new editable YAML fields. It followed only active branches, detected cycles and
bounded traversal depth/visits/components. Its baseline data could not account
for later changes by ammunition, runtime patches or other mods.

Existing generated files lose these comments when regenerated with the updated
DLL; existing full scaffolds require running the full-file command again.
User-written override files and exported copies are not rewritten.
