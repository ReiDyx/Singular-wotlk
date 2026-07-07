using System;
using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using TreeSharp;

namespace Singular.ClassSpecific.Warrior
{
    static class Common
    {
        private static readonly WaitTimer ChargeTimer = new WaitTimer(TimeSpan.FromMilliseconds(2000));

        public static bool PreventDoubleCharge
        {
            get
            {
                var tmp = ChargeTimer.IsFinished;
                if (tmp)
                    ChargeTimer.Reset();
                return tmp;
            }
        }

        [Class(WoWClass.Warrior)]
        [Spec(TalentSpec.ArmsWarrior)]
        [Spec(TalentSpec.FuryWarrior)]
        [Spec(TalentSpec.ProtectionWarrior)]
        [Behavior(BehaviorType.Rest)]
        [Context(WoWContext.All)]
        public static Composite CreateWarriorRest()
        {
            return Rest.CreateDefaultRestBehaviour();
        }
    }
}
