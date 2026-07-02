using System;

using System.Collections.Generic;

using Singular.Helpers;

using Singular.Managers;

using Styx;

using Styx.Combat.CombatRoutine;

using Styx.Logic;

using Styx.Logic.Combat;

using Styx.Logic.POI;

using Styx.WoWInternals;

using Styx.WoWInternals.WoWObjects;



namespace Singular.Utilities

{

    public static class EventHandlers

    {

        public static void Init()

        {

            // WotLK 3.3.5a: Re-enabled with direct memory reading in LuaEvents

            if (SingularRoutine.CurrentWoWContext != WoWContext.Battlegrounds &&

                !StyxWoW.Me.CurrentMap.IsRaid)

                AttachCombatLogEvent();

        }



        internal static void PlayerOnMapChanged(BotEvents.Player.MapChangedEventArgs args)

        {

            // Since we hooked this in ctor, make sure we are the selected CC

            if (RoutineManager.Current.Name != SingularRoutine.Instance.Name)

                return;



            if (SingularRoutine.CurrentWoWContext == WoWContext.Battlegrounds ||

                StyxWoW.Me.CurrentMap.IsRaid)

                DetachCombatLogEvent();

            else

                AttachCombatLogEvent();



            //Why would we create same behaviors all over ?

            if (SingularRoutine.LastWoWContext == SingularRoutine.CurrentWoWContext)

            {

                return;

            }



            Logger.Write("Context changed. New context: " + SingularRoutine.CurrentWoWContext + ". Rebuilding behaviors.");

            SingularRoutine.Instance.CreateBehaviors();

        }



        private static bool _combatLogAttached;

        private static void AttachCombatLogEvent()

        {

            if (_combatLogAttached)

                return;



            // DO NOT EDIT THIS UNLESS YOU KNOW WHAT YOU'RE DOING!

            // This ensures we only capture certain combat log events, not all of them.

            // This saves on performance, and possible memory leaks. (Leaks due to Lua table issues.)

            Lua.Events.AttachEvent("COMBAT_LOG_EVENT_UNFILTERED", HandleCombatLog);

            if (

                !Lua.Events.AddFilter(

                    "COMBAT_LOG_EVENT_UNFILTERED",

                    "return args[2] == 'SPELL_CAST_SUCCESS' or args[2] == 'SPELL_AURA_APPLIED' or args[2] == 'SPELL_MISSED' or args[2] == 'SPELL_CAST_FAILED' or args[2] == 'RANGE_MISSED' or args[2] =='SWING_MISSED'"))

            {

                Logger.Write("ERROR: Could not add combat log event filter! - Performance may be horrible, and things may not work properly!");

            }



            Logger.WriteDebug("Attached combat log");

            _combatLogAttached = true;

        }



        private static void DetachCombatLogEvent()

        {

            if (!_combatLogAttached)

                return;



            Logger.WriteDebug("Detached combat log");

            Lua.Events.DetachEvent("COMBAT_LOG_EVENT_UNFILTERED", HandleCombatLog);

            _combatLogAttached = false;

        }



        private static void HandleCombatLog(object sender, LuaEventArgs args)

        {

            var e = new CombatLogEventArgs(args.EventName, args.FireTimeStamp, args.Args);

            //Logger.WriteDebug("[CombatLog] " + e.Event + " - " + e.SourceName + " - " + e.SpellName);

            switch (e.Event)

            {

                case "SPELL_AURA_APPLIED":

                case "SPELL_CAST_SUCCESS":

                    if (e.SourceGuid != StyxWoW.Me.Guid)

                    {

                        return;

                    }



                    // Update the last spell we cast. So certain classes can 'switch' their logic around.

                    Spell.LastSpellCast = e.SpellName;

                    //Logger.WriteDebug("Successfully cast " + Spell.LastSpellCast);



                    // Force a wait for all summoned minions. This prevents double-casting it.

                    if (SingularRoutine.MyClass == WoWClass.Warlock && e.SpellName.StartsWith("Summon "))

                    {

                        StyxWoW.SleepForLagDuration();

                    }

                    break;



                case "SPELL_CAST_FAILED":

                    if (e.SourceGuid != StyxWoW.Me.Guid)

                        return;



                    var failedType = GetSpellSuffixParam(e);

                    if (failedType == "IMMUNE")

                        HandleImmuneEvent(e, "SPELL_CAST_FAILED");

                    break;



                case "SPELL_MISSED":

                case "RANGE_MISSED":

                case "SWING_MISSED":

                    var missType = GetMissType(e);

                    if (missType == "EVADE")

                    {

                        Logger.Write("Mob is evading. Blacklisting it!");

                        Blacklist.Add(e.DestGuid, TimeSpan.FromMinutes(30));

                        if (StyxWoW.Me.CurrentTargetGuid == e.DestGuid)

                        {

                            StyxWoW.Me.ClearTarget();

                        }



                        BotPoi.Clear("Blacklisting evading mob");

                        StyxWoW.SleepForLagDuration();

                    }

                    else if (missType == "IMMUNE")

                    {

                        HandleImmuneEvent(e, e.Event);

                    }

                    break;

            }

        }



        /// <summary>

        /// Learn spell/school immunity from combat log IMMUNE events on our casts.

        /// </summary>

        private static void HandleImmuneEvent(CombatLogEventArgs e, string eventType)

        {

            if (e.SourceGuid != StyxWoW.Me.Guid)

                return;



            WoWUnit unit = e.DestUnit;

            if (unit == null || unit.IsPlayer)

                return;



            Logger.Write("[Immunity] {0} is immune to {1} ({2}) via {3}", unit.Name, e.SpellName, e.SpellSchool, eventType);

            SpellImmunityManager.AddImmune(unit.Entry, e.SpellName, e.SpellSchool);

        }



        /// <summary>

        /// WotLK 3.3.5a missType index — Cata+ uses index 14, WotLK uses 11 (spell) or 8 (swing).

        /// </summary>

        private static string GetMissType(CombatLogEventArgs e)

        {

            if (e.Args == null)

                return string.Empty;



            switch (e.Event)

            {

                case "SWING_MISSED":

                    return e.Args.Length > 8 ? e.Args[8].ToString() : string.Empty;

                case "SPELL_MISSED":

                case "RANGE_MISSED":

                    return GetSpellSuffixParam(e);

                default:

                    return string.Empty;

            }

        }



        /// <summary>

        /// WotLK spell suffix param (missType / failedType) at Args[11] after spell prefix.

        /// </summary>

        private static string GetSpellSuffixParam(CombatLogEventArgs e)

        {

            return e.Args != null && e.Args.Length > 11 ? e.Args[11].ToString() : string.Empty;

        }

    }

}


