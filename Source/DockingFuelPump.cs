﻿using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
//using OpenNodeParser;


namespace DockingFuelPump
{

    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class DFPSettings : MonoBehaviour
    {
        private void Awake()
        {
            ConfigNode[] configs = GameDatabase.Instance.GetConfigNodes("DOCKINGFUELPUMP");
            foreach (var cfg in configs)
            {
                if (cfg.HasNode("RESOPTS"))
                {
                    var resopts = cfg.GetNode("RESOPTS");
                    foreach (var v in resopts.GetValues("resource"))
                    {
                        string[] s = v.Split(',');
                        if (s.Length >= 2)
                        {
                            switch (s[0].Trim())
                            {
                                case "ignore":
                                    DockingFuelPump.ignore_resources.Add(s[1].Trim());
                                    break;
                                case "reverse":
                                    DockingFuelPump.reverse_resources.Add(s[1].Trim());
                                    break;
                            }
                        }
                    }
                }
            }
        }
    }


    public class DockingFuelPump : PartModule
    {
        //Special Resources
        internal static List<string> ignore_resources = new List<string>();
        internal static List<string> reverse_resources = new List<string>();

        //North and South Parts; parts divided in relationship to the docking port. Fuel will be pumped from the south to the north.
        internal List<Part> north_parts = new List<Part>(); //Parts "North" of the docking port are those which are connected via a docking join
        internal List<Part> south_parts = new List<Part>(); //Parts "South" of the docking port are those which are connected via the attachment node on the docking port
        internal Dictionary<string, List<PartResource>> source_resources = new Dictionary<string, List<PartResource>>();
        internal Dictionary<string, List<PartResource>> sink_resources = new Dictionary<string, List<PartResource>>();
        internal List<string> resources_to_transfer = new List<string>();

        internal bool is_docked = false;
        internal Part docked_to;
        internal DockingFuelPump opposite_pump;
        internal bool reverse_pump = false;
        internal double pump_size;
        internal double scale_factor = 20.0;
        internal double current_flow_rate;
        internal double cold_temp = 400; //below this temperature no flow rate adjustment is made

        public bool pump_running = false;
        public bool warning_displayed = false;

        internal ModuleDockingNode docking_module;
        internal bool state_changed = false;
        internal int state_check_delay;
        internal string last_state;


        new void Awake()
        {
            base.Awake();
            if (HighLogic.CurrentGame != null)
                current_flow_rate = HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().flow_rate;
        }

        [KSPEvent(guiActive = true, guiName = "Pump Fuel", active = false)]
        public void pump_out()
        {
            reverse_pump = false;
            start_fuel_pump();
        }

        [KSPEvent(guiActive = true, guiName = "Stop Pump", active = false)]
        public void stop_pump_out()
        {
            stop_fuel_pump();
        }

        //        [KSPEvent(guiActive = true, guiName = "pump_test", active = true)]
        //        public void pump_test(){
        //            log(docking_module.state);
        //        }

        [KSPField(isPersistant = true, guiActive = false, guiName = "Fuel Pump flow rate")]
        public string fuel_pump_data;

        [KSPField(isPersistant = true, guiActive = true, guiName = "Fuel Pump Info")]
        public string fuel_pump_info;




        //setup events to show/hide the pump fuel buttong and to stop the fuel pump when the port is undocked or it goes kaboomy
        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            docking_module = this.part.FindModuleImplementing<ModuleDockingNode>();
            onVesselModify();
            GameEvents.onVesselStandardModification.Add(onVesselModify);

            this.part.OnJustAboutToBeDestroyed += () =>
            {
                log("just about to be destroyed called");
                if (pump_running)
                {
                    stop_fuel_pump();
                    if (opposite_pump)
                    {
                        opposite_pump.stop_fuel_pump();
                    }
                }
            };
        }

