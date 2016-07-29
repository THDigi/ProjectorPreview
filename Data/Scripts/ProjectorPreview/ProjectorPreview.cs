using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using VRage.ObjectBuilders;
using VRage.Game.Components;
using VRage.ModAPI;

using Digi.Utils;

namespace Digi.ProjectorPreview
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class ProjectorPreview : MySessionComponentBase
    {
        public static bool init { get; private set; }
        
        public static readonly List<IMyTerminalControl> terminalControls = new List<IMyTerminalControl>();
        public static readonly List<IMyTerminalControl> refreshControls = new List<IMyTerminalControl>();
        public static readonly HashSet<string> hideTerminalControls = new HashSet<string>()
        {
            "KeepProjection",
            "ShowOnlyBuildable",
            "X",
            "Y",
            "Z",
            "RotX",
            "RotY",
            "RotZ",
            // scenario-only ones
            /*
            "SpawnProjection",
            "InstantBuilding",
            "GetOwnership",
            "NumberOfProjections",
            "NumberOfBlocks",
             */
        };
        
        private void Init()
        {
            Log.Init();
            Log.Info("Initialized.");
            init = true;
            
            MyAPIGateway.TerminalControls.CustomControlGetter += CustomControlGetter;
            
            terminalControls.Clear();
            CreateControls<IMyProjector>(terminalControls);
        }
        
        protected override void UnloadData()
        {
            try
            {
                if(init)
                {
                    init = false;
                    
                    MyAPIGateway.TerminalControls.CustomControlGetter -= CustomControlGetter;
                    
                    Log.Info("Mod unloaded.");
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
            if(!init)
            {
                if(MyAPIGateway.Session == null)
                    return;
                
                Init();
            }
        }
        
        // HACK workaround IMyProjector not being supported by AddControl()
        private static void CustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            var projector = block as IMyProjector;
            
            if(projector != null && terminalControls.Count > 0)
            {
                refreshControls.Clear();
                
                bool preview = projector.GameLogic.GetAs<Projector>().UI_Preview;
                
                for(int i = controls.Count - 1; i >= 0; i--)
                {
                    var c = controls[i];
                    
                    if(hideTerminalControls.Contains(c.Id))
                    {
                        refreshControls.Add(c);
                        
                        c.Enabled = delegate(IMyTerminalBlock b)
                        {
                            var p = b as IMyProjector;
                            
                            if(p == null || b.GameLogic.GetAs<Projector>().UI_Preview)
                                return false;
                            
                            return p.IsProjecting;
                        };
                    }
                }
                
                refreshControls.AddList(terminalControls);
                controls.AddList(terminalControls);
            }
        }
        
        private static void CreateControls<T>(List<IMyTerminalControl> controls)
        {
            const string PREFIX = "ProjectorPreview.";
            var tc = MyAPIGateway.TerminalControls;
            
            {
                var c = tc.CreateControl<IMyTerminalControlSeparator, T>(string.Empty);
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, T>(PREFIX + "Enabled");
                c.Title = MyStringId.GetOrCompute("Projector Preview");
                c.Tooltip = MyStringId.GetOrCompute("Toggles the projector mode between the normal to-scale blueprint mode and preview mode for miniature overview.");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_Preview;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_Preview = v;
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(PREFIX + "Scale");
                c.Title = MyStringId.GetOrCompute("Relative scale");
                c.Tooltip = MyStringId.GetOrCompute("The hologram scale relative to the grid size.");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_Scale;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_Scale = v;
                c.SetLogLimits((b) => 0.1f, (b) => b.GameLogic.GetAs<Projector>().absMax);
                c.Writer = delegate(IMyTerminalBlock b, StringBuilder s)
                {
                    var logic = b.GameLogic.GetAs<Projector>();
                    s.AppendFormat("{0:0.00}m (1:{1:0.000})", logic.scale, logic.absMax / logic.scale);
                };
                c.Enabled = (b) => b.GameLogic.GetAs<Projector>().UI_Enabled;
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlCheckbox, T>(PREFIX + "StatusMode");
                c.Title = MyStringId.GetOrCompute("Status mode");
                c.Tooltip = MyStringId.GetOrCompute("Colors the projection depending on the status of the projector ship's blocks." +
                                                    "\n" +
                                                    "\nYellow to orange if the block is within max health and critical (red line)." +
                                                    "\nOrange to red if the block is below critical and going towards being completely destroyed." +
                                                    "\nDark red if the block is missing." +
                                                    "\nPurple if the block type is different than the projection's block." +
                                                    "\nThe rest are gray.");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_Status;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_Status = v;
                c.Enabled = (b) => b.GameLogic.GetAs<Projector>().UI_Enabled;
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(PREFIX + "OffsetX");
                c.Title = MyStringId.GetOrCompute("Offset X");
                //c.Tooltip = MyStringId.GetOrCompute("");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_OffsetX;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_OffsetX = v;
                c.SetLimits(-10, 10);
                c.Writer = (b, s) => ControlOffsetStatus(b, s, 0);
                c.Enabled = (b) => b.GameLogic.GetAs<Projector>().UI_Enabled;
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(PREFIX + "OffsetY");
                c.Title = MyStringId.GetOrCompute("Offset Y");
                //c.Tooltip = MyStringId.GetOrCompute("");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_OffsetY;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_OffsetY = v;
                c.SetLimits(-10, 10);
                c.Writer = (b, s) => ControlOffsetStatus(b, s, 1);
                c.Enabled = (b) => b.GameLogic.GetAs<Projector>().UI_Enabled;
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(PREFIX + "OffsetZ");
                c.Title = MyStringId.GetOrCompute("Offset Z");
                //c.Tooltip = MyStringId.GetOrCompute("");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_OffsetZ;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_OffsetZ = v;
                c.SetLimits(-10, 10);
                c.Writer = (b, s) => ControlOffsetStatus(b, s, 2);
                c.Enabled = (b) => b.GameLogic.GetAs<Projector>().UI_Enabled;
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(PREFIX + "RotateX");
                c.Title = MyStringId.GetOrCompute("Rotate X");
                //c.Tooltip = MyStringId.GetOrCompute("");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_RotateX;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_RotateX = v;
                c.SetLimits(-360, 360);
                c.Writer = (b, s) => ControlRotateStatus(b, s, 0);
                c.Enabled = (b) => b.GameLogic.GetAs<Projector>().UI_Enabled;
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(PREFIX + "RotateY");
                c.Title = MyStringId.GetOrCompute("Rotate Y");
                //c.Tooltip = MyStringId.GetOrCompute("");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_RotateY;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_RotateY = v;
                c.SetLimits(-360, 360);
                c.Writer = (b, s) => ControlRotateStatus(b, s, 1);
                c.Enabled = (b) => b.GameLogic.GetAs<Projector>().UI_Enabled;
                controls.Add(c);
            }
            
            {
                var c = tc.CreateControl<IMyTerminalControlSlider, T>(PREFIX + "RotateZ");
                c.Title = MyStringId.GetOrCompute("Rotate Z");
                //c.Tooltip = MyStringId.GetOrCompute("");
                c.SupportsMultipleBlocks = true;
                c.Getter = (b) => b.GameLogic.GetAs<Projector>().UI_RotateZ;
                c.Setter = (b, v) => b.GameLogic.GetAs<Projector>().UI_RotateZ = v;
                c.SetLimits(-360, 360);
                c.Writer = (b, s) => ControlRotateStatus(b, s, 2);
                c.Enabled = (b) => b.GameLogic.GetAs<Projector>().UI_Enabled;
                controls.Add(c);
            }
        }
        
        private static void ControlRotateStatus(IMyTerminalBlock b, StringBuilder s, int axis)
        {
            var r = b.GameLogic.GetAs<Projector>().rotate.GetDim(axis);
            var absR = Math.Abs(r);
            
            if(absR <= 0.001f)
                s.Append("Off");
            else if(r > 0)
                s.AppendFormat("Spinning {0:0.00}°/s", r);
            else
                s.AppendFormat("Fixed at {0:0.00}°", absR);
        }
        
        private static void ControlOffsetStatus(IMyTerminalBlock b, StringBuilder s, int axis)
        {
            var o = b.GameLogic.GetAs<Projector>().offset.GetDim(axis);
            
            if(Math.Abs(o) <= 0.001f)
                s.Append("Off");
            else
                s.AppendFormat("{0:0.00}m", o);
        }
    }
    
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Projector))]
    public class Projector : MyGameLogicComponent
    {
        private bool preview = false;
        private bool status = false;
        public float scale = MIN_SCALE;
        public Vector3 offset = Vector3.Zero;
        public Vector3 rotate = Vector3.Zero;
        
        public float absMax = 100;
        
        private byte skip = 200;
        private bool first = true;
        private long turnOn = 0;
        private bool visible = true;
        private int statusIndex = 0;
        private byte propertiesChanged = 0;
        private Vector3 rotateMemory = Vector3.Zero;
        private float gridSize = 0.5f;
        private MyCubeGrid customProjection = null;
        private List<IMySlimBlock> statusCache = null;
        private Dictionary<Vector3I, Vector3> statusPrevColors = null;
        
        public bool UI_Enabled
        {
            get { return preview; }
        }
        
        public bool UI_Preview
        {
            get { return preview; }
            set
            {
                preview = value;
                
                foreach(var c in ProjectorPreview.refreshControls)
                {
                    c.UpdateVisual();
                }
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        public float UI_Scale
        {
            get { return scale; }
            set
            {
                scale = (float)Math.Round(MathHelper.Clamp(value, MIN_SCALE, Math.Min(absMax, MAX_SCALE)), 3);
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        public bool UI_Status
        {
            get { return status; }
            set
            {
                status = value;
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        public float UI_OffsetX
        {
            get { return offset.X; }
            set
            {
                offset.X = (float)Math.Round(value, 2);
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        public float UI_OffsetY
        {
            get { return offset.Y; }
            set
            {
                offset.Y = (float)Math.Round(value, 2);
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        public float UI_OffsetZ
        {
            get { return offset.Z; }
            set
            {
                offset.Z = (float)Math.Round(value, 2);
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        public float UI_RotateX
        {
            get { return rotate.X; }
            set
            {
                rotate.X = (float)Math.Round(value, 2);
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        public float UI_RotateY
        {
            get { return rotate.Y; }
            set
            {
                rotate.Y = (float)Math.Round(value, 2);
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        public float UI_RotateZ
        {
            get { return rotate.Z; }
            set
            {
                rotate.Z = (float)Math.Round(value, 2);
                
                if(propertiesChanged == 0)
                    propertiesChanged = PROPERTIES_CHANGED_TICKS;
            }
        }
        
        //private static bool terminalUI = false;
        
        private static readonly StringBuilder str = new StringBuilder();
        
        public const string DATA_TAG_START = "{ProjectorPreview:";
        public const char DATA_TAG_END = '}';
        public const char DATA_SEPARATOR = ';';
        public const char DATA_KEYVALUE_SEPARATOR = ':';
        
        public const byte PROPERTIES_CHANGED_TICKS = 15;
        
        public const float TRANSPARENCY_FATBLOCK = -0.5f; // dither transparency, negative to have the hologram effect
        public const float TRANSPARENCY_GRID = -0.75f;
        public const float MAX_RANGE_SQ = 25 * 25;
        
        public const float MIN_SCALE = 0.1f;
        public const float MAX_SCALE = 10f;
        
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Entity.NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
        }
        
        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return Entity.GetObjectBuilder(copy);
        }
        
        private void FirstUpdate()
        {
            var block = Entity as IMyTerminalBlock;
            
            if(block.CubeGrid.Physics == null)
                return;
            
            gridSize = block.CubeGrid.GridSize;
            NameChanged(block);
            LegacyStorage();
            block.CustomNameChanged += NameChanged;
            
            //if(!terminalUI)
            //{
            //    terminalUI = true;
            //    CreateControls<IMyProjector>();
            //}
        }
        
        public override void Close()
        {
            try
            {
                RemoveCustomProjection();
                var block = Entity as IMyTerminalBlock;
                block.CustomNameChanged -= NameChanged;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        private void RemoveCustomProjection()
        {
            skip = 0;
            statusIndex = 0;
            statusCache = null;
            statusPrevColors = null;
            
            if(customProjection != null)
            {
                customProjection.Close();
                customProjection = null;
            }
        }
        
        private void CheckAndSpawnProjection()
        {
            var projector = Entity as IMyProjector;
            
            if(customProjection != null || projector.ProjectedGrid == null)
                return;
            
            var obj = projector.ProjectedGrid.GetObjectBuilder(false) as MyObjectBuilder_CubeGrid;
            obj.IsStatic = true;
            obj.CreatePhysics = false;
            obj.OxygenAmount = null;
            obj.DestructibleBlocks = false;
            obj.IsRespawnGrid = false;
            obj.EnableSmallToLargeConnections = false;
            obj.JumpDriveDirection = null;
            obj.JumpRemainingTime = null;
            MyAPIGateway.Entities.RemapObjectBuilder(obj);
            customProjection = MyAPIGateway.Entities.CreateFromObjectBuilderNoinit(obj) as MyCubeGrid;
            customProjection.IsPreview = true;
            customProjection.SyncFlag = false;
            customProjection.Save = false;
            customProjection.Render.CastShadows = false; // does nothing
            customProjection.Render.Transparency = TRANSPARENCY_GRID;
            customProjection.Init(obj);
            
            foreach(var fatBlock in customProjection.GetFatBlocks())
            {
                SetTransparencyForSubparts(fatBlock, TRANSPARENCY_FATBLOCK);
                fatBlock.NeedsUpdate = MyEntityUpdateEnum.NONE; // disable any logic
            }
            
            MyAPIGateway.Entities.AddEntity(customProjection);
            
            // TODO remove edges somehow?
            //foreach(var internalSlim in customProjection.GetBlocks())
            //{
            //    var slim = internalSlim as IMySlimBlock;
            //
            //    if(slim.FatBlock == null)
            //    {
            //        customProjection.SetBlockDirty(internalSlim);
            //        //slim.UpdateVisual();
            //    }
            //}
            //
            //customProjection.UpdateDirty();
            
            absMax = (projector.ProjectedGrid.Max - projector.ProjectedGrid.Min).AbsMax() * projector.CubeGrid.GridSize;
            
            projector.ProjectedGrid.Close();
        }
        
        public override void UpdateAfterSimulation()
        {
            try
            {
                if(first)
                {
                    first = false;
                    FirstUpdate();
                }
                
                var projector = Entity as IMyProjector;
                
                if(propertiesChanged > 0 && --propertiesChanged <= 0)
                {
                    SaveToName();
                }
                
                if(turnOn > 0 && MyAPIGateway.Multiplayer.IsServer && DateTime.UtcNow.Ticks > turnOn)
                {
                    turnOn = 0;
                    projector.RequestEnable(true);
                }
                
                if(!preview || !projector.IsWorking || !projector.IsProjecting) // if projector wants to project this returns true, even if the projected grid is null which is useful here
                {
                    if(customProjection != null && !preview && MyAPIGateway.Multiplayer.IsServer)
                    {
                        turnOn = DateTime.UtcNow.Ticks + (TimeSpan.TicksPerMillisecond * 500);
                        projector.RequestEnable(false);
                    }
                    
                    RemoveCustomProjection();
                    return;
                }
                
                if(++skip >= 30)
                {
                    skip = 0;
                    visible = (Vector3D.DistanceSquared(MyAPIGateway.Session.Camera.WorldMatrix.Translation, projector.WorldMatrix.Translation) <= (MAX_RANGE_SQ * scale));
                    CheckAndSpawnProjection();
                }
                
                if(customProjection == null)
                    return;
                
                customProjection.Render.Visible = visible;
                
                if(visible)
                {
                    if(status)
                    {
                        var realGrid = (projector.CubeGrid as IMyCubeGrid);
                        
                        if(statusCache == null)
                        {
                            statusCache = new List<IMySlimBlock>(customProjection.CubeBlocks);
                            statusPrevColors = new Dictionary<Vector3I, Vector3>(statusCache.Count);
                            
                            foreach(var projectedSlim in statusCache)
                            {
                                statusPrevColors.Add(projectedSlim.Position, projectedSlim.GetColorMask());
                                
                                var color = GetBlockStatusColor(projectedSlim, realGrid.GetCubeBlock(projectedSlim.Position));
                                customProjection.ChangeColor(customProjection.GetCubeBlock(projectedSlim.Position), color);
                                
                                if(projectedSlim.FatBlock != null)
                                {
                                    var block = (projectedSlim.FatBlock as MyCubeBlock);
                                    block.Render.ColorMaskHsv = color;
                                    SetTransparencyForSubparts(block, TRANSPARENCY_FATBLOCK);
                                }
                            }
                        }
                        else
                        {
                            var blocksPerTick = statusCache.Count / 120;
                            byte scanPerTick = (byte)MathHelper.Clamp(blocksPerTick, 1, 50);
                            byte paintPerTick = (byte)MathHelper.Clamp(blocksPerTick, 1, 10);
                            byte painted = 0;
                            byte scanned = 0;
                            
                            while(++scanned <= scanPerTick)
                            {
                                var projectedSlim = statusCache[statusIndex];
                                var color = GetBlockStatusColor(projectedSlim, realGrid.GetCubeBlock(projectedSlim.Position));
                                
                                if(++statusIndex >= statusCache.Count)
                                    statusIndex = 0;
                                
                                if(Vector3.DistanceSquared(projectedSlim.GetColorMask(), color) <= 0.0001f)
                                    continue;
                                
                                customProjection.ChangeColor(customProjection.GetCubeBlock(projectedSlim.Position), color);
                                
                                if(projectedSlim.FatBlock != null)
                                {
                                    var block = (projectedSlim.FatBlock as MyCubeBlock);
                                    block.Render.ColorMaskHsv = color;
                                    SetTransparencyForSubparts(block, TRANSPARENCY_FATBLOCK);
                                }
                                
                                if(++painted > paintPerTick)
                                    break;
                            }
                        }
                    }
                    else
                    {
                        if(statusCache != null)
                        {
                            statusCache = null;
                            statusIndex = 0;
                            
                            foreach(var kv in statusPrevColors)
                            {
                                customProjection.ChangeColor(customProjection.GetCubeBlock(kv.Key), kv.Value);
                            }
                            
                            statusPrevColors = null;
                        }
                    }
                    
                    var rescale = MathHelper.Clamp(scale / absMax, 0, 1);
                    var matrix = projector.WorldMatrix;
                    MatrixD.Rescale(ref matrix, rescale);
                    
                    matrix.Translation = (customProjection.WorldMatrix.Translation - customProjection.PositionComp.WorldAABB.Center);
                    
                    for(int i = 0; i < 3; i++)
                    {
                        var r = rotate.GetDim(i);
                        
                        if(Math.Abs(r) <= 0.001f)
                        {
                            rotateMemory.SetDim(i, 0);
                        }
                        else if(r < 0)
                        {
                            rotateMemory.SetDim(i, r);
                        }
                        else if(r > 0)
                        {
                            r = rotateMemory.GetDim(i) + ((r / 60f) % 360);
                            
                            if(r < 0)
                                r += 360;
                            
                            rotateMemory.SetDim(i, r);
                        }
                    }
                    
                    if(Math.Abs(rotateMemory.X) > 0.001f)
                        matrix *= MatrixD.CreateFromAxisAngle(projector.WorldMatrix.Up, MathHelper.ToRadians(rotateMemory.X));
                    
                    if(Math.Abs(rotateMemory.Y) > 0.001f)
                        matrix *= MatrixD.CreateFromAxisAngle(projector.WorldMatrix.Right, MathHelper.ToRadians(rotateMemory.Y));
                    
                    if(Math.Abs(rotateMemory.Z) > 0.001f)
                        matrix *= MatrixD.CreateFromAxisAngle(projector.WorldMatrix.Backward, MathHelper.ToRadians(rotateMemory.Z));
                    
                    matrix.Translation = projector.WorldMatrix.Translation + (customProjection.WorldMatrix.Translation - customProjection.PositionComp.WorldAABB.Center);
                    matrix.Translation += ((offset.X * projector.WorldMatrix.Right) + ((offset.Y + projector.CubeGrid.GridSize) * projector.WorldMatrix.Up) + (offset.Z * projector.WorldMatrix.Backward));
                    
                    customProjection.WorldMatrix = matrix;
                    customProjection.PositionComp.Scale = rescale;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        private static void SetTransparencyForSubparts(MyEntity entity, float transparency)
        {
            entity.Render.RemoveRenderObjects();
            entity.Render.Transparency = transparency;
            entity.Render.AddRenderObjects();
            
            if(entity.Subparts == null)
                return;
            
            foreach(var e in entity.Subparts.Values)
            {
                SetTransparencyForSubparts(e, transparency);
            }
        }
        
        private static Vector3 GetBlockStatusColor(IMySlimBlock projectedSlim, IMySlimBlock realSlim)
        {
            if(realSlim == null)
                return Color.DarkRed.ColorToHSVDX11();
            
            Vector3 color;
            
            if(realSlim != null)
            {
                if(realSlim.FatBlock != null && projectedSlim.FatBlock != null)
                {
                    var realDef = realSlim.FatBlock.BlockDefinition;
                    var projectedDef = projectedSlim.FatBlock.BlockDefinition;
                    
                    if(realDef.TypeId != projectedDef.TypeId || realDef.SubtypeId != projectedDef.SubtypeId)
                    {
                        return Color.Purple.ColorToHSVDX11();
                    }
                }
                else if(realSlim.FatBlock == null && projectedSlim.FatBlock == null && realSlim.ToString() != projectedSlim.ToString())
                {
                    return Color.Purple.ColorToHSVDX11();
                }
                
                float realIntegrity = Math.Max(realSlim.BuildIntegrity - realSlim.CurrentDamage, 0);
                float realIntegrityRatio = realIntegrity / realSlim.MaxIntegrity;
                float projectedIntegrity = projectedSlim.BuildIntegrity / projectedSlim.MaxIntegrity;
                
                if(realIntegrityRatio < projectedIntegrity)
                {
                    var realBlock = realSlim.FatBlock as MyCubeBlock;
                    
                    if(realBlock != null)
                    {
                        float critRatio = realIntegrityRatio - realBlock.BlockDefinition.CriticalIntegrityRatio;
                        float ratio = 0;
                        
                        if(critRatio > 0)
                        {
                            ratio = critRatio / (1f - realBlock.BlockDefinition.CriticalIntegrityRatio);
                            
                            color.X = MathHelper.Lerp(10f/360f, 75f/360f, ratio);
                            color.Y = 1;
                            color.Z = 1;
                            return color;
                        }
                        else
                        {
                            ratio = realIntegrityRatio / realBlock.BlockDefinition.CriticalIntegrityRatio;
                            
                            color.X = MathHelper.Lerp(0f/360f, 10f/360f, ratio);
                            color.Y = 1;
                            color.Z = 1;
                            return color;
                        }
                    }
                    else
                    {
                        color.X = MathHelper.Lerp(0f/360f, 75f/360f, realIntegrityRatio);
                        color.Y = 1;
                        color.Z = 1;
                        return color;
                    }
                }
            }
            
            color.X = 0;
            color.Y = -1;
            color.Z = 0;
            return color;
        }
        
        public void NameChanged(IMyTerminalBlock block)
        {
            try
            {
                ResetSettings(); // first reset fields
                
                var name = block.CustomName.ToLower();
                var startIndex = name.IndexOf(DATA_TAG_START, StringComparison.OrdinalIgnoreCase);
                
                if(startIndex == -1)
                    return;
                
                startIndex += DATA_TAG_START.Length;
                var endIndex = name.IndexOf(DATA_TAG_END, startIndex);
                
                if(endIndex == -1)
                    return;
                
                var data = name.Substring(startIndex, (endIndex - startIndex)).Split(DATA_SEPARATOR);
                
                foreach(var d in data)
                {
                    var kv = d.Split(DATA_KEYVALUE_SEPARATOR);
                    
                    switch(kv[0])
                    {
                        case "preview":
                            preview = true;
                            break;
                        case "scale":
                            scale = MathHelper.Clamp(float.Parse(kv[1]), MIN_SCALE, Math.Min(absMax, MAX_SCALE));
                            break;
                        case "status":
                            status = true;
                            break;
                        case "offset":
                            offset = new Vector3(float.Parse(kv[1]), float.Parse(kv[2]), float.Parse(kv[3]));
                            break;
                        case "rotate":
                            rotate = new Vector3(float.Parse(kv[1]), float.Parse(kv[2]), float.Parse(kv[3]));
                            break;
                        default:
                            Log.Error("Unknown key in name: '"+kv[0]+"', data raw: '"+block.CustomName+"'");
                            break;
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public void ResetSettings()
        {
            preview = false;
            scale = gridSize;
            status = false;
            offset = Vector3.Zero;
            rotate = Vector3.Zero;
        }
        
        public bool AreSettingsDefault()
        {
            return !(preview
                     || Math.Abs(scale - gridSize) >= 0.001f
                     || status
                     || offset.LengthSquared() > 0
                     || rotate.LengthSquared() > 0);
        }
        
        private void SaveToName(string forceName = null)
        {
            var block = Entity as IMyTerminalBlock;
            var trimmedName = (forceName ?? GetNameNoData());
            
            if(AreSettingsDefault())
            {
                if(block.CustomName.Length != trimmedName.Length)
                    block.SetCustomName(trimmedName);
                
                return;
            }
            
            str.Clear();
            str.Append(trimmedName);
            str.Append(' ', 3);
            str.Append(DATA_TAG_START);
            
            if(preview)
            {
                str.Append("preview");
                str.Append(DATA_SEPARATOR);
            }
            
            if(Math.Abs(scale - gridSize) >= 0.001f)
            {
                str.Append("scale").Append(DATA_KEYVALUE_SEPARATOR).Append(scale);
                str.Append(DATA_SEPARATOR);
            }
            
            if(status)
            {
                str.Append("status");
                str.Append(DATA_SEPARATOR);
            }
            
            if(offset.LengthSquared() > 0)
            {
                str.Append("offset");
                str.Append(DATA_KEYVALUE_SEPARATOR).Append(offset.X);
                str.Append(DATA_KEYVALUE_SEPARATOR).Append(offset.Y);
                str.Append(DATA_KEYVALUE_SEPARATOR).Append(offset.Z);
                str.Append(DATA_SEPARATOR);
            }
            
            if(rotate.LengthSquared() > 0)
            {
                str.Append("rotate");
                str.Append(DATA_KEYVALUE_SEPARATOR).Append(rotate.X);
                str.Append(DATA_KEYVALUE_SEPARATOR).Append(rotate.Y);
                str.Append(DATA_KEYVALUE_SEPARATOR).Append(rotate.Z);
                str.Append(DATA_SEPARATOR);
            }
            
            if(str[str.Length - 1] == DATA_SEPARATOR) // remove the last DATA_SEPARATOR character
                str.Length -= 1;
            
            str.Append(DATA_TAG_END);
            
            block.SetCustomName(str.ToString());
        }
        
        private string GetNameNoData()
        {
            var block = Entity as IMyTerminalBlock;
            var name = block.CustomName;
            var startIndex = name.IndexOf(DATA_TAG_START, StringComparison.OrdinalIgnoreCase);
            
            if(startIndex == -1)
                return name;
            
            var nameNoData = name.Substring(0, startIndex);
            var endIndex = name.IndexOf(DATA_TAG_END, startIndex);
            
            if(endIndex == -1)
                return nameNoData.Trim();
            else
                return (nameNoData + name.Substring(endIndex + 1)).Trim();
        }
        
        // legacy name tags support
        
        private static readonly Regex previewRegex = new Regex(@"([@+]preview\:)(-?[\.\d]+)", RegexOptions.Compiled);
        private static readonly Regex spinRegex = new Regex(@"([@+]spin\:)(-?[\.\d]+)", RegexOptions.Compiled);
        
        public void LegacyStorage()
        {
            try
            {
                var block = Entity as IMyProjector;
                string name = block.CustomName;
                
                if(string.IsNullOrEmpty(name))
                    return;
                
                name = name.ToLower();
                
                if(name.Contains("@preview") || name.Contains("+preview"))
                {
                    preview = true;
                    var match = previewRegex.Match(name);
                    string finalCustomName = block.CustomName;
                    
                    if(match.Success)
                    {
                        finalCustomName = previewRegex.Replace(finalCustomName, "");
                        float num;
                        
                        if(float.TryParse(match.Groups[2].Value, out num))
                        {
                            scale = MathHelper.Clamp(num, 0.1f, 5f);
                        }
                    }
                    else
                    {
                        finalCustomName = finalCustomName.Replace("@preview", "");
                        finalCustomName = finalCustomName.Replace("+preview", "");
                        scale = 1;
                    }
                    
                    if(name.Contains("@spin") || name.Contains("+spin"))
                    {
                        rotate.X = 60;
                        match = spinRegex.Match(name);
                        
                        if(match.Success)
                        {
                            finalCustomName = spinRegex.Replace(finalCustomName, "");
                            float num;
                            
                            if(float.TryParse(match.Groups[2].Value, out num))
                            {
                                rotate.X = MathHelper.Clamp(num * 60, -360, 360);
                            }
                        }
                        else
                        {
                            finalCustomName = finalCustomName.Replace("@spin", "");
                            finalCustomName = finalCustomName.Replace("+spin", "");
                        }
                    }
                    
                    if(name.Contains("@status") || name.Contains("+status"))
                    {
                        status = true;
                        finalCustomName = finalCustomName.Replace("@status", "");
                        finalCustomName = finalCustomName.Replace("+status", "");
                    }
                    
                    SaveToName(finalCustomName.Trim());
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}