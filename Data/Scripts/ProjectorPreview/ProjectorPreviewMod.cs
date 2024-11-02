﻿using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

// TODO: if IgnoreSize=false, set IgnoreSize=true clientside when opening blueprints menu
// then if they select a different size grid, force preview mode only.

// TODO block place/remove sync between real grid and projected grid
// ^^^ needs sync of block stages, paint, and many other things...

// TODO: show missing blocks in vanilla projection - something like if <= 5% blocks (minimum 1 block) remaining then show them on UI somehow

namespace Digi.ProjectorPreview
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class ProjectorPreviewMod : MySessionComponentBase
    {
        public static ProjectorPreviewMod Instance = null;
        public static bool IsInProjectorTerminal { get; private set; }
        public static bool Debug { get; private set; }

        public bool IsInitialized = false;
        public bool IsPlayer = false;
        public float Transparency;
        public float TransparencyHighlight;
        public float TransparencyDecor;
        public float TransparencyDecorHighlight;
        public IMyProjector ViewingTerminalOf = null;
        public IMyTerminalControl ControlProjectorMode = null;
        public IMyTerminalControl ControlUseThisShip = null;
        public IMyTerminalControl ControlRemoveButton = null;
        public IMyTerminalControl ControlAlignProjector = null;
        public IMyTerminalAction ActionAlignProjector = null;
        public readonly IMyTerminalControl[] ControlRotate = new IMyTerminalControl[3];
        public readonly List<IMyTerminalControl> RefreshControls = new List<IMyTerminalControl>(); // controls to be refreshed on certain actions
        private readonly HashSet<IMyTerminalControl> ControlsAfterEverything = new HashSet<IMyTerminalControl>();
        private readonly HashSet<IMyTerminalControl> ControlsAfterRemoveButton = new HashSet<IMyTerminalControl>();
        private readonly List<IMyTerminalControl> ReorderedControls = new List<IMyTerminalControl>(16);
        private bool createdTerminalControls = false;
        public readonly List<IMyPlayer> Players = new List<IMyPlayer>();

        public const ushort PACKET_ID = 62528;
        public readonly Guid SETTINGS_GUID = new Guid("1F2F7BAA-31BA-4E75-82C4-FA29679DE822");
        public readonly Guid BLUEPRINT_GUID = new Guid("E973AD49-F3F4-41B9-811B-2B114E6EE0F9");
        public readonly MyStringHash EMISSIVE_NAME_ALTERNATIVE = MyStringHash.GetOrCompute("Alternative");
        public readonly Color LIGHT_COLOR = new Color(190, 225, 255);
        public const string CONTROL_PREFIX = "ProjectorPreview.";
        public const string REMOVE_BUTTON_ID = "Remove";
        public readonly HashSet<string> REFRESH_VANILLA_IDS = new HashSet<string>()
        {
            "KeepProjection",
            "ShowOnlyBuildable",
            "X",
            "Y",
            "Z",
            "RotX",
            "RotY",
            "RotZ",
        };
        public readonly HashSet<MyObjectBuilderType> DECORATIVE_TYPES = new HashSet<MyObjectBuilderType>()
        {
            typeof(MyObjectBuilder_CubeBlock),
            typeof(MyObjectBuilder_Passage),
            typeof(MyObjectBuilder_Wheel),
        };
        public readonly Vector3 STATUS_COLOR_NORMAL = new Color(0, 0, 0).ColorToHSVDX11();
        public readonly Vector3 STATUS_COLOR_BETTER = new Color(0, 255, 0).ColorToHSVDX11();
        public readonly Vector3 STATUS_COLOR_MISSING = new Color(255, 0, 0).ColorToHSVDX11();
        public readonly Vector3 STATUS_COLOR_WRONG_TYPE = new Color(255, 0, 255).ColorToHSVDX11();
        public readonly Vector3 STATUS_COLOR_WRONG_COLOR = new Color(55, 0, 155).ColorToHSVDX11();
        public readonly Vector3 STATUS_COLOR_WRONG_ROTATION = new Color(0, 180, 200).ColorToHSVDX11();
        public readonly MyStringId MATERIAL_SQUARE = MyStringId.GetOrCompute("Square");

        public const string ProjectedGridDisplayName = "ProjectorPreview-CustomProjection";

        public override void LoadData()
        {
            Instance = this;
            Log.ModName = "Projector Preview";

            Log.Info("Mod version: 5"); // HACK: for easy check if the correct mod version is installed, increment on updates

            Debug = MyAPIGateway.Utilities.FileExistsInLocalStorage("debug", typeof(ProjectorPreviewMod));
            if(Debug)
                Log.Info("Debug logging enabled.");

            IsPlayer = !(MyAPIGateway.Session.IsServer && MyAPIGateway.Utilities.IsDedicated);

            if(IsPlayer)
                MyEntities.OnEntityCreate += EntityCreated;
        }

        public override void BeforeStart()
        {
            IsInitialized = true;

            UpdateConfigValues();

            if(IsPlayer)
            {
                MyAPIGateway.Gui.GuiControlRemoved += GUIControlRemoved;
            }

            MyAPIGateway.Multiplayer.RegisterMessageHandler(PACKET_ID, PacketReceived);
            MyAPIGateway.TerminalControls.CustomControlGetter += CustomControlGetter;
        }

        protected override void UnloadData()
        {
            Instance = null;

            try
            {
                MyEntities.OnEntityCreate -= EntityCreated;

                if(IsInitialized)
                {
                    IsInitialized = false;

                    MyAPIGateway.Gui.GuiControlRemoved -= GUIControlRemoved;

                    MyAPIGateway.Multiplayer.UnregisterMessageHandler(PACKET_ID, PacketReceived);
                    MyAPIGateway.TerminalControls.CustomControlGetter -= CustomControlGetter;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        void EntityCreated(MyEntity ent)
        {
            try
            {
                var block = ent as MyCubeBlock;
                if(block != null)
                {
                    if(block.CubeGrid.DisplayName == ProjectedGridDisplayName)
                    {
                        // turn off updates ASAP
                        block.IsPreview = true;
                        block.NeedsUpdate = MyEntityUpdateEnum.NONE;
                    }
                    return;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void GUIControlRemoved(object obj) // only executed on players
        {
            try
            {
                var objName = obj.ToString();

                if(objName.EndsWith("ScreenOptionsSpace")) // closing options menu just assumes you changed something so it'll re-check config settings
                {
                    UpdateConfigValues();
                }
                else if(objName.EndsWith("GuiScreenTerminal")) // closing the terminal menu
                {
                    ViewingTerminalOf = null;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void UpdateConfigValues()
        {
            var cfg = MyAPIGateway.Session?.Config;
            if(cfg == null)
                return;

            var aa = (int)(cfg.AntialiasingMode ?? 0); // HACK VRageRender.MyAntialiasingMode not whitelisted.

            // finalTransparency = (SeeThrough && BlockIsDecor ? (highlight ? TransparencyDecorHighlight : TransparencyDecor) : (highlight ? TransparencyHighlight : Transparency))

            if(aa >= 1) // FXAA or any future AA
            {
                Transparency = -(1f / 3f);
                TransparencyHighlight = -0.05f;
                TransparencyDecor = -(2f / 3f);
                TransparencyDecorHighlight = -0.25f;
            }
            else // no AA
            {
                Transparency = -0.25f;
                TransparencyHighlight = -0.1f;
                TransparencyDecor = -0.5f;
                TransparencyDecorHighlight = -0.25f;
            }
        }

        #region Network sync
        private static void PacketReceived(byte[] bytes)
        {
            try
            {
                if(bytes.Length <= 2)
                {
                    if(Debug)
                        Log.Error($"PacketReceived(); invalid length <= 2; length={bytes.Length.ToString()}");

                    return;
                }

                var data = MyAPIGateway.Utilities.SerializeFromBinary<PacketData>(bytes); // this will throw errors on invalid data
                if(data == null)
                {
                    if(Debug)
                        Log.Error($"PacketReceived(); no deserialized data!");

                    return;
                }

                IMyEntity ent;
                if(!MyAPIGateway.Entities.TryGetEntityById(data.EntityId, out ent) || ent.Closed || !(ent is IMyProjector))
                {
                    if(Debug)
                        Log.Info($"PacketReceived(); {data.Type.ToString()}; {(ent == null ? "can't find entity" : (ent.Closed ? "found closed entity" : "entity not projector"))}");

                    return;
                }

                var logic = ent.GameLogic.GetAs<Projector>();
                if(logic == null)
                {
                    if(Debug)
                        Log.Error($"PacketReceived(); {data.Type.ToString()}; projector doesn't have the gamelogic component!");

                    return;
                }

                switch(data.Type)
                {
                    case PacketType.SETTINGS:
                    {
                        if(data.Settings == null)
                        {
                            if(Debug)
                                Log.Error($"PacketReceived(); {data.Type.ToString()}; settings are null!");

                            return;
                        }

                        if(Debug)
                            Log.Info($"PacketReceived(); Settings; {(MyAPIGateway.Multiplayer.IsServer ? " Relaying to clients;" : "")}Valid!\n{logic.Settings}");

                        logic.UpdateSettings(data.Settings);
                        logic.SaveSettings();

                        if(MyAPIGateway.Multiplayer.IsServer)
                            RelayToClients(((IMyCubeBlock)ent).CubeGrid.GetPosition(), bytes, data.Sender);

                        break;
                    }
                    case PacketType.REMOVE:
                        logic.RemoveBlueprints_Receiver(bytes, data.Sender);
                        break;
                    case PacketType.RECEIVED_BP:
                        logic.PlayerReceivedBP(data.Sender);
                        break;
                    case PacketType.USE_THIS_AS_IS:
                        logic.UseThisShip_Receiver(false);
                        break;
                    case PacketType.USE_THIS_FIX:
                        logic.UseThisShip_Receiver(true);
                        break;
                }
            }
            catch(Exception e)
            {
                Log.Error(e, "Invalid packet data!");
            }
        }

        public static void RelaySettingsToClients(IMyCubeBlock block, ProjectorPreviewModSettings settings)
        {
            if(Debug)
                Log.Info("RelaySettingsToClients(block,settings)");

            var data = new PacketData(MyAPIGateway.Multiplayer.MyId, block.EntityId, settings);
            var bytes = MyAPIGateway.Utilities.SerializeToBinary(data);
            RelayToClients(block.CubeGrid.GetPosition(), bytes, data.Sender);
        }

        public static void RelayToClients(Vector3D syncPosition, byte[] bytes, ulong sender)
        {
            if(Debug)
                Log.Info("RelayToClients(syncPos,bytes,sender)");

            var localSteamId = MyAPIGateway.Multiplayer.MyId;
            var distSq = MyAPIGateway.Session.SessionSettings.SyncDistance;
            distSq += 1000; // some safety padding, wouldn't want desync now...
            distSq *= distSq;

            var players = Instance.Players;
            players.Clear();
            MyAPIGateway.Players.GetPlayers(players);

            foreach(var p in players)
            {
                var id = p.SteamUserId;

                if(id != localSteamId && id != sender && Vector3D.DistanceSquared(p.GetPosition(), syncPosition) <= distSq)
                    MyAPIGateway.Multiplayer.SendMessageTo(PACKET_ID, bytes, p.SteamUserId);
            }

            players.Clear();
        }
        #endregion

        #region Terminal controls and actions
        private void CustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            try
            {
                ViewingTerminalOf = block as IMyProjector;
                IsInProjectorTerminal = ViewingTerminalOf != null;

                if(!IsInProjectorTerminal)
                    return;

                ViewingTerminalOf.RefreshCustomInfo();

                if(!createdTerminalControls || ControlRemoveButton == null)
                    return;

                ReorderedControls.Clear();

                bool foundRemoveButton = false;

                for(int i = 0; i < controls.Count; ++i)
                {
                    var c = controls[i];
                    if(c == ControlUseThisShip || c == ControlAlignProjector || ControlsAfterEverything.Contains(c) || ControlsAfterRemoveButton.Contains(c))
                        continue; // skips controls that we're gonna add later

                    if(c == ControlRemoveButton)
                    {
                        foundRemoveButton = true;
                        ReorderedControls.Add(ControlUseThisShip);
                        ReorderedControls.Add(ControlRemoveButton);

                        foreach(var mc in ControlsAfterRemoveButton)
                        {
                            ReorderedControls.Add(mc);
                        }
                    }
                    else
                    {
                        ReorderedControls.Add(c);

                        if(c.Id == "ShowOnlyBuildable")
                        {
                            ReorderedControls.Add(ControlAlignProjector);
                        }
                    }
                }

                if(!foundRemoveButton)
                    return;

                controls.Clear();
                controls.EnsureCapacity(ReorderedControls.Count + ControlsAfterEverything.Count);

                foreach(var c in ReorderedControls)
                {
                    controls.Add(c);
                }

                foreach(var c in ControlsAfterEverything)
                {
                    controls.Add(c);
                }

                ReorderedControls.Clear();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private readonly List<MyTerminalControlComboBoxItem> useThisShipOptions = new List<MyTerminalControlComboBoxItem>()
        {
            new MyTerminalControlComboBoxItem() { Key = 0, Value = MyStringId.GetOrCompute("(Pick an option)") },
            new MyTerminalControlComboBoxItem() { Key = 1, Value = MyStringId.GetOrCompute("... in its current state.") },
            new MyTerminalControlComboBoxItem() { Key = 2, Value = MyStringId.GetOrCompute("... as built & fixed.") }
        };

        public void SetupTerminalControls()
        {
            if(createdTerminalControls)
                return;

            createdTerminalControls = true;

            var tc = MyAPIGateway.TerminalControls;

            #region Vanilla control editing
            List<IMyTerminalControl> existingControls;
            tc.GetControls<IMyProjector>(out existingControls);

            for(int i = 0; i < existingControls.Count; ++i)
            {
                var vc = existingControls[i];

                if(REFRESH_VANILLA_IDS.Contains(vc.Id))
                {
                    RefreshControls.Add(vc);
                }

                if(ControlRemoveButton == null && vc.Id == REMOVE_BUTTON_ID)
                {
                    ControlRemoveButton = vc;

                    var button = (IMyTerminalControlButton)vc;

                    // edit the button to be visible when the custom projection is visible as well as normal projection
                    button.Enabled = Projector.UI_RemoveButton_Enabled;

                    // edit the action to remove both the vanilla projection and the custom projection
                    button.Action = Projector.UI_RemoveButton_Action;
                }

                // change gray-out condition for "Keep Projection" as it is awkward to use if off and you try to load a blueprint that perfectly matches the ship and it's already aligned.
                if(vc.Id == "KeepProjection")
                {
                    var checkbox = (IMyTerminalControlCheckbox)vc;
                    checkbox.Enabled = Projector.UI_KeepProjection_Enabled;
                }
            }
            #endregion

            {
                var c = tc.CreateControl<IMyTerminalControlCombobox, IMyProjector>(CONTROL_PREFIX + "UseThisShip");
                c.SupportsMultipleBlocks = false;
                c.Title = MyStringId.GetOrCompute("Load this ship...");
                c.Tooltip = MyStringId.GetOrCompute("Use the current ship as the blueprint for this projector." +
                    "\nThis copies the ship in its current state or with all current blocks fixed/built, it does not automatically update it as it changes." +
                    "\nIt also automatically aligns it for Build Mode." +
                    "\n\n(Added by Projector Preview mod)");
                c.Enabled = (b) => b.IsWorking;
                c.Setter = Projector.UI_UseThisShip_Action;
                c.Getter = (b) => 0;
                c.ComboBoxContent = (l) => l.AddList(useThisShipOptions);

                CreateAction<IMyProjector>(c,
                    icon: MyAPIGateway.Utilities.GamePaths.ContentPath + @"\Textures\GUI\Icons\Actions\Start.dds",
                    itemIds: new string[] { null, "UseShip", "UseShipBuilt" },
                    itemNames: new string[] { null, "Load this ship - as is", "Load this ship - built & fixed" });

                ControlUseThisShip = c;
                // this one gets added manually before remove button

                tc.AddControl<IMyProjector>(c);
            }

            // vanilla remove button would be here

            {
                var c = tc.CreateControl<IMyTerminalControlOnOffSwitch, IMyProjector>(CONTROL_PREFIX + "Enabled");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Projector Mode");
                c.Tooltip = MyStringId.GetOrCompute("Change how to display the projection.\nEach mode has its own configurable controls.\n\n(Added by Projector Preview mod)");
                c.OnText = MyStringId.GetOrCompute("Preview");
                c.OffText = MyStringId.GetOrCompute("Build");
                c.Enabled = Projector.UI_Preview_Enabled;
                c.Getter = Projector.UI_Preview_Getter;
                c.Setter = Projector.UI_Preview_Setter;

                CreateActionPreviewMode(c);

                RefreshControls.Add(c);
                ControlProjectorMode = c;
                ControlsAfterRemoveButton.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSeparator, IMyProjector>(string.Empty);
                c.SupportsMultipleBlocks = true;
                ControlsAfterRemoveButton.Add(c);

                // don't AddControl() because it'll be confusing if sorting doesn't work
            }

            {
                var c = tc.CreateControl<IMyTerminalControlLabel, IMyProjector>(string.Empty);
                c.SupportsMultipleBlocks = true;
                c.Label = MyStringId.GetOrCompute("Build mode configuration");
                ControlsAfterRemoveButton.Add(c);

                // don't AddControl() because it'll be confusing if sorting doesn't work
            }

            {
                var c = tc.CreateControl<IMyTerminalControlButton, IMyProjector>(CONTROL_PREFIX + "AlignProjection");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Align Projection");
                c.Tooltip = MyStringId.GetOrCompute("Finds the projector block in the projector and attempts to align it to the real projector." +
                                                    "\nThis is also automatically executed when loading current ship as a projection." +
                                                    "\n\n(Added by Projector Preview mod)");
                c.Enabled = Projector.UI_AlignProjection_Enabled;
                c.Action = Projector.UI_AlignProjection_Action;

                tc.AddControl<IMyProjector>(c);
                RefreshControls.Add(c);
                ControlAlignProjector = c;

                {
                    var a = MyAPIGateway.TerminalControls.CreateAction<IMyProjector>(CONTROL_PREFIX + "AlignProjection");
                    a.Name = new StringBuilder("Align Projection");
                    a.Icon = @"Textures\GUI\Icons\HUD 2017\BlueprintsScreen.png";
                    a.ValidForGroups = true;
                    a.Action = Projector.UI_AlignProjection_Action;
                    a.Writer = (b, sb) =>
                    {
                        sb.Append(Projector.UI_AlignProjection_Enabled(b) ? "Ready" : "Invalid\nState");
                    };

                    ActionAlignProjector = a;
                    MyAPIGateway.TerminalControls.AddAction<IMyProjector>(a);
                }
            }

            // rest of vanilla controls would fit here

            {
                var c = tc.CreateControl<IMyTerminalControlSeparator, IMyProjector>(string.Empty);
                c.SupportsMultipleBlocks = true;
                ControlsAfterEverything.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlLabel, IMyProjector>(string.Empty);
                c.Label = MyStringId.GetOrCompute("Preview mode configuration");
                c.SupportsMultipleBlocks = true;
                ControlsAfterEverything.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, IMyProjector>(CONTROL_PREFIX + "Scale");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Hologram scale");
                c.Tooltip = MyStringId.GetOrCompute("The hologram size in meters.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLogLimits(Projector.UI_Scale_LogLimitMin, Projector.UI_Scale_LogLimitMax);
                c.Getter = Projector.UI_Scale_Getter;
                c.Setter = Projector.UI_Scale_Setter;
                c.Writer = Projector.UI_Scale_Writer;

                CreateAction<IMyProjector>(c,
                    modifier: 0.1f,
                    gridSizeDefaultValue: true);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, IMyProjector>(CONTROL_PREFIX + "StatusMode");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Status mode");
                c.Tooltip = MyStringId.GetOrCompute("Colors the projected blocks depending on the status compared to the real block." +
                                                    "\n" +
                                                    "\nBlack = matches the blueprint" +
                                                    "\nYellow-Orange = integrity above red line but not full health" +
                                                    "\nOrange-Red = integrity below red line and going towards being completely destroyed" +
                                                    "\nRed blinking = missing from ship" +
                                                    "\nTeal = orientation/position is wrong (only shows up in build stage)" +
                                                    "\nDark purple = color is different" +
                                                    "\nLight purple = type is different" +
                                                    "\nGreen = blueprint has lower built percent");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = Projector.UI_Status_Getter;
                c.Setter = Projector.UI_Status_Setter;

                CreateAction<IMyProjector>(c, iconPack: "Missile");

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, IMyProjector>(CONTROL_PREFIX + "SeeThrough");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("See-through armor");
                c.Tooltip = MyStringId.GetOrCompute("Makes armor and decorative blocks more transparent.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = Projector.UI_SeeThrough_Getter;
                c.Setter = Projector.UI_SeeThrough_Setter;

                CreateAction<IMyProjector>(c, iconPack: "MovingObject");

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, IMyProjector>(CONTROL_PREFIX + "OffsetX");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Offset X");
                c.Tooltip = MyStringId.GetOrCompute("Changes the projection position.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Offset_Getter(b, 0);
                c.Setter = (b, v) => Projector.UI_Offset_Setter(b, v, 0);
                c.SetLimits(-Projector.MIN_MAX_OFFSET, Projector.MIN_MAX_OFFSET);
                c.Writer = (b, s) => Projector.UI_Offset_Writer(b, s, 0);

                CreateAction<IMyProjector>(c, modifier: 0.1f);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, IMyProjector>(CONTROL_PREFIX + "OffsetY");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Offset Y");
                c.Tooltip = MyStringId.GetOrCompute("Changes the projection position.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Offset_Getter(b, 1);
                c.Setter = (b, v) => Projector.UI_Offset_Setter(b, v, 1);
                c.SetLimits(-Projector.MIN_MAX_OFFSET, Projector.MIN_MAX_OFFSET);
                c.Writer = (b, s) => Projector.UI_Offset_Writer(b, s, 1);

                CreateAction<IMyProjector>(c, modifier: 0.1f);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, IMyProjector>(CONTROL_PREFIX + "OffsetZ");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Offset Z");
                c.Tooltip = MyStringId.GetOrCompute("Changes the projection position.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Offset_Getter(b, 2);
                c.Setter = (b, v) => Projector.UI_Offset_Setter(b, v, 2);
                c.SetLimits(-Projector.MIN_MAX_OFFSET, Projector.MIN_MAX_OFFSET);
                c.Writer = (b, s) => Projector.UI_Offset_Writer(b, s, 2);

                CreateAction<IMyProjector>(c, modifier: 0.1f);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSeparator, IMyProjector>(string.Empty);
                c.SupportsMultipleBlocks = true;
                ControlsAfterEverything.Add(c);
                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, IMyProjector>(CONTROL_PREFIX + "RotateX");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Rotate X / Pitch");
                c.Tooltip = MyStringId.GetOrCompute("Rotate projection along the X axis. This can be turned into constant spinning by checking the checkbox below.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLimits(-Projector.MIN_MAX_ROTATE, Projector.MIN_MAX_ROTATE);
                c.Getter = (b) => Projector.UI_Rotate_Getter(b, 0);
                c.Setter = (b, v) => Projector.UI_Rotate_Setter(b, v, 0);
                c.Writer = (b, s) => Projector.UI_Rotate_Writer(b, s, 0);

                CreateAction<IMyProjector>(c, modifier: 0.1f);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);
                ControlRotate[0] = c;

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, IMyProjector>(CONTROL_PREFIX + "SpinX");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Spin X / Pitch");
                c.Tooltip = MyStringId.GetOrCompute("Makes the Rotate X / Pitch slider act as spin speed.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Spin_Getter(b, 0);
                c.Setter = (b, v) => Projector.UI_Spin_Setter(b, v, 0);

                CreateAction<IMyProjector>(c);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, IMyProjector>(CONTROL_PREFIX + "RotateY");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Rotate Y / Yaw");
                c.Tooltip = MyStringId.GetOrCompute("Rotate projection along the Y axis. This can be turned into constant spinning by checking the checkbox below.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLimits(-Projector.MIN_MAX_ROTATE, Projector.MIN_MAX_ROTATE);
                c.Getter = (b) => Projector.UI_Rotate_Getter(b, 1);
                c.Setter = (b, v) => Projector.UI_Rotate_Setter(b, v, 1);
                c.Writer = (b, s) => Projector.UI_Rotate_Writer(b, s, 1);

                CreateAction<IMyProjector>(c, modifier: 0.1f);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);
                ControlRotate[1] = c;

                tc.AddControl<IMyProjector>(c);
            }
            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, IMyProjector>(CONTROL_PREFIX + "SpinY");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Spin Y / Yaw");
                c.Tooltip = MyStringId.GetOrCompute("Makes the Rotate Y / Yaw slider act as spin speed.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Spin_Getter(b, 1);
                c.Setter = (b, v) => Projector.UI_Spin_Setter(b, v, 1);

                CreateAction<IMyProjector>(c);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, IMyProjector>(CONTROL_PREFIX + "RotateZ");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Rotate Z / Roll");
                c.Tooltip = MyStringId.GetOrCompute("Rotate projection along the Z axis. This can be turned into constant spinning by checking the checkbox below.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLimits(-Projector.MIN_MAX_ROTATE, Projector.MIN_MAX_ROTATE);
                c.Getter = (b) => Projector.UI_Rotate_Getter(b, 2);
                c.Setter = (b, v) => Projector.UI_Rotate_Setter(b, v, 2);
                c.Writer = (b, s) => Projector.UI_Rotate_Writer(b, s, 2);

                CreateAction<IMyProjector>(c, modifier: 0.1f);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);
                ControlRotate[2] = c;

                tc.AddControl<IMyProjector>(c);
            }
            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, IMyProjector>(CONTROL_PREFIX + "SpinZ");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Spin Z / Roll");
                c.Tooltip = MyStringId.GetOrCompute("Makes the Rotate Z / Roll slider act as spin speed.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Spin_Getter(b, 2);
                c.Setter = (b, v) => Projector.UI_Spin_Setter(b, v, 2);

                CreateAction<IMyProjector>(c);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, IMyProjector>(CONTROL_PREFIX + "LightIntensity");
                c.SupportsMultipleBlocks = true;
                c.Title = MyStringId.GetOrCompute("Hologram light intensity");
                c.Tooltip = MyStringId.GetOrCompute("The intensity of the light emitted by the hologram.");
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLimits(Projector.MIN_LIGHTINTENSITY, Projector.MAX_LIGHTINTENSITY);
                c.Getter = Projector.UI_LightIntensity_Getter;
                c.Setter = Projector.UI_LightIntensity_Setter;
                c.Writer = Projector.UI_LightIntensity_Writer;

                CreateAction<IMyProjector>(c, modifier: 0.1f);

                ControlsAfterEverything.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<IMyProjector>(c);
            }
        }

        private void CreateActionPreviewMode(IMyTerminalControlOnOffSwitch c)
        {
            var id = ((IMyTerminalControl)c).Id;
            var gamePath = MyAPIGateway.Utilities.GamePaths.ContentPath;
            Action<IMyTerminalBlock, StringBuilder> writer = (b, s) => s.Append(c.Getter(b) ? c.OnText.String : c.OffText.String);

            {
                var a = MyAPIGateway.TerminalControls.CreateAction<IMyProjector>(CONTROL_PREFIX + id + "_Toggle");
                a.Name = new StringBuilder(c.Title.String).Append(" - ").Append(c.OnText.String).Append("/").Append(c.OffText.String);
                a.Icon = gamePath + @"\Textures\GUI\Icons\Actions\SmallShipToggle.dds";
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, !c.Getter(b));
                a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<IMyProjector>(a);
            }
            {
                var a = MyAPIGateway.TerminalControls.CreateAction<IMyProjector>(CONTROL_PREFIX + id + "_On");
                a.Name = new StringBuilder(c.Title.String).Append(" - ").Append(c.OnText.String);
                a.Icon = gamePath + @"\Textures\GUI\Icons\Actions\SmallShipSwitchOn.dds";
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, true);
                a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<IMyProjector>(a);
            }
            {
                var a = MyAPIGateway.TerminalControls.CreateAction<IMyProjector>(CONTROL_PREFIX + id + "_Off");
                a.Name = new StringBuilder(c.Title.String).Append(" - ").Append(c.OffText.String);
                a.Icon = gamePath + @"\Textures\GUI\Icons\Actions\LargeShipSwitchOn.dds";
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, false);
                a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<IMyProjector>(a);
            }
        }

        private void CreateAction<T>(IMyTerminalControlCheckbox c,
            bool addToggle = true,
            bool addOnOff = true,
            string iconPack = null,
            string iconToggle = null,
            string iconOn = null,
            string iconOff = null)
        {
            var id = ((IMyTerminalControl)c).Id;
            var name = "(Preview) " + c.Title.String;
            Action<IMyTerminalBlock, StringBuilder> writer = (b, s) => s.Append(c.Getter(b) ? c.OnText.String : c.OffText.String);

            if(iconToggle == null && iconOn == null && iconOff == null)
            {
                var pack = iconPack ?? "";
                var gamePath = MyAPIGateway.Utilities.GamePaths.ContentPath;
                iconToggle = gamePath + @"\Textures\GUI\Icons\Actions\" + pack + "Toggle.dds";
                iconOn = gamePath + @"\Textures\GUI\Icons\Actions\" + pack + "SwitchOn.dds";
                iconOff = gamePath + @"\Textures\GUI\Icons\Actions\" + pack + "SwitchOff.dds";
            }

            if(addToggle)
            {
                var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_Toggle");
                a.Name = new StringBuilder(name).Append(" On/Off");
                a.Icon = iconToggle;
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, !c.Getter(b));
                if(writer != null)
                    a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<T>(a);
            }

            if(addOnOff)
            {
                {
                    var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_On");
                    a.Name = new StringBuilder(name).Append(" On");
                    a.Icon = iconOn;
                    a.ValidForGroups = true;
                    a.Action = (b) => c.Setter(b, true);
                    if(writer != null)
                        a.Writer = writer;

                    MyAPIGateway.TerminalControls.AddAction<T>(a);
                }
                {
                    var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_Off");
                    a.Name = new StringBuilder(name).Append(" Off");
                    a.Icon = iconOff;
                    a.ValidForGroups = true;
                    a.Action = (b) => c.Setter(b, false);
                    if(writer != null)
                        a.Writer = writer;

                    MyAPIGateway.TerminalControls.AddAction<T>(a);
                }
            }
        }

        private void CreateAction<T>(IMyTerminalControlSlider c,
            float defaultValue = 0f, // HACK terminal controls don't have a default value built in...
            float modifier = 1f,
            string iconReset = null,
            string iconIncrease = null,
            string iconDecrease = null,
            bool gridSizeDefaultValue = false) // hacky quick way to get a dynamic default value depending on grid size)
        {
            var id = ((IMyTerminalControl)c).Id;
            var name = c.Title.String;

            if(iconReset == null && iconIncrease == null && iconDecrease == null)
            {
                var gamePath = MyAPIGateway.Utilities.GamePaths.ContentPath;
                iconReset = gamePath + @"\Textures\GUI\Icons\Actions\Reset.dds";
                iconIncrease = gamePath + @"\Textures\GUI\Icons\Actions\Increase.dds";
                iconDecrease = gamePath + @"\Textures\GUI\Icons\Actions\Decrease.dds";
            }

            {
                var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_Reset");
                a.Name = new StringBuilder("(Preview) Reset ").Append(name);
                if(!gridSizeDefaultValue)
                    a.Name.Append(" (").Append(defaultValue.ToString("0.###")).Append(")");
                a.Icon = iconReset;
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, (gridSizeDefaultValue ? b.CubeGrid.GridSize : defaultValue));
                a.Writer = (b, s) => s.Append(c.Getter(b));

                MyAPIGateway.TerminalControls.AddAction<T>(a);
            }
            {
                var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_Increase");
                a.Name = new StringBuilder("(Preview) Increase ").Append(name).Append(" (+").Append(modifier.ToString("0.###")).Append(")");
                a.Icon = iconIncrease;
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, c.Getter(b) + modifier);
                a.Writer = (b, s) => s.Append(c.Getter(b));

                MyAPIGateway.TerminalControls.AddAction<T>(a);
            }
            {
                var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_Decrease");
                a.Name = new StringBuilder("(Preview) Decrease ").Append(name).Append(" (-").Append(modifier.ToString("0.###")).Append(")");
                a.Icon = iconDecrease;
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, c.Getter(b) - modifier);
                a.Writer = (b, s) => s.Append(c.Getter(b).ToString("0.###"));

                MyAPIGateway.TerminalControls.AddAction<T>(a);
            }
        }

        private void CreateAction<T>(IMyTerminalControlCombobox c,
            string[] itemIds = null,
            string[] itemNames = null,
            string icon = null)
        {
            var items = new List<MyTerminalControlComboBoxItem>();
            c.ComboBoxContent.Invoke(items);

            foreach(var item in items)
            {
                var id = (itemIds == null ? item.Value.String : itemIds[item.Key]);

                if(id == null)
                    continue; // item id is null intentionally in the array, this means "don't add action".

                var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id);
                a.Name = new StringBuilder(itemNames == null ? item.Value.String : itemNames[item.Key]);
                if(icon != null)
                    a.Icon = icon;
                a.ValidForGroups = true;

                long key = item.Key;
                a.Action = (b) => c.Setter(b, key);
                //if(writer != null)
                //    a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<T>(a);
            }
        }
        #endregion

        public static IMyPlayer GetPlayerFromSteamId(ulong steamId)
        {
            var players = ProjectorPreviewMod.Instance.Players;
            players.Clear();
            MyAPIGateway.Players.GetPlayers(players);
            IMyPlayer player = null;

            foreach(var p in players)
            {
                if(p.SteamUserId == steamId)
                {
                    player = p;
                    break;
                }
            }

            players.Clear();
            return player;
        }
    }
}