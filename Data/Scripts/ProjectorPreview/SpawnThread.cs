using ParallelTasks;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace Digi.ProjectorPreview
{
    static class SpawnThread
    {
        public class Data : WorkData
        {
            public MyObjectBuilder_CubeGrid Blueprint = null;
            public MyCubeGrid Entity = null;

            public Data(MyObjectBuilder_CubeGrid blueprint)
            {
                Blueprint = blueprint;
            }
        }

        public static void Run(WorkData workData)
        {
            var data = (Data)workData;

            var bp = (MyObjectBuilder_CubeGrid)data.Blueprint.Clone();

            CleanBlueprint(bp);

            MyAPIGateway.Entities.RemapObjectBuilder(bp);

            var ent = (MyCubeGrid)MyEntities.CreateFromObjectBuilder(bp, false);

            ent.IsPreview = true;
            ent.SyncFlag = false;
            ent.Save = false;
            ent.Render.CastShadows = false;

            data.Entity = ent;
        }

        private static void CleanBlueprint(MyObjectBuilder_CubeGrid bp)
        {
            bp.PersistentFlags = MyPersistentEntityFlags2.InScene;
            bp.IsStatic = true;
            bp.CreatePhysics = false;
            bp.Editable = false;
            bp.DestructibleBlocks = false;
            bp.IsPowered = false;
            bp.DampenersEnabled = false;
            bp.IsRespawnGrid = false;
            bp.LinearVelocity = bp.AngularVelocity = new SerializableVector3();
            bp.OxygenAmount = null;
            bp.JumpDriveDirection = null;
            bp.JumpRemainingTime = null;
            bp.ConveyorLines?.Clear(); // must not be null
            bp.BlockGroups?.Clear(); // must not be null
            bp.TargetingTargets = null;
            bp.TargetingWhitelist = false;
            bp.XMirroxPlane = null;
            bp.YMirroxPlane = null;
            bp.ZMirroxPlane = null;

            for(int i = bp.CubeBlocks.Count - 1; i >= 0; --i)
            {
                var block = bp.CubeBlocks[i];
                block.EntityId = 0;
                block.Owner = 0;
                block.ShareMode = MyOwnershipShareModeEnum.None;
                block.ComponentContainer = null;
                block.ConstructionInventory = null;
                block.ConstructionStockpile = null;
                block.Name = null;
                block.SetupForProjector();

                var terminal = block as MyObjectBuilder_TerminalBlock;

                if(terminal != null)
                {
                    terminal.CustomName = null;
                    terminal.ShowInInventory = true;
                    terminal.ShowOnHUD = true;
                    terminal.ShowInTerminal = true;
                    terminal.ShowInToolbarConfig = true;
                }

                var functional = block as MyObjectBuilder_FunctionalBlock;

                if(functional != null)
                    functional.Enabled = false;

                var projector = block as MyObjectBuilder_ProjectorBase;

                if(projector != null)
                {
                    projector.ProjectedGrid = null;
                    projector.ProjectionOffset = Vector3I.Zero;
                    projector.ProjectionRotation = Vector3I.Zero;
                }

                var pb = block as MyObjectBuilder_MyProgrammableBlock;

                if(pb != null)
                {
                    pb.Program = null;
                    pb.Storage = string.Empty;
                    pb.DefaultRunArgument = null;
                }

                var lcd = block as MyObjectBuilder_TextPanel;

                if(lcd != null)
                {
                    lcd.PublicTitle = null;
                    lcd.PublicDescription = null;
                    lcd.Title = null;
                    lcd.Description = null;
                }

                var timer = block as MyObjectBuilder_TimerBlock;

                if(timer != null)
                {
                    ClearToolbar(timer.Toolbar); // must not be null
                    timer.JustTriggered = false;
                    timer.Delay = 10000;
                    timer.IsCountingDown = false;
                    timer.Silent = true;
                }

                var warhead = block as MyObjectBuilder_Warhead;

                if(warhead != null)
                {
                    warhead.IsArmed = false;
                    warhead.IsCountingDown = false;
                    warhead.CountdownMs = 10000;
                }

                var button = block as MyObjectBuilder_ButtonPanel;

                if(button != null)
                {
                    ClearToolbar(button.Toolbar); // must not be null
                }

                var shipController = block as MyObjectBuilder_ShipController;

                if(shipController != null)
                {
                    ClearToolbar(shipController.Toolbar); // must not be null
                }

                var sensor = block as MyObjectBuilder_SensorBlock;

                if(sensor != null)
                {
                    ClearToolbar(sensor.Toolbar); // must not be null
                }
            }
        }

        private static void ClearToolbar(MyObjectBuilder_Toolbar toolbar)
        {
            if(toolbar == null)
                return;

            toolbar.Slots?.Clear();
            toolbar.ColorMaskHSVList?.Clear();
            toolbar.SelectedSlot = null;
        }
    }
}