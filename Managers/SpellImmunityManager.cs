using System;
using System.Collections.Generic;
using System.Linq;
using Singular;
using Singular.Helpers;
using Styx;
using Styx.Logic.Combat;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace Singular.Managers
{
    static class SpellImmunityManager
    {
        // NPC entry -> spell schools learned from combat log IMMUNE (per combat session)
        static readonly Dictionary<uint, WoWSpellSchool> ImmuneSchools = new Dictionary<uint, WoWSpellSchool>();

        // NPC entry -> spell names learned from combat log IMMUNE (per combat session)
        static readonly Dictionary<uint, HashSet<string>> ImmuneSpells = new Dictionary<uint, HashSet<string>>();

        // Unit GUID -> spell names learned from fallback only (avoids cross-mob false positives)
        static readonly Dictionary<ulong, HashSet<string>> ImmuneSpellsByGuid = new Dictionary<ulong, HashSet<string>>();

        // Pending debuff checks after cast — one entry per guid+spell
        static readonly List<PendingDebuffCheck> PendingDebuffChecks = new List<PendingDebuffCheck>();

        // Failed apply attempts per guid+spell before marking immune (fallback)
        static readonly Dictionary<string, int> DebuffFailAttempts = new Dictionary<string, int>();

        const int MaxDebuffFailAttempts = 2;

        static readonly HashSet<string> TrackedDebuffSpells = new HashSet<string>
        {
            "Moonfire",
            "Insect Swarm",
            "Immolate",
            "Corruption",
            "Curse of Agony",
            "Curse of the Elements",
            "Unstable Affliction",
            "Haunt",
            "Seed of Corruption",
            "Blood Plague",
            "Frost Fever",
            "Flame Shock",
        };

        struct PendingDebuffCheck
        {
            public uint Entry;
            public ulong UnitGuid;
            public string SpellName;
            public string UnitName;
            public DateTime RegisteredAt;
        }

        /// <summary>
        /// Record immunity from combat log (school + spell name, keyed by NPC entry).
        /// </summary>
        public static void AddImmune(uint mobId, string spellName, WoWSpellSchool school)
        {
            if (school != 0)
            {
                if (ImmuneSchools.ContainsKey(mobId))
                    ImmuneSchools[mobId] |= school;
                else
                    ImmuneSchools.Add(mobId, school);
            }

            if (string.IsNullOrEmpty(spellName))
                return;

            HashSet<string> spells;
            if (!ImmuneSpells.TryGetValue(mobId, out spells))
            {
                spells = new HashSet<string>();
                ImmuneSpells.Add(mobId, spells);
            }
            spells.Add(spellName);
        }

        public static void Add(uint mobId, WoWSpellSchool school)
        {
            AddImmune(mobId, null, school);
        }

        public static bool IsImmune(this WoWUnit unit, WoWSpellSchool school)
        {
            return unit != null && ImmuneSchools.ContainsKey(unit.Entry) && (ImmuneSchools[unit.Entry] & school) > 0;
        }

        public static bool IsImmuneToSpell(this WoWUnit unit, string spellName)
        {
            if (unit == null || string.IsNullOrEmpty(spellName))
                return false;

            HashSet<string> guidSpells;
            if (ImmuneSpellsByGuid.TryGetValue(unit.Guid, out guidSpells) && guidSpells.Contains(spellName))
                return true;

            HashSet<string> entrySpells;
            return ImmuneSpells.TryGetValue(unit.Entry, out entrySpells) && entrySpells.Contains(spellName);
        }

        /// <summary>
        /// Reset all learned immunity — called when combat ends so each pull starts fresh.
        /// </summary>
        public static void Clear()
        {
            ImmuneSchools.Clear();
            ImmuneSpells.Clear();
            ImmuneSpellsByGuid.Clear();
            PendingDebuffChecks.Clear();
            DebuffFailAttempts.Clear();
            Logger.WriteDebug("[Immunity] Cleared learned immunity (combat ended)");
        }

        /// <summary>
        /// New target — retry all spells on this unit; drop prior fallback blocks for its GUID.
        /// </summary>
        public static void OnTargetChanged(ulong targetGuid)
        {
            if (targetGuid == 0)
                return;

            ImmuneSpellsByGuid.Remove(targetGuid);

            var prefix = targetGuid + "_";
            var keysToRemove = DebuffFailAttempts.Keys.Where(k => k.StartsWith(prefix)).ToList();
            foreach (var key in keysToRemove)
                DebuffFailAttempts.Remove(key);

            PendingDebuffChecks.RemoveAll(c => c.UnitGuid == targetGuid);
        }

        /// <summary>
        /// Queue a debuff verify after cast — fallback when combat log does not report IMMUNE.
        /// </summary>
        public static void RegisterDebuffCast(WoWUnit unit, string spellName)
        {
            // PvP: no fallback on players (bubble etc.) — combat log path already skips IsPlayer
            if (unit == null || unit.IsMe || unit.IsFriendly || unit.IsPlayer || string.IsNullOrEmpty(spellName))
                return;

            if (!TrackedDebuffSpells.Contains(spellName))
                return;

            if (unit.IsImmuneToSpell(spellName))
                return;

            // One pending check per guid+spell — rapid recasts only refresh the timer
            var now = DateTime.UtcNow;
            for (var i = 0; i < PendingDebuffChecks.Count; i++)
            {
                var pending = PendingDebuffChecks[i];
                if (pending.UnitGuid != unit.Guid || pending.SpellName != spellName)
                    continue;

                pending.RegisteredAt = now;
                PendingDebuffChecks[i] = pending;
                return;
            }

            PendingDebuffChecks.Add(new PendingDebuffCheck
            {
                Entry = unit.Entry,
                UnitGuid = unit.Guid,
                SpellName = spellName,
                UnitName = unit.SafeName(),
                RegisteredAt = now
            });
        }

        /// <summary>
        /// Verify pending debuff casts — mark immune if aura never applied after retries.
        /// </summary>
        public static void Pulse()
        {
            if (PendingDebuffChecks.Count == 0)
                return;

            var latencyMs = StyxWoW.WoWClient.Latency + 800;
            var now = DateTime.UtcNow;
            var incrementedThisPulse = new HashSet<string>();

            for (var i = PendingDebuffChecks.Count - 1; i >= 0; i--)
            {
                var check = PendingDebuffChecks[i];
                if (now.Subtract(check.RegisteredAt).TotalMilliseconds < latencyMs)
                    continue;

                PendingDebuffChecks.RemoveAt(i);

                // Match exact unit only — avoid false positives from another mob with same entry
                var unit = ObjectManager.GetObjectsOfType<WoWUnit>(true, false)
                    .FirstOrDefault(u => u.IsValid && u.Guid == check.UnitGuid);

                if (unit == null || !unit.IsAlive)
                    continue;

                if (unit.HasMyAura(check.SpellName))
                {
                    DebuffFailAttempts.Remove(GetFailKey(check.UnitGuid, check.SpellName));
                    continue;
                }

                // Player targets (e.g. bubble) — do not learn immunity from failed debuff applies
                if (unit.IsPlayer)
                    continue;

                var failKey = GetFailKey(check.UnitGuid, check.SpellName);

                // Only count one fail attempt per pulse — duplicate pending checks must not double-count
                if (!incrementedThisPulse.Add(failKey))
                    continue;

                int attempts;
                DebuffFailAttempts.TryGetValue(failKey, out attempts);
                attempts++;
                DebuffFailAttempts[failKey] = attempts;

                if (attempts < MaxDebuffFailAttempts)
                {
                    Logger.WriteDebug("{0} attempt {1}/{2} — {3} not on {4}, will retry",
                        check.SpellName, attempts, MaxDebuffFailAttempts, check.SpellName, check.UnitName);
                    continue;
                }

                Logger.Write("{0} cannot apply on {1} — skipping this fight (immune or immune-like)", check.SpellName, check.UnitName);
                AddImmuneSpellByGuid(check.UnitGuid, check.SpellName);
                DebuffFailAttempts.Remove(failKey);
            }
        }

        static void AddImmuneSpellByGuid(ulong guid, string spellName)
        {
            HashSet<string> spells;
            if (!ImmuneSpellsByGuid.TryGetValue(guid, out spells))
            {
                spells = new HashSet<string>();
                ImmuneSpellsByGuid.Add(guid, spells);
            }
            spells.Add(spellName);
        }

        static string GetFailKey(ulong guid, string spellName)
        {
            return guid + "_" + spellName;
        }
    }
}
