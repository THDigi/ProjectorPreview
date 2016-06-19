using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.ObjectBuilders;
using VRage.Game.Components;
using VRage.ModAPI;

using Ingame = Sandbox.ModAPI.Ingame;

using Digi.Utils;

namespace Digi.ProjectorPreview
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class ProjectorPreview : MySessionComponentBase
    {
        public static bool init { get; private set; }
        
        public void Init()
        {
            Log.Init();
            Log.Info("Initialized.");
            init = true;
        }
        
        protected override void UnloadData()
        {
            try
            {
                if(init)
                {
                    init = false;
                    Log.Info("Mod unloaded.");
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
            
            Log.Close();
        }
        
        public override void UpdateAfterSimulation()
        {
            if(!init)
            {
                if(MyAPIGateway.Session == null)
                    return;
                
                Init();
            }
        }
    }
    
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Projector))]
    public class Projector : MyGameLogicComponent
    {
        public bool preview = false;
        public bool spin = false;
        public bool status = false;
        public byte statusType = 0;
        //public bool strip = false;
        public float spinSpeed = 0;
        public float scaleOffset = 1.0f;
        
        private static readonly Regex previewRegex = new Regex(@"([@+]preview\:)(-?[\.\d]+)", RegexOptions.Compiled);
        private static readonly Regex spinRegex = new Regex(@"([@+]spin\:)(-?[\.\d]+)", RegexOptions.Compiled);
        //private static readonly Regex statusRegex = new Regex(@"([@+]status\:)(-?[\.\w]+)", RegexOptions.Compiled);
        
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Entity.NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }
        
        public override void UpdateOnceBeforeFrame()
        {
            var block = Entity as IMyTerminalBlock;
            
            if(block.CubeGrid.Physics == null)
                return;
            
            NameChanged(block);
            block.CustomNameChanged += NameChanged;
            block.AppendingCustomInfo += CustomInfo;
            block.RefreshCustomInfo();
        }
        
        public void NameChanged(IMyTerminalBlock terminal)
        {
            try
            {
                var block = terminal as Ingame.IMyProjector;
                string name = block.CustomName;
                
                if(name == null || name.Length == 0)
                {
                    preview = false;
                    return;
                }
                
                name = name.ToLower();
                preview = name.Contains("@preview") || name.Contains("+preview");
                
                if(!preview)
                    return;
                
                var match = previewRegex.Match(name);
                
                if(match.Success)
                {
                    float num;
                    
                    if(float.TryParse(match.Groups[2].Value, out num))
                    {
                        scaleOffset = MathHelper.Clamp(num, 0.1f, 5f);
                    }
                }
                else
                {
                    scaleOffset = 1;
                }
                
                spin = name.Contains("@spin") || name.Contains("+spin");
                
                if(spin)
                {
                    match = spinRegex.Match(name);
                    
                    if(match.Success)
                    {
                        float num;
                        
                        if(float.TryParse(match.Groups[2].Value, out num))
                        {
                            spinSpeed = MathHelper.Clamp(num, -10, 10);
                        }
                    }
                    else
                    {
                        spinSpeed = 1;
                    }
                }
                
                status = name.Contains("@status") || name.Contains("+status");
                
                //if(status)
                //{
                //    match = statusRegex.Match(name);
                //
                //    if(match.Success)
                //    {
                //        switch(match.Groups[2].Value)
                //        {
                //            case "damage":
                //                statusType = 1;
                //                break;
                //            case "func":
                //                statusType = 2;
                //                break;
                //        }
                //    }
                //    else
                //    {
                //        statusType = 0;
                //    }
                //}
                
                //strip = name.Contains("@strip") || name.Contains("+strip");
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public void CustomInfo(IMyTerminalBlock block, StringBuilder info)
        {
            info.Clear();
            info.AppendLine();
            info.AppendLine("Projector Preview options:");
            info.AppendLine("@preview[:num] - enables preview, num range 0.1 to 5.0");
            info.AppendLine("@spin[:num] - spin in preview, num range -10 to 10");
            info.AppendLine("@status - color depending on damage or build level");
            info.AppendLine("Add these in the block name to use them.");
            info.AppendLine("If you can't use @ then you can use + instead");
            info.AppendLine("Examples:");
            info.AppendLine("Projector @preview");
            info.AppendLine("Projector @preview:2.5 @spin:-3");
            info.AppendLine("Projector +preview:5 +spin:0.5");
            info.AppendLine("Projector +preview:0.25 +status");
        }
        
        public override void Close()
        {
            var block = Entity as IMyTerminalBlock;
            block.CustomNameChanged -= NameChanged;
            block.AppendingCustomInfo -= CustomInfo;
        }
        
        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return Entity.GetObjectBuilder(copy);
        }
    }
    
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CubeGrid))]
    public class Grid : MyGameLogicComponent
    {
        private MyObjectBuilder_EntityBase objectBuilder;
        
        private Ingame.IMyProjector projector = null;
        private bool preview = false;
        private bool spin = false;
        //private bool stripped = false;
        private bool far = false;
        private float spinSpeed = 1.0f;
        private float scale = 1.0f;
        private float maxSize;
        private float deg = 0;
        private int skip = 9999; // execute first slow tick ASAP
        
        public const float MAX_RANGE_SQ = 25 * 25;
        
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            this.objectBuilder = objectBuilder;
            
            Entity.NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
            
            preview = SlowUpdate();
        }
        
        public override void UpdateOnceBeforeFrame()
        {
            maxSize = (float)Entity.PositionComp.WorldAABB.Size.AbsMax();
        }
        
        private bool SlowUpdate()
        {
            if(MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null)
                return false;
            
            var grid = Entity as MyCubeGrid;
            
            if(grid.Projector == null)
            {
                projector = null;
                return false;
            }
            
            projector = grid.Projector as Ingame.IMyProjector;
            
            if(!projector.IsWorking)
                return false;
            
            var logic = projector.GameLogic.GetAs<Projector>();
            
            if(!logic.preview)
                return false;
            
            /* // TODO strip armor?
            if(logic.strip && !stripped)
            {
                stripped = true;
                MyAPIGateway.Utilities.ShowNotification("Stripped!", 3000, MyFontEnum.Red);
                
                var gridObj = objectBuilder as MyObjectBuilder_CubeGrid;
                var blocks = gridObj.CubeBlocks;
                
                for(int i = (blocks.Count - 1); i >= 0; i--)
                {
                    blocks[i].BuildPercent = 1;
                    
                    if(blocks[i].SubtypeName.ToLower().Contains("window"))
                    {
                        blocks.RemoveAt(i);
                        continue;
                    }
                    
                    switch(blocks[i].TypeId.ToString())
                    {
                        case "MyObjectBuilder_CubeBlock":
                        case "MyObjectBuilder_Refinery":
                        case "MyObjectBuilder_UpgradeModule":
                        case "MyObjectBuilder_CargoContainer":
                        case "MyObjectBuilder_Door":
                        case "MyObjectBuilder_AirtightHangarDoor":
                        case "MyObjectBuilder_JumpDrive":
                        case "MyObjectBuilder_Conveyor":
                        case "MyObjectBuilder_ConveyorConnector":
                        case "MyObjectBuilder_Assembler":
                        case "MyObjectBuilder_SolarPanel":
                        case "MyObjectBuilder_Thrust":
                        case "MyObjectBuilder_RadioAntenna":
                        case "MyObjectBuilder_Beacon":
                        case "MyObjectBuilder_MedicalRoom":
                            continue;
                    }
                    
                    blocks.RemoveAt(i);
                }
                
                /*
                List<IMySlimBlock> blocks = new List<IMySlimBlock>();
                var cubeGrid = (grid as IMyCubeGrid);
                cubeGrid.GetBlocks(blocks, b => b.FatBlock != null);
                
                foreach(var slim in blocks)
                {
                    cubeGrid.RemoveBlock(slim, false);
                }
                
                //grid.GetBlocks().RemoveWhere(b => (b as IMySlimBlock).FatBlock != null);
                
                /*
                foreach(var slimBlockInternal in grid.GetBlocks())
                {
                    var slimBlock = slimBlockInternal as IMySlimBlock;
                    
                    if(slimBlock.FatBlock != null)
                        grid.RemoveBlock(slimBlockInternal, false);
                }
             */
            //}
            
            spin = logic.spin;
            spinSpeed = logic.spinSpeed;
            scale = MathHelper.Clamp((grid.GridSize / maxSize) * logic.scaleOffset, 0, 1);
            far = Vector3D.DistanceSquared(MyAPIGateway.Session.Player.GetPosition(), projector.WorldMatrix.Translation) > (MAX_RANGE_SQ * logic.scaleOffset);
            
            if(logic.status)
            {
                var projectedGrid = (grid as MyCubeGrid);
                var realGrid = (projector.CubeGrid as MyCubeGrid);
                
                foreach(var internalSlim in projectedGrid.CubeBlocks)
                {
                    var projectedSlim = internalSlim as IMySlimBlock;
                    var realSlim = realGrid.GetCubeBlock(projectedSlim.Position) as IMySlimBlock;
                    var color = GetBlockStatusColor(projectedSlim, realSlim, logic);
                    
                    if(projectedSlim.FatBlock == null)
                    {
                        projectedGrid.ChangeColor(projectedSlim as Sandbox.Game.Entities.Cube.MySlimBlock, color); // can cause severe issues if used on a FatBlock!
                    }
                    else
                    {
                        var block = (projectedSlim.FatBlock as MyCubeBlock);
                        block.Render.ColorMaskHsv = color;
                        block.Render.Transparency = -0.5f; // dither transparency, negative to have the hologram effect
                        block.Render.RemoveRenderObjects();
                        block.Render.AddRenderObjects();
                    }
                }
            }
            
            return true;
        }
        
        public Vector3 GetBlockStatusColor(IMySlimBlock projectedSlim, IMySlimBlock realSlim, Projector logic)
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
        
        public override void UpdateAfterSimulation()
        {
            try
            {
                if(++skip >= 20)
                {
                    skip = 0;
                    preview = SlowUpdate();
                    
                    if(projector == null)
                        return;
                    
                    if(!preview)
                    {
                        if(Entity.PositionComp.Scale.HasValue)
                            Entity.PositionComp.Scale = null;
                    }
                }
                
                if(projector == null)
                    return;
                
                if(projector.Closed || projector.MarkedForClose)
                {
                    projector = null;
                    return;
                }
                
                if(!preview)
                    return;
                
                if(far)
                {
                    if(Entity.Render.Visible)
                        Entity.Render.Visible = false;
                }
                else
                {
                    if(!Entity.Render.Visible)
                        Entity.Render.Visible = true;
                }
                
                var matrix = Entity.WorldMatrix;
                MatrixD.Rescale(ref matrix, scale);
                
                if(spin)
                {
                    deg = (deg + spinSpeed) % 360;
                    
                    if(deg < 0)
                        deg += 360;
                    
                    matrix.Translation = (Entity.WorldMatrix.Translation - Entity.PositionComp.WorldAABB.Center);
                    matrix *= MatrixD.CreateFromAxisAngle(projector.WorldMatrix.Up, MathHelper.ToRadians(deg));
                    matrix.Translation = projector.WorldMatrix.Translation + matrix.Translation;
                }
                else
                {
                    matrix.Translation = projector.WorldMatrix.Translation + (Entity.WorldMatrix.Translation - Entity.PositionComp.WorldAABB.Center);
                }
                
                matrix.Translation += ((projector.ProjectionOffsetX * projector.WorldMatrix.Right) + (projector.ProjectionOffsetY * projector.WorldMatrix.Up) + (projector.ProjectionOffsetZ * projector.WorldMatrix.Backward)) * (projector.CubeGrid.GridSize / 10);
                Entity.WorldMatrix = matrix;
                Entity.PositionComp.Scale = scale;
                return;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public override void Close()
        {
            objectBuilder = null;
            projector = null;
        }
        
        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return copy ? (MyObjectBuilder_EntityBase)objectBuilder.Clone() : objectBuilder;
        }
    }
}