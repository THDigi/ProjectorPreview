using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

// TODO block place/remove sync between real grid and projected grid

#pragma warning disable CS0162 // Unreachable code detected
namespace Digi.ProjectorPreview
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class ProjectorPreviewMod : MySessionComponentBase
    {
        public override void LoadData()
        {
            Instance = this;
            Log.SetUp("Projector Preview", 517675282, "ProjectorPreview");
        }

        public const bool DEBUG = false;

        public static ProjectorPreviewMod Instance = null;
        public static bool IsInProjectorTerminal => Instance.ViewingTerminalOf != null;

        public bool IsInitialized = false;
        public bool IsPlayer = false;
        public float Transparency;
        public float TransparencyHighlight;
        public float TransparencyDecor;
        public float TransparencyDecorHighlight;
        public IMyProjector ViewingTerminalOf = null;
        public IMyTerminalControl ControlPreview = null;
        public IMyTerminalControl ControlUseThisShip = null;
        public IMyTerminalControl ControlRemoveButton = null;
        public readonly IMyTerminalControl[] ControlRotate = new IMyTerminalControl[3];
        public readonly List<IMyTerminalControl> SortedControls = new List<IMyTerminalControl>(); // all controls properly sorted
        public readonly List<IMyTerminalControl> RefreshControls = new List<IMyTerminalControl>(); // controls to be refreshed on certain actions
        private bool createdTerminalControls = false;

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

        private void Init()
        {
            IsInitialized = true;
            IsPlayer = !(MyAPIGateway.Session.IsServer && MyAPIGateway.Utilities.IsDedicated);

            Log.Init();
            UpdateConfigValues();

            if(IsPlayer)
                MyAPIGateway.Gui.GuiControlRemoved += GUIControlRemoved;

            MyAPIGateway.Multiplayer.RegisterMessageHandler(PACKET_ID, PacketReceived);
            MyAPIGateway.TerminalControls.CustomControlGetter += CustomControlGetter;

            // SetUpdateOrder() can't be called in update methods, needs to be done like this
            MyAPIGateway.Utilities.InvokeOnGameThread(() => SetUpdateOrder(MyUpdateOrder.NoUpdate));
        }

        protected override void UnloadData()
        {
            Instance = null;

            try
            {
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

            Log.Close();
        }

        public override void UpdateBeforeSimulation()
        {
            try
            {
                if(!IsInitialized)
                {
                    if(MyAPIGateway.Session == null)
                        return;

                    Init();
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
                    UpdateConfigValues();
                else if(objName.EndsWith("GuiScreenTerminal")) // closing the terminal menu
                    ViewingTerminalOf = null;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void UpdateConfigValues()
        {
            var cfg = MyAPIGateway.Session.Config;
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
                    if(DEBUG)
                        Log.Error($"PacketReceived(); invalid length <= 2; length={bytes.Length}");

                    return;
                }

                var data = MyAPIGateway.Utilities.SerializeFromBinary<PacketData>(bytes); // this will throw errors on invalid data

                if(data == null)
                {
                    if(DEBUG)
                        Log.Error($"PacketReceived(); no deserialized data!");

                    return;
                }

                IMyEntity ent;

                if(!MyAPIGateway.Entities.TryGetEntityById(data.EntityId, out ent) || ent.Closed || !(ent is IMyProjector))
                {
                    if(DEBUG)
                        Log.Info($"PacketReceived(); {data.Type}; {(ent == null ? "can't find entity" : (ent.Closed ? "found closed entity" : "entity not projector"))}");

                    return;
                }

                var logic = ent.GameLogic.GetAs<Projector>();

                if(logic == null)
                {
                    if(DEBUG)
                        Log.Error($"PacketReceived(); {data.Type}; projector doesn't have the gamelogic component!");

                    return;
                }

                switch(data.Type)
                {
                    case PacketType.SETTINGS:
                        {
                            if(data.Settings == null)
                            {
                                if(DEBUG)
                                    Log.Error($"PacketReceived(); {data.Type}; settings are null!");

                                return;
                            }

                            if(DEBUG)
                                Log.Info($"PacketReceived(); Settings; {(MyAPIGateway.Multiplayer.IsServer ? " Relaying to clients;" : "")}Valid!\n{logic.Settings}");

                            logic.UpdateSettings(data.Settings);
                            logic.SaveSettings();

                            if(MyAPIGateway.Multiplayer.IsServer)
                                RelayToClients(((IMyCubeBlock)ent).CubeGrid.GetPosition(), bytes, data.Sender);
                        }
                        break;
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
            if(DEBUG)
                Log.Info("RelaySettingsToClients(block,settings)");

            var data = new PacketData(MyAPIGateway.Multiplayer.MyId, block.EntityId, settings);
            var bytes = MyAPIGateway.Utilities.SerializeToBinary(data);
            RelayToClients(block.CubeGrid.GetPosition(), bytes, data.Sender);
        }

        public static void RelayToClients(Vector3D syncPosition, byte[] bytes, ulong sender)
        {
            if(DEBUG)
                Log.Info("RelayToClients(syncPos,bytes,sender)");

            var localSteamId = MyAPIGateway.Multiplayer.MyId;
            var distSq = MyAPIGateway.Session.SessionSettings.ViewDistance;
            distSq += 1000; // some safety padding
            distSq *= distSq;

            MyAPIGateway.Players.GetPlayers(null, (p) =>
            {
                var id = p.SteamUserId;

                if(id != localSteamId && id != sender && Vector3D.DistanceSquared(p.GetPosition(), syncPosition) <= distSq)
                    MyAPIGateway.Multiplayer.SendMessageTo(PACKET_ID, bytes, p.SteamUserId);

                return false; // avoid adding to the null list
            });
        }
        #endregion

        #region Terminal controls and actions
        private void CustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            try
            {
                ViewingTerminalOf = block as IMyProjector;

                if(ViewingTerminalOf != null)
                {
                    ViewingTerminalOf.RefreshCustomInfo();

                    if(SortedControls.Count == 0)
                        return;

                    controls.Clear();
                    controls.AddList(SortedControls);
                }
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

        public void SetupTerminalControls<T>()
        {
            if(createdTerminalControls)
                return;

            createdTerminalControls = true;

            var tc = MyAPIGateway.TerminalControls;

            // get existing controls before creating the ones for this mod
            List<IMyTerminalControl> vanillaControls;
            tc.GetControls<T>(out vanillaControls);

            #region Top custom controls
            {
                var c = tc.CreateControl<IMyTerminalControlOnOffSwitch, T>(CONTROL_PREFIX + "Enabled");
                c.Title = MyStringId.GetOrCompute("Projector Mode");
                c.Tooltip = MyStringId.GetOrCompute("Change how to display the projection.\nEach mode has its own configurable controls.\n\n(Added by Projector Preview mod)");
                c.OnText = MyStringId.GetOrCompute("Preview");
                c.OffText = MyStringId.GetOrCompute("Build");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Preview_Enabled;
                c.Getter = Projector.UI_Preview_Getter;
                c.Setter = Projector.UI_Preview_Setter;

                CreateActionPreviewMode<T>(c);

                RefreshControls.Add(c);
                ControlPreview = c;
                // don't add to SortedControls here, it's added later

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlCombobox, T>(CONTROL_PREFIX + "UseThisShip");
                c.Title = MyStringId.GetOrCompute("Load this ship...");
                c.Tooltip = MyStringId.GetOrCompute("Use the current ship as the blueprint for this projector.\nThis copies the ship in its current state or with all current blocks fixed/built, it does not automatically update it as it changes.\n\n(Added by Projector Preview mod)");
                c.Enabled = (b) => b.IsWorking;
                c.Setter = Projector.UI_UseThisShip_Action;
                c.Getter = (b) => 0;
                c.ComboBoxContent = (l) => l.AddList(useThisShipOptions);

                CreateAction<T>(c,
                    icon: MyAPIGateway.Utilities.GamePaths.ContentPath + @"\Textures\GUI\Icons\Actions\Start.dds",
                    itemIds: new string[] { null, "UseShip", "UseShipBuilt" },
                    itemNames: new string[] { null, "(Preview) Load this ship - as is", "(Preview) Load this ship - built&fixed" });

                ControlUseThisShip = c;
                // don't add to SortedControls here, it's added later

                tc.AddControl<T>(c);
            }
            #endregion

            #region Sorting and vanilla control editing
            for(int i = 0; i < vanillaControls.Count; ++i)
            {
                var vc = vanillaControls[i];

                if(REFRESH_VANILLA_IDS.Contains(vc.Id))
                    RefreshControls.Add(vc);

                if(vc.Id == REMOVE_BUTTON_ID)
                {
                    // add controls before the button
                    SortedControls.Add(ControlUseThisShip);

                    // add the button
                    ControlRemoveButton = vc;
                    SortedControls.Add(vc);

                    // add controls right after the button
                    SortedControls.Add(tc.CreateControl<IMyTerminalControlSeparator, T>(string.Empty));
                    SortedControls.Add(ControlPreview);
                    SortedControls.Add(tc.CreateControl<IMyTerminalControlSeparator, T>(string.Empty));

                    var label = tc.CreateControl<IMyTerminalControlLabel, T>(string.Empty);
                    label.Label = MyStringId.GetOrCompute("Build mode configuration");
                    SortedControls.Add(label);

                    var button = (IMyTerminalControlButton)vc;

                    // edit the button to be visible when the custom projection is visible as well as normal projection
                    button.Enabled = Projector.UI_RemoveButton_Enabled;

                    // edit the action to remove both the vanilla projection and the custom projection
                    button.Action = Projector.UI_RemoveButton_Action;
                }
                else
                {
                    SortedControls.Add(vc);
                }
            }
            #endregion

            #region Bottom controls
            {
                SortedControls.Add(tc.CreateControl<IMyTerminalControlSeparator, T>(string.Empty));

                var label = tc.CreateControl<IMyTerminalControlLabel, T>(string.Empty);
                label.Label = MyStringId.GetOrCompute("Preview mode configuration");
                SortedControls.Add(label);
                // no reason to add these to the TerminalControls
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(CONTROL_PREFIX + "Scale");
                c.Title = MyStringId.GetOrCompute("Hologram scale");
                c.Tooltip = MyStringId.GetOrCompute("The hologram size in meters.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLogLimits(Projector.UI_Scale_LogLimitMin, Projector.UI_Scale_LogLimitMax);
                c.Getter = Projector.UI_Scale_Getter;
                c.Setter = Projector.UI_Scale_Setter;
                c.Writer = Projector.UI_Scale_Writer;

                CreateAction<T>(c,
                    modifier: 0.1f,
                    gridSizeDefaultValue: true);

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, T>(CONTROL_PREFIX + "StatusMode");
                c.Title = MyStringId.GetOrCompute("Status mode");
                c.Tooltip = MyStringId.GetOrCompute("Colors the projection depending on the status of the projector ship's blocks." +
                                                    "\n" +
                                                    "\nBlack = matches the blueprint." +
                                                    "\nYellow-Orange = integrity above red line but not full health." +
                                                    "\nOrange-Red = integrity below red line and going towards being completely destroyed." +
                                                    "\nRed blinking = missing from ship." +
                                                    "\nTeal = orientation/position is wrong (only shows up in build stage)." +
                                                    "\nDark purple = color is different." +
                                                    "\nLight purple = type is different.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = Projector.UI_Status_Getter;
                c.Setter = Projector.UI_Status_Setter;

                CreateAction<T>(c, iconPack: "Missile");

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, T>(CONTROL_PREFIX + "SeeThrough");
                c.Title = MyStringId.GetOrCompute("See-through armor");
                c.Tooltip = MyStringId.GetOrCompute("Makes armor and decorative blocks more transparent.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = Projector.UI_SeeThrough_Getter;
                c.Setter = Projector.UI_SeeThrough_Setter;

                CreateAction<T>(c, iconPack: "MovingObject");

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(CONTROL_PREFIX + "OffsetX");
                c.Title = MyStringId.GetOrCompute("Offset X");
                c.Tooltip = MyStringId.GetOrCompute("Changes the projection position.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Offset_Getter(b, 0);
                c.Setter = (b, v) => Projector.UI_Offset_Setter(b, v, 0);
                c.SetLimits(-Projector.MIN_MAX_OFFSET, Projector.MIN_MAX_OFFSET);
                c.Writer = (b, s) => Projector.UI_Offset_Writer(b, s, 0);

                CreateAction<T>(c, modifier: 0.1f);

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(CONTROL_PREFIX + "OffsetY");
                c.Title = MyStringId.GetOrCompute("Offset Y");
                c.Tooltip = MyStringId.GetOrCompute("Changes the projection position.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Offset_Getter(b, 1);
                c.Setter = (b, v) => Projector.UI_Offset_Setter(b, v, 1);
                c.SetLimits(-Projector.MIN_MAX_OFFSET, Projector.MIN_MAX_OFFSET);
                c.Writer = (b, s) => Projector.UI_Offset_Writer(b, s, 1);

                CreateAction<T>(c, modifier: 0.1f);

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(CONTROL_PREFIX + "OffsetZ");
                c.Title = MyStringId.GetOrCompute("Offset Z");
                c.Tooltip = MyStringId.GetOrCompute("Changes the projection position.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Offset_Getter(b, 2);
                c.Setter = (b, v) => Projector.UI_Offset_Setter(b, v, 2);
                c.SetLimits(-Projector.MIN_MAX_OFFSET, Projector.MIN_MAX_OFFSET);
                c.Writer = (b, s) => Projector.UI_Offset_Writer(b, s, 2);

                CreateAction<T>(c, modifier: 0.1f);

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(CONTROL_PREFIX + "RotateX");
                c.Title = MyStringId.GetOrCompute("Rotate X / Pitch");
                c.Tooltip = MyStringId.GetOrCompute("Rotate projection along the X axis. This can be turned into constant spinning by checking the checkbox below.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLimits(-Projector.MIN_MAX_ROTATE, Projector.MIN_MAX_ROTATE);
                c.Getter = (b) => Projector.UI_Rotate_Getter(b, 0);
                c.Setter = (b, v) => Projector.UI_Rotate_Setter(b, v, 0);
                c.Writer = (b, s) => Projector.UI_Rotate_Writer(b, s, 0);

                CreateAction<T>(c, modifier: 0.1f);

                SortedControls.Add(c);
                RefreshControls.Add(c);
                ControlRotate[0] = c;

                tc.AddControl<T>(c);
            }
            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, T>(CONTROL_PREFIX + "SpinX");
                c.Title = MyStringId.GetOrCompute("Spin X / Pitch");
                c.Tooltip = MyStringId.GetOrCompute("Makes the Rotate X / Pitch slider act as spin speed.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Spin_Getter(b, 0);
                c.Setter = (b, v) => Projector.UI_Spin_Setter(b, v, 0);

                CreateAction<T>(c);

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(CONTROL_PREFIX + "RotateY");
                c.Title = MyStringId.GetOrCompute("Rotate Y / Yaw");
                c.Tooltip = MyStringId.GetOrCompute("Rotate projection along the Y axis. This can be turned into constant spinning by checking the checkbox below.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLimits(-Projector.MIN_MAX_ROTATE, Projector.MIN_MAX_ROTATE);
                c.Getter = (b) => Projector.UI_Rotate_Getter(b, 1);
                c.Setter = (b, v) => Projector.UI_Rotate_Setter(b, v, 1);
                c.Writer = (b, s) => Projector.UI_Rotate_Writer(b, s, 1);

                CreateAction<T>(c, modifier: 0.1f);

                SortedControls.Add(c);
                RefreshControls.Add(c);
                ControlRotate[1] = c;

                tc.AddControl<T>(c);
            }
            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, T>(CONTROL_PREFIX + "SpinY");
                c.Title = MyStringId.GetOrCompute("Spin Y / Yaw");
                c.Tooltip = MyStringId.GetOrCompute("Makes the Rotate Y / Yaw slider act as spin speed.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Spin_Getter(b, 1);
                c.Setter = (b, v) => Projector.UI_Spin_Setter(b, v, 1);

                CreateAction<T>(c);

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }

            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(CONTROL_PREFIX + "RotateZ");
                c.Title = MyStringId.GetOrCompute("Rotate Z / Roll");
                c.Tooltip = MyStringId.GetOrCompute("Rotate projection along the Z axis. This can be turned into constant spinning by checking the checkbox below.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.SetLimits(-Projector.MIN_MAX_ROTATE, Projector.MIN_MAX_ROTATE);
                c.Getter = (b) => Projector.UI_Rotate_Getter(b, 2);
                c.Setter = (b, v) => Projector.UI_Rotate_Setter(b, v, 2);
                c.Writer = (b, s) => Projector.UI_Rotate_Writer(b, s, 2);

                CreateAction<T>(c, modifier: 0.1f);

                SortedControls.Add(c);
                RefreshControls.Add(c);
                ControlRotate[2] = c;

                tc.AddControl<T>(c);
            }
            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, T>(CONTROL_PREFIX + "SpinZ");
                c.Title = MyStringId.GetOrCompute("Spin Z / Roll");
                c.Tooltip = MyStringId.GetOrCompute("Makes the Rotate Z / Roll slider act as spin speed.");
                c.SupportsMultipleBlocks = true;
                c.Enabled = Projector.UI_Generic_Enabled;
                c.Getter = (b) => Projector.UI_Spin_Getter(b, 2);
                c.Setter = (b, v) => Projector.UI_Spin_Setter(b, v, 2);

                CreateAction<T>(c);

                SortedControls.Add(c);
                RefreshControls.Add(c);

                tc.AddControl<T>(c);
            }
            #endregion
        }

        private void CreateActionPreviewMode<T>(IMyTerminalControlOnOffSwitch c)
        {
            var id = ((IMyTerminalControl)c).Id;
            var gamePath = MyAPIGateway.Utilities.GamePaths.ContentPath;
            Action<IMyTerminalBlock, StringBuilder> writer = (b, s) => s.Append(c.Getter(b) ? c.OnText : c.OffText);

            {
                var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_Toggle");
                a.Name = new StringBuilder(c.Title.String).Append(" - ").Append(c.OnText.String).Append("/").Append(c.OffText.String);
                a.Icon = gamePath + @"\Textures\GUI\Icons\Actions\SmallShipToggle.dds";
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, !c.Getter(b));
                a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<T>(a);
            }
            {
                var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_On");
                a.Name = new StringBuilder(c.Title.String).Append(" - ").Append(c.OnText.String);
                a.Icon = gamePath + @"\Textures\GUI\Icons\Actions\SmallShipSwitchOn.dds";
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, true);
                a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<T>(a);
            }
            {
                var a = MyAPIGateway.TerminalControls.CreateAction<T>(CONTROL_PREFIX + id + "_Off");
                a.Name = new StringBuilder(c.Title.String).Append(" - ").Append(c.OffText.String);
                a.Icon = gamePath + @"\Textures\GUI\Icons\Actions\LargeShipSwitchOn.dds";
                a.ValidForGroups = true;
                a.Action = (b) => c.Setter(b, false);
                a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<T>(a);
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
            Action<IMyTerminalBlock, StringBuilder> writer = (b, s) => s.Append(c.Getter(b) ? c.OnText : c.OffText);

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
                a.Action = (b) => c.Setter(b, item.Key);
                //if(writer != null)
                //    a.Writer = writer;

                MyAPIGateway.TerminalControls.AddAction<T>(a);
            }
        }
        #endregion
    }
}