        //called on each frame, this calls the main process for transfering resources
        public override void OnUpdate()
        {
            if (HighLogic.CurrentGame == null)
                return;

            //if the state (of ModuleDockingNode) has changed, count down n frames (defined by value of state_check_delay) and then check the state again.
            //This is because when the vessel changes and onVesselStandardModification fires the state of the ModuleDockingNode doesn't imediatly reflect
            //the final state the docking port ends up in.  That happens slightly after, so this gives ModuleDockingNode a chance to change.
            //this essentially waits for the ModuleDockingNode state to stop changing before using it's state to decide if the pump_out gui element should be shown.
            if (state_changed)
            {
                state_check_delay -= 1;
                if (state_check_delay <= 0)
                {
                    check_state();
                }
            }

            if (pump_running)
            {
                transfer_resources();
            }
        }

        public void onDestroy()
        {
            GameEvents.onVesselStandardModification.Remove(onVesselModify);
        }


        //Called by the onVesselStandardModification Event
        //sets a flag to check the state of the docking module after a few physics frames
        public void onVesselModify(Vessel ves = null)
        {
            check_state(true);
        }

        //used in checking the state of ModuleDockingNode.  If the state of ModuleDockingNode has changed since it was last checked then this sets a delay
        //for a number of frames to wait before checking it again and will continue checking it until the state stops changing.  If the state hasn't changed
        //after n frames then it sets the active property of the pump_out button.
        internal virtual void check_state(bool force_check = false)
        {
            if (force_check)
            {
                last_state = "";
            }
            if (docking_module.state != last_state)
            {
                state_changed = true;
                state_check_delay = 10;
                last_state = docking_module.state;
            }
            else
            {
                state_changed = false;
                Events["pump_out"].active = (docking_module.state.StartsWith("Docked") || docking_module.state.StartsWith("PreAttached") || docking_module.state.StartsWith("Locked"));
                fuel_pump_info = docking_module.state;
            }
        }



        public virtual void start_fuel_pump()
        {
            is_docked = false;
            warning_displayed = false;

            pump_size = this.part.mass * scale_factor;
            get_docked_info();
            if (is_docked)
            {
                log("Starting Fuel Pump");
                Events["pump_out"].active = false;
                Events["stop_pump_out"].active = true;
                Fields["fuel_pump_data"].guiActive = true;

                get_part_groups();
                identify_resources();
                highlight_parts();
                current_flow_rate = HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().flow_rate;
                pump_running = true;
            }
        }

        public virtual void stop_fuel_pump()
        {
            Events["pump_out"].active = true;
            Events["stop_pump_out"].active = false;
            Fields["fuel_pump_data"].guiActive = false;
            pump_running = false;
            log("Pump stopped");
            unhighlight_parts();
        }



        //setup dictionary of resource name to list of available PartResource(s) in the given list of parts
        internal Dictionary<string, List<PartResource>> identify_resources_in_parts(List<Part> parts)
        {
            Dictionary<string, List<PartResource>> resources = new Dictionary<string, List<PartResource>>();
            foreach (Part part in parts)
            {
                foreach (PartResource res in part.Resources)
                {
                    if (!res.info.resourceFlowMode.Equals(ResourceFlowMode.NO_FLOW) && !ignore_resources.Contains(res.resourceName))
                    {
                        if (!resources.ContainsKey(res.resourceName))
                        {
                            resources.Add(res.resourceName, new List<PartResource>());
                        }
                        resources[res.resourceName].Add(res);
                    }
                }
            }
            return resources;
        }

        //finds the resources in the sink and source parts and then determines which of those exist on both sides.
        internal void identify_resources()
        {
            source_resources = identify_resources_in_parts(south_parts);
            sink_resources = identify_resources_in_parts(north_parts);
            //find the types of resources which need to be transfered
            foreach (string resource_name in sink_resources.Keys)
            {
                if (source_resources.Keys.Contains(resource_name))
                {
                    resources_to_transfer.AddUnique(resource_name);
                }
            }
        }


