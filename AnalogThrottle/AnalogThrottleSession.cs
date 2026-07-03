using IngameScript.WolfeLabs.AnalogThrottleAPI;
using Sandbox.ModAPI;
using SharpDX.DirectInput;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;

namespace WolfeLabs.AnalogThrottle
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    class AnalogThrottleSession : MySessionComponentBase
    {
        private const int CONTROLLER_SCAN_INTERVAL_TICKS = 300;

        public static AnalogThrottleSession Instance;

        // This List stores all currently connected controllers
        private List<Controller> controllers = new List<Controller>();

        // Shared DirectInput instance used by all controllers
        private DirectInput directInput = null;

        // This List will store all commands that will be sent to our PBs
        private ControllerInputCollection queuedInputEvents = new ControllerInputCollection();

        // Our current Player
        private IMyPlayer currentPlayer = null;

        // Our current Ship Controller
        private IMyShipController currentControlUnit = null;

        // Our current Grid
        private IMyCubeGrid currentGrid = null;

        // Our current tick number
        private ushort currentTick = ushort.MaxValue;

        // Our currently linked Programming Blocks
        private List<IMyProgrammableBlock> currentProgrammableBlocks = new List<IMyProgrammableBlock>();

        public override void Init (MyObjectBuilder_SessionComponent sessionComponent)
        {
            DebugHelper.Log("Session.Init()");

            // Initializes DirectInput
            this.directInput = new DirectInput();

            // Fetches the available controllers and sets them up
            this.ScanForControllers();
        }

        public override void LoadData ()
        {
            AnalogThrottleSession.Instance = this;
        }

        protected override void UnloadData ()
        {
            this.UnsubscribeCurrentPlayer();
            this.ClearCurrentGrid();
            this.currentControlUnit = null;
            this.queuedInputEvents.Clear();
            this.DisposeControllers();
            this.DisposeDirectInput();

            AnalogThrottleSession.Instance = null;
        }

        public override void UpdateBeforeSimulation ()
        {
            // Accounts for current tick, overflows as needed
            if (this.currentTick == ushort.MaxValue) {
                this.currentTick = ushort.MinValue;
            } else {
                this.currentTick++;
            }

            // Waits for a Player instance to exist
            if (null == this.Session.Player)
                return;

            // If first time detected a player (= login, usually) does first triggers and set-up
            if (null == this.currentPlayer) {
                this.currentPlayer = this.Session.Player;
                this.Session.Player.Controller.ControlledEntityChanged += this.UpdateCurrentControlUnit;
                this.UpdateCurrentControlUnit(null, this.Session.Player.Controller.ControlledEntity);
            }

            if (0 == this.currentTick % CONTROLLER_SCAN_INTERVAL_TICKS) {
                this.ScanForControllers();
            }

            // For multiplayer sessions, only processed each n-th input
            if (!this.Session.IsServer && 0 != this.currentTick % Plugin.THROTTLE_MULTIPLAYER) {
                DebugHelper.Log($"Skipping tick for multiplayer: { this.currentTick % Plugin.THROTTLE_MULTIPLAYER } of { Plugin.THROTTLE_MULTIPLAYER }");
                return;
            }

            // Ticks the Controllers
            for (int controllerIndex = this.controllers.Count - 1; controllerIndex >= 0; controllerIndex--) {
                Controller controller = this.controllers[controllerIndex];

                if (!controller.HandleInput()) {
                    DebugHelper.Log($"Removing disconnected controller: { controller.Device.InstanceName }");
                    this.RemoveController(controllerIndex);
                }
            }

            // Sends list of queued events and clears queue
            if (this.currentProgrammableBlocks.Count > 0 && this.queuedInputEvents.Count > 0) {
                DebugHelper.Log($"Sending { this.queuedInputEvents.Count } controller events to game");
                this.SendInputToActiveGrid(this.queuedInputEvents.ToString());
            }
            this.queuedInputEvents.Clear();
        }

        private void UpdateCurrentControlUnit (IMyControllableEntity oldUnit, IMyControllableEntity newUnit)
        {
            this.ClearCurrentGrid();

            // Tries to convert the new Control Unit into a Ship Controller, becomes null otherwise
            this.currentControlUnit = newUnit as IMyShipController;

            DebugHelper.Log("Convert Check");
            // If a usable Ship Controller and a grid are detected, extract grid information
            if (this.CanUseControlUnit(this.currentControlUnit) && null != this.currentControlUnit.CubeGrid) {
                // Prepares grid and events
                DebugHelper.Log("Grid");
                this.currentGrid = this.currentControlUnit.CubeGrid;
                DebugHelper.Log("Events");
                this.currentGrid.OnBlockAdded += this.UpdateCurrentGridBlocks;
                this.currentGrid.OnBlockRemoved += this.UpdateCurrentGridBlocks;

                // Fetches the Programmable Blocks
                DebugHelper.Log("UpdateBlocks");
                this.UpdateCurrentGridBlocks(null);

                DebugHelper.Log($"Ship controller found: { this.currentControlUnit.CustomName }");
                DebugHelper.Log($"Grid found: { this.currentControlUnit.CubeGrid.CustomName }");
                return;
            }

            if (null == this.currentControlUnit) {
                DebugHelper.Log($"No ship controller found");
            } else {
                DebugHelper.Log($"Ship controller cannot control ship: { this.currentControlUnit.CustomName }");
            }
        }

        private bool CanUseControlUnit (IMyShipController controlUnit)
        {
            return null != controlUnit && controlUnit.CanControlShip;
        }

        private void UpdateCurrentGridBlocks (IMySlimBlock changedBlock)
        {
            // Makes sure we have an active ship controller and grid
            if (this.currentControlUnit == null || this.currentGrid == null)
                return;

            // Gets a list of all blocks on the grid
            List<IMySlimBlock> gridBlocks = new List<IMySlimBlock>();
            currentControlUnit.CubeGrid.GetBlocks(gridBlocks);

            // Now filters any Programmable Blocks that contain the [AnalogThrottle] tag
            this.currentProgrammableBlocks.Clear();
            foreach (IMySlimBlock block in gridBlocks) {
                // Tries to convert into a PB
                IMyProgrammableBlock blockAsPB = block.FatBlock as IMyProgrammableBlock;

                // Adds to our PB list if found
                if (null != blockAsPB && blockAsPB.CustomName.Contains(Plugin.TAG_SCRIPTABLE)) {
                    this.currentProgrammableBlocks.Add(blockAsPB);
                }
            }

            // Debugging helper, shows how many valid PBs were found
            DebugHelper.Log($"Grid processed: { this.currentProgrammableBlocks.Count } PBs out of { gridBlocks.Count } blocks");
        }

        private void SendInputToActiveGrid (string message)
        {
            DebugHelper.Log($"Preparing command: { message }");
            try {
                // Sends data to each of our PBs
                foreach (IMyProgrammableBlock block in this.currentProgrammableBlocks) {
                    DebugHelper.Log($"Sending command to PB: { block.CustomName }");
                    block.Run(message, Sandbox.ModAPI.Ingame.UpdateType.Mod);
                }
            } catch (Exception e) {
                DebugHelper.Log("ERROR: " + e.Message);
                DebugHelper.Log(e);
            }
        }

        private void HandleAnalogInput (object sender, Controller.AnalogEventArgs args)
        {
            Controller controller = sender as Controller;
            this.queuedInputEvents.Add(new ControllerInput(controller.Device.InstanceName, args.Axis, args.Value));
        }

        private void HandleDigitalInput (object sender, Controller.DigitalEventArgs args)
        {
            Controller controller = sender as Controller;
            this.queuedInputEvents.Add(new ControllerInput(controller.Device.InstanceName, args.Axis, args.Value ? ushort.MaxValue : ushort.MinValue));
        }

        private void ScanForControllers ()
        {
            if (null == this.directInput)
                return;

            IList<DeviceInstance> availableDevices;
            try {
                availableDevices = this.directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            } catch (Exception e) {
                DebugHelper.Log("Controller scan failed");
                DebugHelper.Log(e);
                return;
            }

            if (null == availableDevices)
                return;

            foreach (DeviceInstance deviceInstance in availableDevices) {
                if (!this.IsControllerKnown(deviceInstance)) {
                    this.AddController(deviceInstance);
                }
            }
        }

        private bool IsControllerKnown (DeviceInstance deviceInstance)
        {
            foreach (Controller controller in this.controllers) {
                if (controller.Device.InstanceGuid == deviceInstance.InstanceGuid) {
                    return true;
                }
            }

            return false;
        }

        private void AddController (DeviceInstance deviceInstance)
        {
            try {
                Controller controller = new Controller(this.directInput, deviceInstance);
                controller.AnalogInput += this.HandleAnalogInput;
                controller.DigitalInput += this.HandleDigitalInput;
                this.controllers.Add(controller);
                DebugHelper.Log($"Monitoring controller: { deviceInstance.InstanceName }");
            } catch (Exception e) {
                DebugHelper.Log($"Controller setup failed: { deviceInstance.InstanceName }");
                DebugHelper.Log(e);
            }
        }

        private void RemoveController (int controllerIndex)
        {
            Controller controller = this.controllers[controllerIndex];
            controller.AnalogInput -= this.HandleAnalogInput;
            controller.DigitalInput -= this.HandleDigitalInput;
            controller.Dispose();
            this.controllers.RemoveAt(controllerIndex);
        }

        private void DisposeControllers ()
        {
            for (int controllerIndex = this.controllers.Count - 1; controllerIndex >= 0; controllerIndex--) {
                this.RemoveController(controllerIndex);
            }
        }

        private void UnsubscribeCurrentPlayer ()
        {
            if (null != this.currentPlayer && null != this.currentPlayer.Controller) {
                this.currentPlayer.Controller.ControlledEntityChanged -= this.UpdateCurrentControlUnit;
            }

            this.currentPlayer = null;
        }

        private void ClearCurrentGrid ()
        {
            if (null != this.currentGrid) {
                this.currentGrid.OnBlockAdded -= this.UpdateCurrentGridBlocks;
                this.currentGrid.OnBlockRemoved -= this.UpdateCurrentGridBlocks;
                this.currentGrid = null;
            }

            this.currentProgrammableBlocks.Clear();
        }

        private void DisposeDirectInput ()
        {
            if (null == this.directInput)
                return;

            this.directInput.Dispose();
            this.directInput = null;
        }
    }
}
