using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace DataForge;

// Baseline-only diagnostics shared by item and reactive-effect references.
// Never mutates, instantiates or registers a linked prefab.
internal static class AttackReference
{
    internal static List<string> Capture(ItemDrop.ItemData.SharedData shared)
    {
        return Capture(walk =>
        {
            AddAttack(shared.m_attack, "primaryAttack");
            AddAttack(shared.m_secondaryAttack, "secondaryAttack");
            void AddAttack(Attack attack, string label)
            {
                if (attack == null) return;
                walk.Visit(attack.m_attackProjectile, label + ".attackProjectile (" + attack.m_attackType + ")",
                    "root IProjectile; weapon/ammo HitData when fired", 0);
                walk.Visit(attack.m_spawnOnHit, label + ".spawnOnHit override; chance=" + Number(attack.m_spawnOnHitChance),
                    "depends on attack type; may replace Projectile.spawnOnHit", 0);
            }
        });
    }

    internal static List<string> Capture(SE_React react)
    {
        return Capture(walk => walk.Visit(react.m_spawnObj, "react.spawnPrefab",
            "root Projectile; its base damage + react.damagePerLevel * (itemLevel - 1)", 0));
    }

    private static List<string> Capture(Action<Traversal> capture)
    {
        Traversal traversal = new();
        try { capture(traversal); }
        catch (Exception exception)
        {
            // An optional diagnostic must not stop baseline capture or override application.
            traversal.Lines.Add("Attack graph incomplete: " + exception.GetType().Name);
        }
        if (traversal.Lines.Count > 0)
            traversal.Lines.Insert(0, "Attack graph (read-only baseline): prefab values, not final damage. Inactive branches are not followed; ammo/other mods may replace paths.");
        return traversal.Lines;
    }

    private sealed class Traversal
    {
        internal readonly List<string> Lines = new();
        private readonly HashSet<int> Path = new();
        private int Remaining = 32;
        private bool ReportedLimit;

        internal void Visit(GameObject? prefab, string edge, string setup, int depth)
        {
            if (prefab == null) return;
            if (Remaining <= 0)
            {
                if (!ReportedLimit) Lines.Add("Attack graph truncated at 32 prefab visits.");
                ReportedLimit = true;
                return;
            }
            Remaining--;
            string indent = new(' ', depth * 2);
            Lines.Add(indent + edge + " -> " + prefab.name + " [Setup: " + setup + "]");
            if (depth >= 6)
            {
                Lines.Add(indent + "  depth limit; remaining links omitted");
                return;
            }
            int id = prefab.GetInstanceID();
            if (!Path.Add(id))
            {
                Lines.Add(indent + "  cycle; already on this path");
                return;
            }
            try
            {
                MonoBehaviour[] components = prefab.GetComponentsInChildren<MonoBehaviour>(true)
                    .Where(component => component is Projectile || component is Aoe || component is SpawnAbility).ToArray();
                foreach (MonoBehaviour component in components.Take(64))
                {
                    bool active = ActiveInPrefab(component.transform, prefab.transform) && component.enabled;
                    string location = indent + "  " + ComponentPath(component.transform, prefab.transform) + " / " + component.GetType().Name +
                                      (active ? " [active] " : " [inactive/disabled] ");
                    switch (component)
                    {
                        case Projectile projectile:
                            Lines.Add(location + "prefabDamage=" + Damage(projectile.m_damage) + "; non-null Setup HitData replaces damage/eitr; prefabEitrAdd=" + Number(projectile.m_eitrAdd) +
                                "; followupInheritsHitData=" + projectile.m_projectilesInheritHitData + "; returnDirection=" + projectile.m_projectileReturn);
                            if (!active) break;
                            Visit(projectile.m_spawnOnHit, "Projectile.spawnOnHit (also TTL=" + projectile.m_spawnOnTtl + ")",
                                "first active child IProjectile; " + (projectile.m_projectilesInheritHitData ? "parent original HitData (may be null)" : "null HitData; own prefab damage"), depth + 1);
                            foreach (GameObject random in projectile.m_randomSpawnOnHit ?? new List<GameObject>())
                                Visit(random, "Projectile.randomSpawnOnHit", "root IProjectile only; null HitData", depth + 1);
                            break;
                        case Aoe aoe:
                            Lines.Add(location + "prefabDamage=" + Damage(aoe.m_damage) + "; useAttackSettings=" + aoe.m_useAttackSettings +
                                "; damagePerLevel=" + Damage(aoe.m_damagePerLevel) + "; radius=" + Number(aoe.m_radius) +
                                "; statusEffect=" + aoe.m_statusEffect);
                            if (!active) break;
                            Visit(aoe.m_chainObj, "Aoe.chainObj; startChance=" + Number(aoe.m_chainStartChance), "first active child IProjectile; forwards incoming HitData, not Aoe's own damage", depth + 1);
                            Visit(aoe.m_spawnOnHitTerrain, "Aoe.spawnOnHitTerrain", "terrain-hit path", depth + 1);
                            break;
                        case SpawnAbility spawn:
                            Lines.Add(location + "spawnPrefabs=" + string.Join(", ", (spawn.m_spawnPrefab ?? Array.Empty<GameObject>()).Where(value => value != null).Select(value => value.name)) +
                                "; creatures own their stats; maxInstancesFromWeaponLevel=" + spawn.m_setMaxInstancesFromWeaponLevel);
                            break;
                    }
                }
                if (components.Length > 64) Lines.Add(indent + "  component limit; remaining components omitted");
            }
            finally { Path.Remove(id); }
        }
    }

    private static bool ActiveInPrefab(Transform current, Transform root)
    {
        // Ignore an external disabled storage parent, but include the prefab root itself.
        while (current != null)
        {
            if (!current.gameObject.activeSelf) return false;
            if (current == root) return true;
            current = current.parent;
        }
        return false;
    }

    private static string ComponentPath(Transform current, Transform root)
    {
        List<string> parts = new();
        while (current != null)
        {
            parts.Add(current.name);
            if (current == root) break;
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string Damage(HitData.DamageTypes damage)
    {
        List<string> values = new();
        void Add(string name, float value) { if (value != 0f) values.Add(name + "=" + Number(value)); }
        Add("generic", damage.m_damage);
        Add("blunt", damage.m_blunt);
        Add("slash", damage.m_slash);
        Add("pierce", damage.m_pierce);
        Add("chop", damage.m_chop);
        Add("pickaxe", damage.m_pickaxe);
        Add("fire", damage.m_fire);
        Add("frost", damage.m_frost);
        Add("lightning", damage.m_lightning);
        Add("poison", damage.m_poison);
        Add("spirit", damage.m_spirit);
        Add("nonPlayer", damage.m_nonPlayer);
        return values.Count == 0 ? "0" : string.Join(", ", values);
    }

    private static string Number(float value) => value.ToString("G9", CultureInfo.InvariantCulture);
}