        //This is the Main process. Called in onUpdate if pump_running is true and handles transfering resources between tanks.
        internal void transfer_resources()
        {

            double resources_transfered = 0; //keep track of overall quantity of resources transfered across the docking port each cycle. used to auto stop the pump.

            foreach (string res_name in resources_to_transfer)
            {
                //holds the total available vs total required amount of current resource.  Also holds the max rate value as the min of all 
                //these values is used to define the amount to be transfered in this cycle.
                Dictionary<string, double> volumes = new Dictionary<string, double>() { { "available", 0.0 }, { "required", 0.0 }, { "rate", 0.0 } };
                //holds the resource tanks which have resouces to transfer and those which require resources
                Dictionary<string, List<PartResource>> tanks = new Dictionary<string, List<PartResource>>(){
                    {"available", new List<PartResource>()}, {"required", new List<PartResource>()}
                };


                bool reverse_flow = reverse_resources.Contains(res_name); //if true switches sink_resources with source_resources so this resources is transfered in the opposite direction
                //reversed resources defined in reverse_resources which is set from special resources from settings.

                //collect the available/required tanks and resource totals.
                foreach (PartResource res in (reverse_flow ? sink_resources : source_resources)[res_name])
                {
                    if (res.amount > 0 && res.flowState)
                    {
                        tanks["available"].Add(res);
                        volumes["available"] += res.amount;
                    }
                }
                foreach (PartResource res in (reverse_flow ? source_resources : sink_resources)[res_name])
                {
                    if ((res.maxAmount - res.amount > 0) && res.flowState)
                    {
                        tanks["required"].Add(res);
                        volumes["required"] += (res.maxAmount - res.amount);
                    }
                }

                //calculate the rate at which to transfer this resouce from each tank, based on how many tanks are active in transfer, size of docking port and time warp
                //rate is set as the flow_rate divided by the smallest number of active tanks.
                volumes["rate"] = (current_flow_rate * 400) / (double)(new int[] { tanks["available"].Count, tanks["required"].Count }.Min());
                volumes["rate"] = volumes["rate"] * Math.Sqrt(pump_size);//factor in size of docking port in rate of flow (larger docking ports have high flow rate).
                volumes["rate"] = volumes["rate"] * TimeWarp.deltaTime;  //factor in physics warp

                double to_transfer = volumes.Values.Min();  //the amount to transfer is selected as the min of either the required or available 
                //resources or the rate (which acts as a max flow value).

                //transfer resources between source and sink tanks.
                int i = tanks["required"].Count;
                foreach (PartResource res in tanks["required"])
                {
                    //calcualte how much to transfer into a tank
                    double max_t = new double[] { to_transfer / i, (res.maxAmount - res.amount) }.Min(); //calc the max to be transfered into this tank
                    //either as amount remaining to transfer divided by number of tanks remaining to transfer into OR free space in tank, whichever is smaller
                    res.amount += max_t;            //add the amount to the tank
                    to_transfer -= max_t;           //and deduct it from remaining amount to transfer
                    resources_transfered += max_t;  //add amount added to overall track of transfered resources
                    i -= 1;                         //reduce count of remaining tanks to transfer to

                    //extract the amount added to tank from source tanks.
                    int j = tanks["available"].Count;
                    foreach (PartResource s_res in tanks["available"])
                    {
                        double max_e = new double[] { max_t / j, s_res.amount }.Min(); //calc the max to extract as either the total amount added 
                        //(max_t) over number of source tanks OR take the available anount in the tank, which ever is smaller
                        s_res.amount -= max_e;  //deduct amonut from tank
                        max_t -= max_e;         //and deduct it from the amount remaining to extract
                        j -= 1;                 //reduce the count of remaining tanks to extract from
                    }
                    //handle rounding errors - res.amount is a double, so division of doubles can result in rounding errors.  The descrepancy is 
                    //the amount remaining on max_t, so the descrepency is deducted from the sink tank (and from the total overall transfer).
                    res.amount -= max_t;
                    resources_transfered -= max_t;
                }
            }

            //Docking Port heating
            if (HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().transfer_heating)
            {
                this.part.temperature += (0.5 + (resources_transfered / (pump_size * pump_size))) * HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().heating_factor;
                if (opposite_pump)
                {
                    opposite_pump.part.temperature = this.part.temperature; //heat the other port to the same level
                }

                if (this.part.temperature <= cold_temp)
                {
                    current_flow_rate = HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().flow_rate;
                }
                else
                {
                    current_flow_rate = (1 - ((this.part.temperature - cold_temp) / (this.part.maxTemp - cold_temp))) * HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().flow_rate;
                }
            }
            fuel_pump_info = "transfered: " + resources_transfered;

            fuel_pump_data = Math.Round(current_flow_rate, 2) * 100 + "% temp: " + Math.Round(this.part.temperature, 2);

            //Docking Port overheating when adjacent ports are both pumping (will quickly overheat and explode ports).
            if (opposite_pump && opposite_pump.pump_running)
            {
                if (!warning_displayed)
                {
                    log("opposite pump running - imminent KABOOM likely!");
                    warning_displayed = true;
                }
                this.part.temperature += 20;
                docked_to.temperature += 20;
            }

            //pump shutdown when dry.
            if (resources_transfered < 0.01)
            {
                stop_fuel_pump();
            }

            //pump power draw and shutdown when out of power.
            if ((HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().power_drain > 0) && (resources_transfered > 0))
            {
                if (this.part.RequestResource("ElectricCharge", HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().power_drain * resources_transfered) <= 0)
                {
                    stop_fuel_pump();
                }
            }
        }



