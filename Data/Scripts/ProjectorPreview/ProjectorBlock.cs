using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ParallelTasks;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Lights;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

#pragma warning disable CS0162 // Unreachable code detected
namespace Digi.ProjectorPreview
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Projector), useEntityUpdate: false)]
    public class Projector : MyGameLogicComponent
    {
        public ProjectorPreviewModSettings Settings = new ProjectorPreviewModSettings();
        public MyObjectBuilder_CubeGrid OriginalBlueprint = null;
        public MyCubeGrid CustomProjection = null;
        public float LargestGridLength = 2.5f;
        public float ProjectionScale = 1f; // cached projection scale calculated from the slider value and LargestGridLength

        private bool initialized = false;
        private IMyProjector projector = null;
        private byte previewCooldown = 0;
        private byte useThisShipCooldown = 0;
        private byte saveCountdown = 0;
        private bool projectionVisible = true;
        private long lastCheckedProjectionId = 0;
        private Vector3 currentRotationRad = Vector3.Zero;
        private Vector3D relativeOffset = Vector3D.Zero;
        private bool needsMatrixUpdate = false;
        private bool needsConstantMatrixUpdate = false;
        private Vector3[] corners = null;
        private MyLight[] lights = null;
        private int skipDebug = 0;

        // used by status mode
        private bool blocksNeedRefresh = false;
        private bool resetColors = false;
        private int blockIndex = 0;
        private int blocksDamaged = 0;
        private int blocksUnfinished = 0;
        private int blocksMissing = 0;
        private int blocksWrongType = 0;
        private int blocksWrongColor = 0;
        private int blocksWrongRotation = 0;
        private int blocksBetterIntegrity = 0;
        private byte blinkTicker = 0;
        private bool blinkMemory = false;
        private Dictionary<Vector3I, MyTuple<IMySlimBlock, Vector3>> originalColors = null;

        private long receiveTimeOut = 0;
        private HashSet<ulong> playersToReceive = null;

        private Task blueprintLoadTask;
        private bool blueprintLoadTaskRunning = false;

        private Task blueprintSerializeTask;
        private bool blueprintSerializeTaskRunning = false;

        private Task spawnTask;
        private bool spawnTaskRunning = false;

        #region Constants
        private enum BlockStatus : byte { MISSING, WRONG_TYPE, WRONG_ROTATION }

        private const float PROJECTION_RANGE_ADD_SQ = 10 * 10; // squared range to add to the multiplier, effectively a minimum view distance
        private const float PROJECTION_RANGE_SCALE_SQ = 50 * 50; // squared range at which projection vanishes. this value is multiplied by the projection scale squared.
        private const byte COOLDOWN_PREVIEW = 30; // cooldown after toggling preview mode, in ticks
        private const byte COOLDOWN_USETHISSHIP = 60 * 3; // cooldown after pressing the "Use this ship" button, in ticks
        private const byte SETTINGS_SAVE_COUNTDOWN = 60 * 3; // ticks to wait before saving or synchronizing settings after a setting was touched
        private const int RECEIVE_TIMEOUT = 60 * 30; // max amount of time to wait for players to respond to receiving the blueprint before setting it to null.
        private const int BLOCKS_PER_UPDATE = 10000; // how many blocks to update color/transparency to in an update
        private const byte BLINK_FREQUENCY = 60; // ticks to wait between blink changes from on to off or vice versa
        private const int BLINK_MAX_BLOCKS = 1000; // ship no longer blinks after this many missing blocks

        public const float MIN_SCALE = 0.1f; // Scale slider min/max
        public const float MAX_SCALE = 10f;
        public const float MIN_MAX_OFFSET = 10f; // Offset slider min/max
        public const float MIN_MAX_ROTATE = 360f; // Rotate slider min/max
        public const float MIN_LIGHTINTENSITY = 0f; // Light intensity slider
        public const float MAX_LIGHTINTENSITY = 5f;

        private readonly Vector3 STATUS_COLOR_NORMAL = new Color(0, 0, 0).ColorToHSVDX11();
        private readonly Vector3 STATUS_COLOR_BETTER = new Color(0, 255, 0).ColorToHSVDX11();
        private readonly Vector3 STATUS_COLOR_MISSING = new Color(255, 0, 0).ColorToHSVDX11();
        private readonly Vector3 STATUS_COLOR_WRONG_TYPE = new Color(255, 0, 255).ColorToHSVDX11();
        private readonly Vector3 STATUS_COLOR_WRONG_COLOR = new Color(55, 0, 155).ColorToHSVDX11();
        private readonly Vector3 STATUS_COLOR_WRONG_ROTATION = new Color(0, 155, 255).ColorToHSVDX11();

        private const float LIGHT_DETAIL_RADIUS_START = 1f;
        private const float LIGHT_DETAIL_RADIUS_MUL = 1f;
        private const float LIGHT_DETAIL_FALLOFF = 1f;
        private const float LIGHT_DETAIL_INTENSITY = 0.2f;

        private const float LIGHT_CENTER_RADIUS_START = 2f;
        private const float LIGHT_CENTER_RADIUS_MUL = 2f;
        private const float LIGHT_CENTER_FALLOFF = 1f;
        private const float LIGHT_CENTER_INTENSITY = 0.3f;

        // same light as center but the detail lights are hidden so this needs tweaked to look similar
        private const float LIGHT_FAR_RADIUS_START = 3f;
        private const float LIGHT_FAR_RADIUS_MUL = 1.7f;
        private const float LIGHT_FAR_FALLOFF = LIGHT_CENTER_FALLOFF;
        private const float LIGHT_FAR_INTENSITY = 1.0f;
        private const float LIGHT_CENTER_VIEW_RANGE_SQ = 300 * 300; // squared range at which light gets turned off. this value is multiplied by the projection scale squared.
        #endregion

        #region Settings properties
        public bool PreviewMode
        {
            get { return Settings.Enabled; }
            set
            {
                Settings.Enabled = value;
                previewCooldown = COOLDOWN_PREVIEW;
                RefreshControls(refeshCustomInfo: true);
            }
        }

        public float Scale
        {
            get { return Settings.Scale; }
            set
            {
                Settings.Scale = (float)Math.Round(MathHelper.Clamp(value, MIN_SCALE, Math.Min(LargestGridLength, MAX_SCALE)), 3);
                ComputeProjectionScale();
                needsMatrixUpdate = true;
            }
        }

        public bool StatusMode
        {
            get { return Settings.Status; }
            set
            {
                Settings.Status = value;
                blocksNeedRefresh = true;

                if(!value)
                    ResetBlockStatus(resetColors: true);
            }
        }

        public bool SeeThroughMode
        {
            get { return Settings.SeeThrough; }
            set
            {
                Settings.SeeThrough = value;
                blocksNeedRefresh = true;
            }
        }

        public Vector3 Offset => Settings.Offset;

        public void SetOffset(float value, int axis)
        {
            Settings.Offset.SetDim(axis, MathHelper.Clamp((float)Math.Round(value, 2), -MIN_MAX_OFFSET, MIN_MAX_OFFSET));
            needsMatrixUpdate = true;
        }

        public Vector3 Rotate => Settings.RotateRad;

        public void SetRotate(float value, int axis)
        {
            Settings.RotateRad.SetDim(axis, MathHelper.Clamp(value, -MIN_MAX_ROTATE, MIN_MAX_ROTATE));
            CheckNeedsConstantMatrixUpdate();
        }

        public SpinFlags Spin => Settings.Spin;

        public void SetSpin(bool value, int axis)
        {
            var flag = (SpinFlags)(1 << axis);

            if(value)
                Settings.Spin |= flag;
            else
                Settings.Spin &= ~flag;

            CheckNeedsConstantMatrixUpdate();

            if(ProjectorPreviewMod.IsInProjectorTerminal)
                ProjectorPreviewMod.Instance?.ControlRotate[axis]?.UpdateVisual();
        }

        public float LightIntensity
        {
            get { return Settings.LightIntensity; }
            set
            {
                Settings.LightIntensity = (float)Math.Round(MathHelper.Clamp(value, MIN_LIGHTINTENSITY, MAX_LIGHTINTENSITY), 3);
                needsMatrixUpdate = true;
            }
        }

        private void CheckNeedsConstantMatrixUpdate()
        {
            needsMatrixUpdate = true;
            needsConstantMatrixUpdate = false;

            for(int i = 0; i < 3; ++i)
            {
                if(Settings.Spin.IsAxisSet(i) && Math.Abs(Settings.RotateRad.GetDim(i)) > 0.0001f)
                {
                    needsConstantMatrixUpdate = true;
                    break;
                }
            }
        }

        public void UpdateSettings(ProjectorPreviewModSettings newSettings)
        {
            PreviewMode = newSettings.Enabled;
            Scale = newSettings.Scale;
            StatusMode = newSettings.Status;
            SeeThroughMode = newSettings.SeeThrough;

            for(int i = 0; i < 3; ++i)
                SetOffset(newSettings.Offset.GetDim(i), i);

            for(int i = 0; i < 3; ++i)
                SetRotate(newSettings.RotateRad.GetDim(i), i);

            for(int i = 0; i < 3; ++i)
                SetSpin(newSettings.Spin.IsAxisSet(i), i);
        }
        #endregion

        #region Terminal Controls' methods
        public static bool UI_Generic_Enabled(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? false : logic.PreviewMode && logic.projector.IsWorking);
        }

        #region UseThisShip button
        public static bool UI_UseThisShip_Enabled(IMyTerminalBlock block)
        {
            var logic = block.GameLogic?.GetAs<Projector>();
            return (logic == null ? false : logic.projector.IsWorking && logic.useThisShipCooldown <= 0);
        }

        public static void UI_UseThisShip_Action(IMyTerminalBlock block, long value)
        {
            try
            {
                var logic = block.GameLogic?.GetAs<Projector>();

                if(logic == null)
                    return;

                if(ProjectorPreviewMod.Debug)
                    Log.Info($"UI_UseThisShip_Action(); button pressed");

                logic.UseThisShip_Sender(value == 2);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void UseThisShip_Sender(bool fix)
        {
            if(useThisShipCooldown > 0)
                return;

            if(ProjectorPreviewMod.Debug)
                Log.Info($"UseThisShip_Sender({fix})");

            useThisShipCooldown = COOLDOWN_USETHISSHIP;

            if(MyAPIGateway.Multiplayer.IsServer)
            {
                UseThisShip_Internal(fix);
                // don't relay to clients, SetProjectedGrid() syncs this
            }
            else
            {
                var bytes = MyAPIGateway.Utilities.SerializeToBinary(new PacketData(MyAPIGateway.Multiplayer.MyId, projector.EntityId, (fix ? PacketType.USE_THIS_FIX : PacketType.USE_THIS_AS_IS)));
                MyAPIGateway.Multiplayer.SendMessageToServer(ProjectorPreviewMod.PACKET_ID, bytes);
            }
        }

        public void UseThisShip_Receiver(bool fix)
        {
            if(ProjectorPreviewMod.Debug)
                Log.Info($"UseThisShip_Receiver({fix})");

            UseThisShip_Internal(fix);
        }

        // only called server side
        private void UseThisShip_Internal(bool fix)
        {
            if(projector.ProjectedGrid != null)
                projector.SetProjectedGrid(null);

            if(projector.Storage != null)
                projector.Storage.Remove(ProjectorPreviewMod.Instance.BLUEPRINT_GUID);

            var bp = (MyObjectBuilder_CubeGrid)projector.CubeGrid.GetObjectBuilder();

            if(fix)
            {
                bp.Skeleton?.Clear(); // remove deformation

                foreach(var block in bp.CubeBlocks)
                {
                    block.IntegrityPercent = 1f;
                    block.BuildPercent = 1f;
                }
            }

            projector.SetProjectedGrid(bp);

            // TODO: configure projector vanilla sliders to place the projection exactly overlayed?
#if false
            var projectorObj = bp.CubeBlocks.Find(b => b.EntityId == projector.EntityId);

            projector.SetValueBool("KeepProjection", true);
            projector.ProjectionOffset = ???;
            projector.ProjectionRotation = ???;
            projector.UpdateOffsetAndRotation();
#endif
        }
        #endregion

        #region Remove blueprint button
        public static bool UI_RemoveButton_Enabled(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? false : (logic.projector.IsProjecting || logic.OriginalBlueprint != null));
        }

        public static void UI_RemoveButton_Action(IMyTerminalBlock block)
        {
            try
            {
                if(ProjectorPreviewMod.Debug)
                    Log.Info($"UI_RemoveButton_Action(); button pressed");

                GetLogic(block)?.RemoveBlueprints_Sender();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void RemoveBlueprints_Sender()
        {
            if(ProjectorPreviewMod.Debug)
                Log.Info($"RemoveBlueprints_Sender()");

            var bytes = MyAPIGateway.Utilities.SerializeToBinary(new PacketData(MyAPIGateway.Multiplayer.MyId, projector.EntityId, PacketType.REMOVE));

            if(MyAPIGateway.Multiplayer.IsServer)
                ProjectorPreviewMod.RelayToClients(projector.CubeGrid.GetPosition(), bytes, MyAPIGateway.Multiplayer.MyId);
            else
                MyAPIGateway.Multiplayer.SendMessageToServer(ProjectorPreviewMod.PACKET_ID, bytes);

            RemoveBlueprints_Internal();
        }

        public void RemoveBlueprints_Receiver(byte[] bytes, ulong sender)
        {
            if(ProjectorPreviewMod.Debug)
                Log.Info($"RemoveBlueprints_Receiver() :: {projector.CustomName} ({projector.CubeGrid.GridSizeEnum})");

            RemoveBlueprints_Internal();

            if(MyAPIGateway.Multiplayer.IsServer)
                ProjectorPreviewMod.RelayToClients(projector.CubeGrid.GetPosition(), bytes, sender);
        }

        private void RemoveBlueprints_Internal()
        {
            RemoveCustomProjection(removeLights: true, removeOriginalBP: true);

            if(MyAPIGateway.Multiplayer.IsServer)
                projector.SetProjectedGrid(null); // it's synchronized, no reason to do it clientside to spam server and clients with requests

            RefreshControls(refreshRemoveButton: true, refeshCustomInfo: true);
        }
        #endregion

        #region UI - Preview mode
        public static bool UI_Preview_Enabled(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? false : logic.previewCooldown <= 0);
        }

        public static bool UI_Preview_Getter(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? false : logic.PreviewMode);
        }

        public static void UI_Preview_Setter(IMyTerminalBlock block, bool value)
        {
            var logic = GetLogic(block);

            if(logic != null && logic.previewCooldown <= 0)
            {
                logic.PreviewMode = value;
                logic.PropertyChanged();
            }
        }
        #endregion

        #region UI - Scale
        public static float UI_Scale_Getter(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? 0 : logic.Scale);
        }

        public static void UI_Scale_Setter(IMyTerminalBlock block, float value)
        {
            var logic = GetLogic(block);

            if(logic != null)
            {
                logic.Scale = value;
                logic.PropertyChanged();
            }
        }

        public static float UI_Scale_LogLimitMin(IMyTerminalBlock block)
        {
            return MIN_SCALE;
        }

        public static float UI_Scale_LogLimitMax(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? 0 : Math.Min(logic.LargestGridLength, MAX_SCALE));
        }

        public static void UI_Scale_Writer(IMyTerminalBlock block, StringBuilder writer)
        {
            var logic = GetLogic(block);

            if(logic == null)
                return;

            if(logic.CustomProjection != null)
                writer.AppendFormat("{0:0.00}m (1:{1:0.000})", logic.Scale, logic.LargestGridLength / logic.Scale);
            else
                writer.AppendFormat("{0:0.00}m", logic.Scale);
        }
        #endregion

        #region UI - Status
        public static bool UI_Status_Getter(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? false : logic.StatusMode);
        }

        public static void UI_Status_Setter(IMyTerminalBlock block, bool value)
        {
            var logic = GetLogic(block);

            if(logic != null)
            {
                logic.StatusMode = value;
                logic.PropertyChanged();
            }
        }
        #endregion

        #region UI - SeeThrough
        public static bool UI_SeeThrough_Getter(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? false : logic.SeeThroughMode);
        }

        public static void UI_SeeThrough_Setter(IMyTerminalBlock block, bool value)
        {
            var logic = GetLogic(block);

            if(logic != null)
            {
                logic.SeeThroughMode = value;
                logic.PropertyChanged();
            }
        }
        #endregion

        #region UI - Offsets
        public static float UI_Offset_Getter(IMyTerminalBlock block, int axis)
        {
            var logic = GetLogic(block);
            return (logic == null ? 0f : logic.Offset.GetDim(axis));
        }

        public static void UI_Offset_Setter(IMyTerminalBlock block, float value, int axis)
        {
            var logic = GetLogic(block);

            if(logic != null)
            {
                logic.SetOffset(value, axis);
                logic.PropertyChanged();
            }
        }

        public static void UI_Offset_Writer(IMyTerminalBlock block, StringBuilder writer, int axis)
        {
            try
            {
                var logic = GetLogic(block);

                if(logic != null)
                {
                    writer.AppendFormat("{0:0.00}m", logic.Offset.GetDim(axis));
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        #endregion

        #region UI - Rotate
        public static float UI_Rotate_Getter(IMyTerminalBlock block, int axis)
        {
            var logic = GetLogic(block);
            return (logic == null ? 0f : MathHelper.ToDegrees(logic.Rotate.GetDim(axis)));
        }

        public static void UI_Rotate_Setter(IMyTerminalBlock block, float value, int axis)
        {
            var logic = GetLogic(block);

            if(logic != null)
            {
                logic.SetRotate(MathHelper.ToRadians(value), axis);
                logic.PropertyChanged();
            }
        }

        public static void UI_Rotate_Writer(IMyTerminalBlock block, StringBuilder writer, int axis)
        {
            try
            {
                var logic = GetLogic(block);

                if(logic == null)
                    return;

                var deg = (float)Math.Round(MathHelper.ToDegrees(logic.Rotate.GetDim(axis)), 2);

                if(Math.Abs(deg) <= 0.001f)
                {
                    writer.Append("Off");
                }
                else
                {
                    if(logic.Spin.IsAxisSet(axis))
                        writer.AppendFormat("Spinning {0:0.00}°/s", deg);
                    else
                        writer.AppendFormat("Fixed at {0:0.00}°", deg);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        #endregion

        #region UI - Spin
        public static bool UI_Spin_Getter(IMyTerminalBlock block, int axis)
        {
            var logic = GetLogic(block);
            return (logic == null ? false : logic.Spin.IsAxisSet(axis));
        }

        public static void UI_Spin_Setter(IMyTerminalBlock block, bool value, int axis)
        {
            var logic = GetLogic(block);

            if(logic != null)
            {
                logic.SetSpin(value, axis);
                logic.PropertyChanged();
            }
        }
        #endregion

        #region UI - LightIntensity
        public static float UI_LightIntensity_Getter(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? 0f : logic.LightIntensity);
        }

        public static void UI_LightIntensity_Setter(IMyTerminalBlock block, float value)
        {
            var logic = GetLogic(block);

            if(logic != null)
            {
                logic.LightIntensity = value;
                logic.PropertyChanged();
            }
        }

        public static void UI_LightIntensity_Writer(IMyTerminalBlock block, StringBuilder writer)
        {
            try
            {
                var logic = GetLogic(block);

                if(logic == null)
                    return;

                if(logic.LightIntensity <= 0.0000001f)
                {
                    writer.Append("Off");
                }
                else
                {
                    writer.AppendFormat("{0:0.00}", logic.LightIntensity);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        #endregion

        private static Projector GetLogic(IMyTerminalBlock b)
        {
            return b?.GameLogic?.GetAs<Projector>();
        }

        private void PropertyChanged()
        {
            if(saveCountdown == 0)
                saveCountdown = SETTINGS_SAVE_COUNTDOWN;
        }

        private void RefreshControls(bool refreshRemoveButton = false, bool refeshCustomInfo = false)
        {
            if(ProjectorPreviewMod.IsInProjectorTerminal)
            {
                foreach(var c in ProjectorPreviewMod.Instance.RefreshControls)
                    c.UpdateVisual();

                if(refreshRemoveButton)
                    ProjectorPreviewMod.Instance.ControlRemoveButton?.UpdateVisual();

                if(refeshCustomInfo)
                    projector.RefreshCustomInfo();
            }
        }
        #endregion

        #region Init and close
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            // Block spawned, called async.
            // NOTE: For safety, do not do anything more than setting NeedsUpdate in this method.
            NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME;
        }

        private void Initialize()
        {
            projector = (IMyProjector)Entity;

            if(projector.CubeGrid.Physics == null)
            {
                NeedsUpdate = MyEntityUpdateEnum.NONE;
                return;
            }

            Settings.Scale = projector.CubeGrid.GridSize;

            if(ProjectorPreviewMod.Instance.IsPlayer)
            {
                lights = new MyLight[9];
                corners = new Vector3[8];

                projector.AppendingCustomInfo += CustomInfo;
                projector.CubeGrid.PositionComp.OnPositionChanged += GridMoved;

                NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            }

            if(!LoadSettingsAndBlueprint())
            {
                if(MyAPIGateway.Multiplayer.IsServer)
                    ReadLegacyStorage();
            }

            ProjectorPreviewMod.Instance.SetupTerminalControls<IMyProjector>();
        }

        public override void Close()
        {
            try
            {
                if(projector?.CubeGrid?.Physics == null)
                    return;

                if(ProjectorPreviewMod.Instance.IsPlayer)
                {
                    projector.AppendingCustomInfo -= CustomInfo;
                    projector.CubeGrid.PositionComp.OnPositionChanged -= GridMoved;

                    RemoveCustomProjection(removeLights: true, removeOriginalBP: false);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        #endregion

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(!initialized)
                {
                    if(!ProjectorPreviewMod.Instance.IsInitialized) // wait until session component intialized
                        return;

                    initialized = true;
                    Initialize();
                }

                #region Control refresh countdowns and blink ticker, only for players
                if(ProjectorPreviewMod.Instance.IsPlayer)
                {
                    if(previewCooldown > 0 && --previewCooldown <= 0)
                    {
                        if(ProjectorPreviewMod.IsInProjectorTerminal)
                            ProjectorPreviewMod.Instance?.ControlPreview?.UpdateVisual();
                    }

                    if(useThisShipCooldown > 0 && --useThisShipCooldown <= 0)
                    {
                        if(ProjectorPreviewMod.IsInProjectorTerminal)
                            ProjectorPreviewMod.Instance?.ControlUseThisShip?.UpdateVisual();
                    }

                    if(Settings.Status)
                    {
                        if(blocksMissing > BLINK_MAX_BLOCKS)
                        {
                            blinkMemory = false;
                        }
                        else if(++blinkTicker >= BLINK_FREQUENCY)
                        {
                            blinkTicker = 0;
                            blinkMemory = !blinkMemory;
                        }
                    }
                }
                #endregion

                #region Countdown to sync or save settings
                if(saveCountdown > 0 && --saveCountdown <= 0)
                {
                    SaveSettings();

                    if(MyAPIGateway.Multiplayer.IsServer)
                    {
                        ProjectorPreviewMod.RelaySettingsToClients(projector, Settings); // update clients with server's settings
                    }
                    else // client, send settings to server
                    {
                        var bytes = MyAPIGateway.Utilities.SerializeToBinary(new PacketData(MyAPIGateway.Multiplayer.MyId, projector.EntityId, Settings));
                        MyAPIGateway.Multiplayer.SendMessageToServer(ProjectorPreviewMod.PACKET_ID, bytes);
                    }
                }
                #endregion

                if(MyAPIGateway.Multiplayer.IsServer && receiveTimeOut > 0 && --receiveTimeOut <= 0)
                {
                    if(ProjectorPreviewMod.Debug)
                        Log.Info($"Timeout expired, setting vanilla projected grid to null.");

                    SetVanillaProjectedGridNull();
                }

                if(projector.IsWorking)
                {
                    UpdateProjection();

                    if(ProjectorPreviewMod.Debug)
                    {
                        if(++skipDebug > 60 * 3)
                        {
                            skipDebug = 0;
                            Log.Info($"UpdateAfterSimulation() Status: {projector.CustomName} ({projector.CubeGrid.GridSizeEnum}) - VanillaBP={(projector.ProjectedGrid == null ? "(N/A)" : projector.ProjectedGrid.CustomName ?? "(Unnamed)")}; CustomBP={(spawnTaskRunning ? "(Spawning...)" : (OriginalBlueprint == null ? "(N/A)" : OriginalBlueprint.DisplayName ?? "(Unnamed)"))}");
                        }
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void UpdateProjection()
        {
            if(Settings.Enabled)
            {
                #region Load from vanilla blueprint
                if(projector.ProjectedGrid != null)
                {
                    if(ProjectorPreviewMod.Debug)
                        Log.Info($"UpdateProjection() :: vanilla projection detected");

                    if(blueprintSerializeTaskRunning)
                        return;

                    if(lastCheckedProjectionId == projector.ProjectedGrid.EntityId)
                    {
                        if(ProjectorPreviewMod.Debug)
                            Log.Info($"UpdateProjection() :: same projection, ignoring...");

                        return;
                    }

                    lastCheckedProjectionId = projector.ProjectedGrid.EntityId;

                    if(ProjectorPreviewMod.Debug)
                        Log.Info($"UpdateProjection() :: set blueprint; name={projector.ProjectedGrid.CustomName}");

                    RemoveCustomProjection(removeLights: false, removeOriginalBP: true);
                    SetLargestGridLength(projector.ProjectedGrid);
                    RefreshControls(refreshRemoveButton: true);
                    OriginalBlueprint = (MyObjectBuilder_CubeGrid)projector.ProjectedGrid.GetObjectBuilder(true);


                    // HACK workaround SetProjectedGrid not being settable only clientside.
                    // This is required because it takes a while for the grid data to be sent and clients to actually spawn the grid.
                    // Knowing that and calling SetProjectedGrid(null) will make some if not all clients lose the blueprint.
                    // This makes a list of players that supposedly have the projector in range and can receive blueprints from it.
                    // Then then each player sends a packet back to let the server know they spawned the blueprint.
                    // When all players confirmed the blueprint OR the timeout passed, only then the server sets the vanilla projected grid to null.
                    if(MyAPIGateway.Multiplayer.IsServer)
                    {
                        if(!MyAPIGateway.Utilities.IsDedicated && MyAPIGateway.Players.Count == 1)
                        {
                            projector.SetProjectedGrid(null);
                        }
                        else
                        {
                            if(playersToReceive == null)
                                playersToReceive = new HashSet<ulong>();
                            else
                                playersToReceive.Clear();

                            var worldViewRangeSq = MyAPIGateway.Session.SessionSettings.ViewDistance;
                            worldViewRangeSq *= worldViewRangeSq;

                            if(ProjectorPreviewMod.Debug)
                                Log.Info($"playersToReceive defined:");

                            var players = ProjectorPreviewMod.Instance.Players;
                            players.Clear();
                            MyAPIGateway.Players.GetPlayers(players);

                            foreach(var p in players)
                            {
                                if(p.SteamUserId != MyAPIGateway.Multiplayer.MyId && Vector3D.DistanceSquared(p.GetPosition(), projector.GetPosition()) <= worldViewRangeSq)
                                {
                                    playersToReceive.Add(p.SteamUserId);

                                    if(ProjectorPreviewMod.Debug)
                                        Log.Info($" - {p.DisplayName} ({p.SteamUserId})");
                                }
                            }

                            players.Clear();

                            if(playersToReceive.Count == 0)
                            {
                                if(ProjectorPreviewMod.Debug)
                                    Log.Info($"No players nearby, setting vanilla projected grid to null without waiting.");

                                SetVanillaProjectedGridNull();
                            }
                            else
                            {
                                receiveTimeOut = RECEIVE_TIMEOUT;
                            }
                        }
                    }
                    else
                    {
                        var packet = new PacketData(MyAPIGateway.Multiplayer.MyId, projector.EntityId, PacketType.RECEIVED_BP);
                        var bytes = MyAPIGateway.Utilities.SerializeToBinary(packet);
                        MyAPIGateway.Multiplayer.SendMessageToServer(ProjectorPreviewMod.PACKET_ID, bytes);
                    }

                    // serialize the blueprint in another thread and store it in the block.
                    blueprintSerializeTaskRunning = true;
                    blueprintSerializeTask = MyAPIGateway.Parallel.Start(BlueprintThread.Run, BlueprintSerializeCompleted, new BlueprintThread.Data(OriginalBlueprint));
                    return;
                }
                #endregion

                #region Update projection matrix and lights; only for players
                if(ProjectorPreviewMod.Instance.IsPlayer && (needsConstantMatrixUpdate || needsMatrixUpdate))
                {
                    needsMatrixUpdate = false;

                    var l = lights[0];
                    bool isLightVisible = (l != null && l.LightOn);
                    bool isProjectionVisible = (CustomProjection != null && projectionVisible);
                    var relativeOffset = (isLightVisible || isProjectionVisible ? ((Settings.Offset.X * projector.WorldMatrix.Right) + ((Settings.Offset.Y + projector.CubeGrid.GridSize) * projector.WorldMatrix.Up) + (Settings.Offset.Z * projector.WorldMatrix.Backward)) : Vector3D.Zero);

                    if(isProjectionVisible)
                    {
                        var matrix = projector.WorldMatrix;

                        for(int i = 0; i < 3; i++)
                        {
                            var rad = Settings.RotateRad.GetDim(i);

                            if(Math.Abs(rad) <= 0.0001f)
                            {
                                rad = 0;
                            }
                            else
                            {
                                if(Settings.Spin.IsAxisSet(i))
                                    rad = MathHelper.WrapAngle(currentRotationRad.GetDim(i) + (rad * (1f / 60f)));
                            }

                            currentRotationRad.SetDim(i, rad);
                        }

                        matrix.Translation = Vector3D.Zero; // needed to avoid weird rotation

                        if(Math.Abs(currentRotationRad.X) > 0.001f)
                            matrix *= MatrixD.CreateFromAxisAngle(matrix.Right, currentRotationRad.X);

                        if(Math.Abs(currentRotationRad.Y) > 0.001f)
                            matrix *= MatrixD.CreateFromAxisAngle(matrix.Up, currentRotationRad.Y);

                        if(Math.Abs(currentRotationRad.Z) > 0.001f)
                            matrix *= MatrixD.CreateFromAxisAngle(matrix.Backward, currentRotationRad.Z);

                        MatrixD.Rescale(ref matrix, ProjectionScale);

                        matrix.Translation = Vector3D.Zero; // needed to avoid weirdness with the transformation
                        var centerLocal = (CustomProjection.Max + CustomProjection.Min) * CustomProjection.GridSizeHalf;
                        matrix.Translation = (projector.WorldMatrix.Translation + relativeOffset) - Vector3D.Transform(centerLocal, matrix);

                        CustomProjection.WorldMatrix = matrix;
                        CustomProjection.PositionComp.Scale = ProjectionScale;
                    }

                    if(isLightVisible)
                    {
                        l.Position = projector.WorldMatrix.Translation + relativeOffset;

                        if(!projectionVisible)
                        {
                            l.Range = LIGHT_FAR_RADIUS_START + (Settings.Scale * LIGHT_FAR_RADIUS_MUL);
                            l.Falloff = LIGHT_FAR_FALLOFF;
                            l.Intensity = LIGHT_FAR_INTENSITY * LightIntensity;
                        }
                        else
                        {
                            l.Range = LIGHT_CENTER_RADIUS_START + (Settings.Scale * LIGHT_CENTER_RADIUS_MUL);
                            l.Falloff = LIGHT_CENTER_FALLOFF;
                            l.Intensity = LIGHT_CENTER_INTENSITY * LightIntensity;
                        }

                        l.UpdateLight();

                        if(CustomProjection != null && projectionVisible)
                        {
                            for(int i = 1; i < lights.Length; ++i)
                            {
                                l = lights[i];

                                if(l == null)
                                    break;

                                l.LightOn = true;
                                l.Position = Vector3D.Transform(corners[i - 1], CustomProjection.WorldMatrix);
                                l.Range = LIGHT_DETAIL_RADIUS_START + (Settings.Scale * LIGHT_DETAIL_RADIUS_MUL);
                                l.Intensity = LIGHT_DETAIL_INTENSITY * LightIntensity;
                                l.UpdateLight();
                            }
                        }
                    }
                }
                #endregion
            }
            else // build mode/vanilla projection mode
            {
                #region Restore vanilla blueprint
                if(MyAPIGateway.Multiplayer.IsServer && OriginalBlueprint != null)
                {
                    if(projector.ProjectedGrid == null)
                    {
                        if(ProjectorPreviewMod.Debug)
                            Log.Info($"UpdateProjection() :: restored original blueprint to projector");

                        projector.SetProjectedGrid(OriginalBlueprint); // it's synchronized, no reason to do it clientside to spam server and clients with requests
                    }

                    OriginalBlueprint = null;
                }
                #endregion
            }
        }

        public void PlayerReceivedBP(ulong id)
        {
            if(playersToReceive == null)
            {
                var e = "PlayerReceivedBP() :: playersToReceive == null !!!!";
                Log.Error(e, e);
                return;
            }

            if(ProjectorPreviewMod.Debug)
            {
                var p = GetPlayerFromSteamId(id);
                Log.Info($"Received confirmation from {p.DisplayName} ({p.SteamUserId}); remaining={playersToReceive.Count}");
            }

            playersToReceive.Remove(id);

            if(playersToReceive.Count == 0)
            {
                if(ProjectorPreviewMod.Debug)
                    Log.Info($"Confirmed from all players, setting vanilla projected grid to null.");

                SetVanillaProjectedGridNull();
            }
        }

        private void SetVanillaProjectedGridNull()
        {
            receiveTimeOut = 0;
            projector.SetProjectedGrid(null);
            playersToReceive?.Clear();
        }

        private void SetLargestGridLength(IMyCubeGrid grid)
        {
            LargestGridLength = (Vector3I.One + (grid.Max - grid.Min)).AbsMax() * grid.GridSize;
            ComputeProjectionScale();
        }

        private void ComputeProjectionScale()
        {
            ProjectionScale = MathHelper.Clamp(Settings.Scale / LargestGridLength, 0, 1);
        }

        private bool LoadSettingsAndBlueprint()
        {
            if(ProjectorPreviewMod.Debug)
                Log.Info("LoadSettingsAndBlueprint()");

            if(projector.Storage == null)
                return false;

            string rawData;
            bool loadedSomething = false;

            if(projector.Storage.TryGetValue(ProjectorPreviewMod.Instance.SETTINGS_GUID, out rawData))
            {
                ProjectorPreviewModSettings loadedSettings = null;

                try
                {
                    if(rawData.IndexOf('<', 0, 5) != -1) // check for XML format, otherwise assume base64.
                        loadedSettings = MyAPIGateway.Utilities.SerializeFromXML<ProjectorPreviewModSettings>(rawData);
                    else
                        loadedSettings = MyAPIGateway.Utilities.SerializeFromBinary<ProjectorPreviewModSettings>(Convert.FromBase64String(rawData));
                }
                catch(Exception e)
                {
                    loadedSettings = null;
                    Log.Error($"Error loading settings!\n{e}");
                }

                if(loadedSettings != null)
                {
                    Settings = loadedSettings;
                    CheckNeedsConstantMatrixUpdate();
                    loadedSomething = true;
                }

                if(ProjectorPreviewMod.Debug)
                    Log.Info($"  Loaded={loadedSomething}; settings:\n{Settings.ToString()}");
            }

            if(projector.Storage.TryGetValue(ProjectorPreviewMod.Instance.BLUEPRINT_GUID, out rawData))
            {
                if(blueprintLoadTaskRunning)
                {
                    // not sure if this is even a case to worry about, but I don't want glitchy behavior.
                    var e = "blueprintLoadTaskRunning=true";
                    Log.Error(e, e);
                }
                else
                {
                    OriginalBlueprint = null;
                    blueprintLoadTaskRunning = true;
                    blueprintLoadTask = MyAPIGateway.Parallel.Start(BlueprintThread.Run, BlueprintLoadCompleted, new BlueprintThread.Data(rawData));
                    loadedSomething = true;
                }
            }

            return loadedSomething;
        }

        private void BlueprintLoadCompleted(WorkData workData)
        {
            blueprintLoadTaskRunning = false;
            var data = (BlueprintThread.Data)workData;

            if(Log.TaskHasErrors(blueprintLoadTask, "blueprintLoadTask"))
                return;

            OriginalBlueprint = data.Blueprint;

            if(ProjectorPreviewMod.Debug)
                Log.Info("BlueprintLoadCompleted()");
        }

        public void SaveSettings()
        {
            if(projector.Storage == null)
                projector.Storage = new MyModStorageComponent();

            projector.Storage[ProjectorPreviewMod.Instance.SETTINGS_GUID] = Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(Settings));

            if(ProjectorPreviewMod.Debug)
                Log.Info("SaveSettings()");
        }

        private void BlueprintSerializeCompleted(WorkData workData)
        {
            try
            {
                blueprintSerializeTaskRunning = false;
                var data = (BlueprintThread.Data)workData;

                if(Log.TaskHasErrors(blueprintSerializeTask, "BlueprintProcessTask") || projector.Closed)
                    return;

                if(projector.Storage == null)
                    projector.Storage = new MyModStorageComponent();

                projector.Storage[ProjectorPreviewMod.Instance.BLUEPRINT_GUID] = data.SerializedBlueprint;

                if(ProjectorPreviewMod.Debug)
                    Log.Info($"BlueprintProcessCompleted() :: name={data.Blueprint.DisplayName}");
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        #region Only for players
        private void GridMoved(MyPositionComponentBase comp)
        {
            needsMatrixUpdate = true;
        }

        private void CustomInfo(IMyTerminalBlock notUsed, StringBuilder info)
        {
            try
            {
                if(!Settings.Enabled)
                    return;

                info.AppendLine("Mode: Preview");

                if(spawnTaskRunning || blueprintLoadTaskRunning)
                {
                    info.AppendLine();
                    info.AppendLine("Loading blueprint...");
                }
                else if(CustomProjection == null)
                {
                    info.AppendLine();
                    info.AppendLine("No blueprint.");
                }
                else if(Settings.Status)
                {
                    info.Append("Status Mode:").AppendLine();
                    info.Append(" ").Append(CustomProjection.BlocksCount).Append("  Total").AppendLine();
                    info.Append(" ").Append(blocksDamaged).Append("  Damaged (yellow-orange)").AppendLine();
                    info.Append(" ").Append(blocksUnfinished).Append("  Unfinished (red-orange)").AppendLine();
                    info.Append(" ").Append(blocksMissing).Append("  Missing (blinking red)").AppendLine();
                    info.Append(" ").Append(blocksWrongType).Append("  Wrong type (light purple)").AppendLine();
                    info.Append(" ").Append(blocksWrongColor).Append("  Wrong color (dark purple)").AppendLine();
                    info.Append(" ").Append(blocksWrongRotation).Append("  Wrong rotation (teal)").AppendLine();
                    info.Append(" ").Append(blocksBetterIntegrity).Append("  Better integrity (green)").AppendLine();

                    if(blocksMissing > BLINK_MAX_BLOCKS)
                    {
                        info.AppendLine();
                        info.Append("More than ").Append(BLINK_MAX_BLOCKS).Append(" missing blocks,\nblinking turned off.").AppendLine();
                    }
                }

                // HACK CustomInfo doesn't update, added note for players; bugreport: SE-7777
                info.AppendLine();
                info.Append("NOTE: Update of this panel is buggy.\nClick the block again to update.");
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void RemoveCustomProjection(bool removeLights, bool removeOriginalBP)
        {
            if(removeOriginalBP)
            {
                OriginalBlueprint = null;

                if(projector.Storage != null)
                    projector.Storage.RemoveValue(ProjectorPreviewMod.Instance.BLUEPRINT_GUID);
            }

            if(ProjectorPreviewMod.Instance.IsPlayer)
            {
                blocksNeedRefresh = true;
                ResetBlockStatus();
                originalColors?.Clear();

                if(CustomProjection != null)
                {
                    if(ProjectorPreviewMod.Debug)
                        Log.Info($"RemoveCustomProjection() :: name={CustomProjection.DisplayName}; id={CustomProjection.EntityId}");

                    CustomProjection.Close();
                    CustomProjection = null;
                }

                if(removeLights && lights != null)
                {
                    for(int i = 0; i < lights.Length; ++i)
                    {
                        var l = lights[i];

                        if(l != null)
                        {
                            MyLights.RemoveLight(l);
                            lights[i] = null;
                        }
                    }
                }
            }
        }

        public override void UpdateAfterSimulation10()
        {
            try
            {
                UpdateProjectionVisibility();
                UpdateStatusMode();

                if(ProjectorPreviewMod.IsInProjectorTerminal)
                    projector.RefreshCustomInfo();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void UpdateProjectionVisibility()
        {
            projectionVisible = false;
            bool lodLightVisible = false;

            if(Settings.Enabled && projector.IsWorking && OriginalBlueprint != null) // projecting miniature
            {
                var diff = (Vector3)(MyAPIGateway.Session.Camera.WorldMatrix.Translation - projector.WorldMatrix.Translation);
                var distance = diff.LengthSquared();
                var scaleSq = Settings.Scale * Settings.Scale;

                projectionVisible = (distance <= (PROJECTION_RANGE_ADD_SQ + (PROJECTION_RANGE_SCALE_SQ * scaleSq)));
                lodLightVisible = (distance <= (LIGHT_CENTER_VIEW_RANGE_SQ * scaleSq));

                // set emissivity to match the projector's vanilla projecting state
                ((MyCubeBlock)projector).SetEmissiveState(ProjectorPreviewMod.Instance.EMISSIVE_NAME_ALTERNATIVE, projector.Render.RenderObjectIDs[0], null);
            }

            if(projectionVisible)
            {
                if(CustomProjection == null)
                {
                    if(!spawnTaskRunning && OriginalBlueprint != null)
                    {
                        if(ProjectorPreviewMod.Debug)
                            Log.Info("UpdateProjectionVisibility() :: Started spawn task...");

                        spawnTaskRunning = true;
                        spawnTask = MyAPIGateway.Parallel.Start(SpawnThread.Run, SpawnTaskCompleted, new SpawnThread.Data(OriginalBlueprint));
                    }
                }
                else if(!CustomProjection.Render.Visible)
                {
                    CustomProjection.Render.Visible = true;
                    needsMatrixUpdate = true;
                }
            }
            else
            {
                if(CustomProjection != null && CustomProjection.Render.Visible)
                {
                    // HACK since .Render.Visible doesn't hide armor, I'm making it really tiny and placing it inside the projector
                    const float SCALE = 0.0000001f;
                    var m = projector.WorldMatrix;
                    MatrixD.Rescale(ref m, SCALE);
                    CustomProjection.WorldMatrix = m;
                    CustomProjection.PositionComp.Scale = SCALE;
                    CustomProjection.Render.SkipIfTooSmall = true;
                    CustomProjection.Render.Visible = false; // must be after the resize or it won't actually resize

                    needsMatrixUpdate = true;
                }

                // NOTE: skips 0-index because that one is shown at longer ranges
                for(int i = 1; i < lights.Length; ++i)
                {
                    var l = lights[i];

                    if(l == null || !l.LightOn)
                        break;

                    l.LightOn = false;
                    l.UpdateLight();
                }
            }

            // light[0] used for longer ranges
            if(lodLightVisible && LightIntensity > 0)
            {
                var l = lights[0];

                if(l == null)
                {
                    lights[0] = CreateLight(true);
                }
                else if(!l.LightOn)
                {
                    l.LightOn = true;
                    needsMatrixUpdate = true;
                }
            }
            else
            {
                var l = lights[0];

                if(l != null && l.LightOn)
                {
                    l.LightOn = false;
                    l.UpdateLight();
                }
            }
        }

        private void SpawnTaskCompleted(WorkData workData)
        {
            try
            {
                spawnTaskRunning = false;
                var data = (SpawnThread.Data)workData;

                if(Log.TaskHasErrors(spawnTask, "SpawnTask"))
                    return;

                CustomProjection = data.Entity;

                if(CustomProjection == null)
                {
                    var e = "SpawnCompleted() :: CustomProjection == null";
                    Log.Error(e, e);
                    return;
                }

                if(projector.Closed || data.Blueprint == null)
                {
                    CustomProjection.Close();
                    CustomProjection = null;
                    return;
                }

                if(originalColors == null)
                    originalColors = new Dictionary<Vector3I, MyTuple<IMySlimBlock, Vector3>>(Vector3I.Comparer);
                else
                    originalColors.Clear();

                foreach(IMySlimBlock projectedSlim in CustomProjection.GetBlocks())
                {
                    originalColors[projectedSlim.Max] = new MyTuple<IMySlimBlock, Vector3>(projectedSlim, projectedSlim.ColorMaskHSV);

                    SetTransparencyAndColor(projectedSlim);

                    var block = projectedSlim.FatBlock as MyCubeBlock;

                    if(block != null)
                    {
                        // HACK gatling gun barrels remain in their original spawn position if gamelogic is turned off...
                        if(!(block is IMyLargeGatlingTurret))
                            block.NeedsUpdate = MyEntityUpdateEnum.NONE;

                        block.StopDamageEffect(); // HACK because projected blocks still have damage effects for some reason

                        if(block.UseObjectsComponent.DetectorPhysics != null && block.UseObjectsComponent.DetectorPhysics.Enabled)
                            block.UseObjectsComponent.DetectorPhysics.Enabled = false;
                    }
                }

                CustomProjection.NeedsUpdate = MyEntityUpdateEnum.NONE;

                MyAPIGateway.Entities.AddEntity(CustomProjection);
                needsMatrixUpdate = true;

                if(ProjectorPreviewMod.Debug)
                    Log.Info($"CustomProjection created; name={CustomProjection.DisplayName} id={CustomProjection.EntityId}");

                SetLargestGridLength(CustomProjection);

                var box = CustomProjection.PositionComp.LocalAABB;
                box.Inflate(CustomProjection.GridSize); // offset the corners outwards to allow lights to light the surfaces properly
                box.GetCorners(corners); // store local corner vectors, to be used with lights

                for(int i = 0; i < lights.Length; ++i)
                {
                    if(lights[i] == null)
                        lights[i] = CreateLight(i == 0);
                }

                if(ProjectorPreviewMod.Debug)
                    Log.Info($"SpawnCompleted() :: name={CustomProjection.DisplayName}");
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        // HACK IMySlimBlock.Min is not exposed
        private static Vector3I ComputeMin(IMySlimBlock block)
        {
            if(block.FatBlock != null)
                return block.FatBlock.Min;

            Vector3D localCenter;
            block.ComputeScaledCenter(out localCenter);
            return new Vector3I((localCenter - (Vector3D)block.Max) / (block.CubeGrid.GridSize * 0.5f));
        }

        private static MyLight CreateLight(bool centerLight)
        {
            var l = MyLights.AddLight();
            l.Start(centerLight ? "ProjectorCenterLight" : "ProjectorDetailLight");
            l.Color = ProjectorPreviewMod.Instance.LIGHT_COLOR;
            l.Intensity = LIGHT_DETAIL_INTENSITY; // centerLight settings don't matter here as it will be overwritten as needed in the update anyway
            l.Falloff = LIGHT_DETAIL_FALLOFF;
            l.PointLightOffset = 0f;
            l.LightOn = true;
            return l;
        }

        private void ResetBlockStatus(bool resetColors = false)
        {
            this.resetColors = resetColors;
            blockIndex = 0;
            blocksDamaged = 0;
            blocksUnfinished = 0;
            blocksMissing = 0;
            blocksWrongType = 0;
            blocksWrongColor = 0;
            blocksWrongRotation = 0;
            blocksBetterIntegrity = 0;
        }

        private void UpdateStatusMode()
        {
            if(!projector.IsWorking || !Settings.Enabled || CustomProjection == null || (!Settings.Status && !blocksNeedRefresh))
                return;

            if(resetColors && !Settings.Status)
            {
                if(originalColors != null && originalColors.Count > 0)
                {
                    var blocksCount = CustomProjection.BlocksCount;
                    var max = Math.Min(blockIndex + BLOCKS_PER_UPDATE, blocksCount);
                    int i = 0;

                    foreach(var tuple in originalColors.Values)
                    {
                        if(++i <= blockIndex)
                            continue;

                        SetTransparencyAndColor(tuple.Item1, tuple.Item2);

                        if(++blockIndex >= max)
                            break;
                    }

                    if(blockIndex >= blocksCount)
                        resetColors = false;
                }
            }
            else
            {
                var blocksCount = CustomProjection.BlocksCount;

                if(blockIndex >= blocksCount)
                    ResetBlockStatus();

                var max = Math.Min(blockIndex + BLOCKS_PER_UPDATE, blocksCount);
                int i = 0;

                foreach(IMySlimBlock projectedSlim in CustomProjection.GetBlocks())
                {
                    if(++i <= blockIndex)
                        continue;

                    if(Settings.Status)
                    {
                        bool highlight;
                        bool blink;
                        var color = GetBlockStatusColor(projectedSlim, out highlight, out blink);
                        SetTransparencyAndColor(projectedSlim, color, highlight, blink);
                    }
                    else
                    {
                        SetTransparencyAndColor(projectedSlim);
                    }

                    if(++blockIndex >= max)
                        break;
                }
            }
        }

        private Vector3 GetBlockStatusColor(IMySlimBlock projectedSlim, out bool highlight, out bool blink)
        {
            highlight = true; // true for all statuses except normal.
            blink = false; // only blink on very specific statuses
            bool wrongRotation = false;

            var projectedSlimMax = projectedSlim.Max;
            IMySlimBlock realSlim = null;

            if(IsInBounds(projector.CubeGrid, ref projectedSlimMax)) // avoid even looking for the real block if its max would be outside of grid boundary
            {
                realSlim = projector.CubeGrid.GetCubeBlock(projectedSlimMax);

                if(realSlim == null)
                {
                    var size = ((MyCubeBlockDefinition)projectedSlim.BlockDefinition).Size;

                    // if the block is larger than 1x1x1, then check the rest of its slots for obstructions
                    if(size.X > 1 || size.Y > 1 || size.Z > 1)
                    {
                        var projectedSlimMin = ComputeMin(projectedSlim);
                        var iterator = new Vector3I_RangeIterator(ref projectedSlimMin, ref projectedSlimMax);

                        while(iterator.IsValid())
                        {
                            realSlim = projector.CubeGrid.GetCubeBlock(iterator.Current);

                            if(realSlim != null)
                            {
                                if(realSlim.BlockDefinition.Id == projectedSlim.BlockDefinition.Id)
                                {
                                    wrongRotation = true;
                                    break;
                                }
                                else
                                {
                                    blocksWrongType++;
                                    return STATUS_COLOR_WRONG_TYPE;
                                }
                            }

                            iterator.MoveNext();
                        }
                    }
                }
            }

            if(realSlim == null)
            {
                blink = true;
                blocksMissing++;
                return STATUS_COLOR_MISSING;
            }

            if(realSlim.BlockDefinition.Id != projectedSlim.BlockDefinition.Id)
            {
                blocksWrongType++;
                return STATUS_COLOR_WRONG_TYPE;
            }

            if(!wrongRotation)
            {
                wrongRotation = (realSlim.Orientation.Forward != projectedSlim.Orientation.Forward
                              || realSlim.Orientation.Up != projectedSlim.Orientation.Up
                              || realSlim.Max != projectedSlimMax);
            }

            float realIntegrity = realSlim.Integrity;
            float projectedIntegrity = projectedSlim.Integrity;

            if(realIntegrity > projectedIntegrity)
            {
                blocksBetterIntegrity++;
                return STATUS_COLOR_BETTER;
            }

            if(realIntegrity < projectedIntegrity)
            {
                highlight = true;
                Vector3 color;
                float realIntegrityRatio = realIntegrity / realSlim.MaxIntegrity;
                float defCritRatio = ((MyCubeBlockDefinition)realSlim.BlockDefinition).CriticalIntegrityRatio;
                float critRatio = realIntegrityRatio - defCritRatio;

                if(realSlim.BuildLevelRatio < defCritRatio)
                {
                    blocksUnfinished++;

                    if(wrongRotation)
                    {
                        blocksWrongRotation++;
                        return STATUS_COLOR_WRONG_ROTATION;
                    }
                }
                else
                {
                    blocksDamaged++;
                }

                if(critRatio > 0)
                {
                    var ratio = critRatio / (1f - defCritRatio);

                    color.X = MathHelper.Lerp(10f / 360f, 75f / 360f, ratio);
                    color.Y = 1;
                    color.Z = 1;
                    return color;
                }
                else
                {
                    var ratio = realIntegrityRatio / defCritRatio;

                    color.X = MathHelper.Lerp(0f / 360f, 10f / 360f, ratio);
                    color.Y = 1;
                    color.Z = 1;
                    return color;
                }
            }

            if(wrongRotation)
            {
                blocksWrongRotation++;
                return STATUS_COLOR_WRONG_ROTATION;
            }

            if(originalColors != null)
            {
                MyTuple<IMySlimBlock, Vector3> originalColor;

                if(originalColors.TryGetValue(projectedSlimMax, out originalColor) && Vector3.DistanceSquared(realSlim.ColorMaskHSV, originalColor.Item2) >= 0.0001f)
                {
                    blocksWrongColor++;
                    return STATUS_COLOR_WRONG_COLOR;
                }
            }

            highlight = false;
            return STATUS_COLOR_NORMAL;
        }

        private void SetTransparencyAndColor(IMySlimBlock slim, Vector3? color = null, bool highlight = false, bool blink = false)
        {
            if(blink)
                highlight = true;

            const float EPSILON = 0.0001f;
            var mod = ProjectorPreviewMod.Instance;

            if(slim.FatBlock != null)
            {
                var block = (MyCubeBlock)slim.FatBlock;
                var seeThrough = Settings.SeeThrough && mod.DECORATIVE_TYPES.Contains(block.BlockDefinition.Id.TypeId);

                if(blink && blinkMemory)
                {
                    block.Render.Visible = false;
                    return;
                }

                if(!block.Render.Visible)
                    block.Render.Visible = true;

                var transparency = (seeThrough ? (highlight ? mod.TransparencyDecorHighlight : mod.TransparencyDecor) : (highlight ? mod.TransparencyHighlight : mod.Transparency));

                // NOTE: .Render.Transparency probably doesn't get saved for a normal grid; but for this mod's purposes this is faster than slim.Dithering
                if(Math.Abs(block.Render.Transparency - transparency) >= EPSILON)
                    SetTransparencyForSubparts(block, transparency);

                if(color.HasValue && !color.Value.Equals(block.Render.ColorMaskHsv, EPSILON))
                    block.Render.ColorMaskHsv = color.Value;
            }
            else
            {
                if(color.HasValue && !color.Value.Equals(slim.GetColorMask(), EPSILON))
                {
#if(VERSION_190 || VERSION_189 || VERSION_188 || VERSION_187 || VERSION_186 || VERSION_185) // HACK backwards compatibility because it's easy in this case
                    CustomProjection.ChangeColor(CustomProjection.GetCubeBlock(slim.Max), color.Value);
#else
                    CustomProjection.ChangeColorAndSkin(CustomProjection.GetCubeBlock(slim.Max), color.Value);
#endif
                }

                var transparency = (Settings.SeeThrough ? (highlight ? mod.TransparencyDecorHighlight : mod.TransparencyDecor) : (highlight ? mod.TransparencyHighlight : mod.Transparency));

                if(blink && blinkMemory)
                    transparency = -1f; // invisible

                if(Math.Abs(slim.Dithering - transparency) >= EPSILON)
                    slim.Dithering = transparency;
            }
        }

        private static void SetTransparencyForSubparts(MyEntity ent, float transparency)
        {
            ent.Render.Transparency = transparency;
            ent.Render.CastShadows = false;

            //ent.Render.RemoveRenderObjects();
            //ent.Render.AddRenderObjects();

            // HACK Add/RemoveRenderObjects() and slim.Dithering have issues...
            // not calling Add/RemoveRenderObjects() on armor block in construction stage it will not update its transparency (? seems to not happen anymore)
            // calling Add/RemoveRenderObjects() causes subparts do detach when setting transparency after spawn (status mode toggle)
            // calling slim.Dithering on turrets after calling this will cause the subpart to be opaque.
            // calling slim.Dithering before any non-armor block will cause ARMOR BLOCKS to cast shadows even if `.Render.CastShadows = false` on both the block and the grid (yeah, makes no sense).
            // and probably more...

            var subparts = ent.Subparts;

            if(subparts == null)
                return;

            foreach(var subpart in subparts.Values)
            {
                SetTransparencyForSubparts(subpart, transparency);
            }
        }

        private bool IsInBounds(IMyCubeGrid grid, ref Vector3I pos)
        {
            return !(grid.Min != Vector3I.Min(pos, grid.Min)) && !(grid.Max != Vector3I.Max(pos, grid.Max));
        }
        #endregion

        #region Legacy save data reading
        public const string DATA_TAG_START = "{ProjectorPreview:";
        public const char DATA_TAG_END = '}';
        public const char DATA_SEPARATOR = ';';
        public const char DATA_KEYVALUE_SEPARATOR = ':';

        public void ReadLegacyStorage()
        {
            var nameLower = projector.CustomName.ToLower();
            var startIndex = nameLower.IndexOf(DATA_TAG_START, StringComparison.OrdinalIgnoreCase);

            if(startIndex == -1)
                return;

            startIndex += DATA_TAG_START.Length;
            var endIndex = nameLower.IndexOf(DATA_TAG_END, startIndex);

            if(endIndex == -1)
                return;

            var data = nameLower.Substring(startIndex, (endIndex - startIndex)).Split(DATA_SEPARATOR);

            foreach(var d in data)
            {
                var kv = d.Split(DATA_KEYVALUE_SEPARATOR);

                switch(kv[0])
                {
                    case "preview":
                        PreviewMode = true;
                        break;
                    case "scale":
                        Scale = float.Parse(kv[1]);
                        break;
                    case "status":
                        StatusMode = true;
                        break;
                    case "offset":
                        for(int i = 0; i < 3; ++i)
                            SetOffset(float.Parse(kv[i + 1]), i);
                        break;
                    case "rotate":
                        for(int i = 0; i < 3; ++i)
                            SetRotate(MathHelper.ToRadians(float.Parse(kv[i + 1])), i);
                        for(int i = 0; i < 3; ++i)
                            SetSpin(Rotate.GetDim(i) < 0, i);
                        break;
                }
            }

            PropertyChanged();

            projector.CustomName = Regex.Replace(projector.CustomName, @"\s+\{ProjectorPreview:(.+)\}\s+", " ").Trim();
        }
        #endregion

        private static IMyPlayer GetPlayerFromSteamId(ulong steamId)
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
