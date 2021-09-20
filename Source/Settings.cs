using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;



namespace DockingFuelPump
{
    // http://forum.kerbalspaceprogram.com/index.php?/topic/147576-modders-notes-for-ksp-12/#comment-2754813
    // search for "Mod integration into Stock Settings
    // HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>()
    public class DFP_Settings : GameParameters.CustomParameterNode
    {
        public override string Title { get { return ""; } } // Column header
        public override GameParameters.GameMode GameMode { get { return GameParameters.GameMode.ANY; } }
        public override string Section { get { return "Docking Fuel Pump"; } }
        public override string DisplaySection { get { return "Docking Fuel Pump"; } }
        public override int SectionOrder { get { return 1; } }
        public override bool HasPresets { get { return false; } }



        [GameParameters.CustomFloatParameterUI("Base rate for fuel transfer", asPercentage = false, displayFormat = "N2", minValue = 0.1f, maxValue = 10f, stepCount = 100,
                    toolTip = "Transfer rate is scalled port size, larger ports transfer faster")]
        public float flow_rate = 1f;

        [GameParameters.CustomFloatParameterUI("Power cost of running fuel pump", asPercentage = false, displayFormat = "N2", minValue = 0, maxValue = 5f, stepCount = 501)]
        public float power_drain = 0.05f;


        [GameParameters.CustomParameterUI("show the parts involved in the transfer")]
        public bool transfer_highlighting = true;

        [GameParameters.CustomParameterUI("docking ports get hot while transfering fuel")]
        public bool transfer_heating = true;

        [GameParameters.CustomFloatParameterUI("rate at which docking ports gain heat", asPercentage = false, displayFormat = "N2", minValue = 0.1f, maxValue = 10f, stepCount = 100)]
        public float heating_factor = 0.6f;




        public override bool Enabled(MemberInfo member, GameParameters parameters)
        {
            //if (member.Name == "enabled")
            //    return true;

            return true; //otherwise return true
        }

        public override bool Interactible(MemberInfo member, GameParameters parameters)
        {

            return true;
            //            return true; //otherwise return true
        }

        public override IList ValidValues(MemberInfo member)
        {
            return null;
        }

    }
}