        //set info about the docking.  if the part is docked this set's is_docked to true and sets docked_to to be the part it is docked with.
        public virtual void get_docked_info()
        {
            ModuleDockingNode module = this.part.FindModuleImplementing<ModuleDockingNode>();
            if (module.otherNode)
            {
                docked_to = module.otherNode.part;
                opposite_pump = docked_to.FindModuleImplementing<DockingFuelPump>();
                if (docked_to.mass * scale_factor < pump_size)
                {
                    pump_size = docked_to.mass * scale_factor;
                }
                is_docked = true;
            }
        }

        //get vessl parts (just those with resources) in two groups. Those on the opposite side of this.part to the part it is docked to (the south parts)
        //and those on (and connected to) the part this.part is docked to (the north parts). 
        public virtual void get_part_groups()
        {
            south_parts = get_descendant_parts(this.part);
            north_parts = get_descendant_parts(docked_to);
        }

        //Walk the tree structure of connected parts to recursively discover parts which are on the side of the given focal part opposite to the part it is docked to.
        //Walking the tree is blocked by parts which do not have cross feed enabled.
        public List<Part> get_descendant_parts(Part focal_part)
        {
            List<Part> descendant_parts = new List<Part>();
            List<Part> next_parts = new List<Part>();
            List<Part> connected_parts = new List<Part>();

            descendant_parts.Add(focal_part); //add the focal part   
            add_relatives(focal_part, connected_parts); //and add the imediate parent/children of the focal part to connected_parts
            remove_docked_part_from(connected_parts, focal_part);

            bool itterate = true;
            while (itterate)
            {
                next_parts.Clear();

                //select parents and children of parts in connected_parts and adds them to next_parts.
                foreach (Part part in connected_parts)
                {
                    add_relatives(part, next_parts);
                }

                //add any parts in next_parts which are not already in descendant_parts to descendant_parts and new_parts
                //if next_parts is empty then exit the loop.
                connected_parts.Clear();
                itterate = false;
                if (next_parts.Count > 0)
                {
                    itterate = true;
                    foreach (Part part in next_parts)
                    {
                        bool allow_flow = part.fuelCrossFeed || part.FindModuleImplementing<ModuleGrappleNode>();
                        if (!part_ids_for(descendant_parts).Contains(part.GetInstanceID()) && allow_flow)
                        {
                            connected_parts.Add(part);
                            descendant_parts.Add(part);
                        }
                    }
                }

                //ensure the loop will end if the above check fails.
                if (descendant_parts.Count > FlightGlobals.ActiveVessel.parts.Count)
                {
                    itterate = false;
                }
            }

            //filter descendant_parts to be just those which have resources.
            List<int> part_ids = part_ids_for(descendant_parts);
            descendant_parts.Clear();
            foreach (Part part in FlightGlobals.ActiveVessel.parts)
            {
                if (part.Resources.Count > 0)
                {
                    if (part_ids.Contains(part.GetInstanceID()))
                    {
                        descendant_parts.Add(part);
                    }
                }
            }
            return descendant_parts;
        }


        //add the parent and children of the given part to the given part list
        public void add_relatives(Part part, List<Part> relatives)
        {
            if (part.parent)
            {
                relatives.Add(part.parent);
            }
            foreach (Part p in part.children)
            {
                relatives.Add(p);
            }
        }

        //remove the part the focal part is docked to.  overridable method so when used on the claw it can be modified.
        public virtual void remove_docked_part_from(List<Part> list, Part focal_part)
        {
            list.Remove(focal_part.FindModuleImplementing<ModuleDockingNode>().otherNode.part); //but exclude the part it is docked to.
        }

        //Get array of IDs for all parts in a list of parts
        public List<int> part_ids_for(List<Part> parts)
        {
            List<int> part_ids = new List<int>();
            foreach (Part part in parts)
            {
                part_ids.Add(part.GetInstanceID());
            }
            return part_ids;
        }


        //adds different highlight to south and north parts
        public void highlight_parts()
        {
            if (HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().transfer_highlighting)
            {
                foreach (Part part in north_parts)
                {
                    part.Highlight(Color.blue);
                }
                foreach (Part part in south_parts)
                {
                    part.Highlight(Color.green);
                }
            }
        }

        //remove highlighting from parts.
        public void unhighlight_parts()
        {
            if (HighLogic.CurrentGame.Parameters.CustomParams<DFP_Settings>().transfer_highlighting)
            {
                foreach (Part part in FlightGlobals.ActiveVessel.parts)
                {
                    part.Highlight(false);
                }
            }
        }

        //debug log shorthand
        public void log(string msg)
        {
            Debug.Log("[DFP] " + msg);
        }

    }





    //    public class TestHighlight : PartModule
    //    {
    //        public void log(string msg){
    //            Debug.Log("[DFP] " + msg);
    //        }
    //
    //        internal bool is_docked = false;
    //        internal Part docked_to;
    //        internal DockingFuelPump opposite_pump;
    //
    //        [KSPEvent(guiActive = true, guiName = "highlight")]
    //        public void test_highligh(){
    //            Events["test_highligh"].active = false;
    //            Events["clear_highlight"].active = true;
    //
    //
    ////            this.part.Highlight(Color.red);
    ////            this.part.parent.Highlight(Color.blue);
    ////            foreach(Part part in this.part.children){
    ////                part.Highlight(Color.green);
    ////            }
    //
    //            ModuleDockingNode module = this.part.FindModuleImplementing<ModuleDockingNode>();
    //            log(module.state);
    ////            module.state = ModuleDockingNode.StartState.None;
    //            module.otherNode = null;
    //            log(module.state);
    //
    //        }
    //
    //        [KSPEvent(guiActive = true, guiName = "clear highlight", active = false)]
    //        public void clear_highlight(){
    //            Events["test_highligh"].active = true;
    //            Events["clear_highlight"].active = false;
    //            foreach(Part part in FlightGlobals.ActiveVessel.parts){
    //                part.Highlight(false);
    //            }
    //        }
    //    }
    //

    public class Paths
    {
        //takes any number of strings and returns them joined together with OS specific path divider, ie:
        //Paths.joined("follow", "the", "yellow", "brick", "road") -> "follow/the/yellow/brick/road or follow\the\yellow\brick\road
        //actually, not true, now it just joins them with /, as it seems mono sorts out path dividers anyway and using \ just messes things up here. (I mean, what kinda os uses \ anyway, madness).
        static public string joined(params string[] paths)
        {
            string path = paths[0];
            for (int i = 1; i < paths.Length; i++)
            {
                path = path + "/" + paths[i];
            }
            return path;
        }
    }
}