using System;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppSprocket;
using Il2CppSprocket.DamageModelling;
using Il2CppSprocket.Gameplay.VehicleControl;
using Il2CppSprocket.VehicleControl;
using Il2CppSprocket.Vehicles.Cannons;
using Il2CppSprocket.Vehicles.CrewSystems;
using Il2CppSprocket.Vehicles.Weapons;
using Il2CppSprocket.Vehicles.Weapons.Cannons;
using MelonLoader;
using SprocketDepth;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

[assembly: MelonInfo(
    typeof(SprocketLaserRangefinder.SprocketLaserRangefinderMod),
    "Sprocket Laser Rangefinder",
    "0.1.1",
    "furryAxw")]
[assembly: MelonGame("HD", "Sprocket")]

namespace SprocketLaserRangefinder
{
    public sealed class SprocketLaserRangefinderMod : MelonMod
    {
        private const string DepthPassName =
            "Sprocket Laser Rangefinder Depth Sampler";
        private const int VirtualKeyRange = 0xA2; // VK_LCONTROL
        private const int VirtualKeyClear = 0x5A; // Z
        private const int VirtualKeyLeadToggle = 0x4C; // L
        private const float MinimumRangeMeters = 20.0f;
        private const float MaximumRangeMeters = 5000.0f;
        private const float BallisticRefreshSeconds = 0.05f;
        private const float ControllerRefreshSeconds = 0.50f;
        private const float MaximumDepthFrameRotationDegrees = 0.20f;

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);

        internal static SprocketLaserRangefinderMod? Instance { get; private set; }

        private readonly StringBuilder hudText = new(256);
        private HarmonyLib.Harmony? harmony;
        private HdrpDepthMapRenderer? depthRenderer;
        private VehicleController? controller;
        private ProjectilesMain? projectilesMain;
        private GameObject? depthPassObject;
        private CustomPassVolume? depthPassVolume;
        private FullScreenCustomPass? depthPass;
        private Camera? boundCamera;
        private Cannon? currentCannon;
        private CannonBehaviour? currentBehaviour;
        private BallisticModel currentModel;
        private bool currentModelValid;

        private bool rangeKeyWasDown;
        private bool clearKeyWasDown;
        private bool leadKeyWasDown;
        private bool scopeWasActive;
        private bool depthPrimed;
        private bool depthSampleRequested;
        private bool depthReadbackInFlight;
        private bool depthFrameMetadataValid;
        private bool stableFrameWaitLogged;
        private Vector2 depthFrameViewport;
        private float depthFrameAxisCosine;
        private Quaternion depthFrameCameraRotation;
        private float depthReadbackAxisCosine;
        private int depthBindingGeneration;
        private int rangeRequestGeneration;
        private int depthReadbackBindingGeneration;
        private int depthReadbackRangeGeneration;
        private System.Action<AsyncGPUReadbackRequest>? managedDepthReadbackHandler;
        private Il2CppSystem.Action<AsyncGPUReadbackRequest>? depthReadbackHandler;

        // Fire-control state deliberately contains no ranged world point.
        private bool rangeValid;
        private float rangedDistance;
        private float normalizedDepthValue;
        private bool solutionValid;
        private BallisticSolution solution;
        // Relative angular table; no ranged world point or target is retained.
        private Quaternion localAngularTable = Quaternion.identity;
        private bool aimCommandValid;
        private bool operatorAimRayValid;
        private Ray operatorAimRay;
        private bool nativeAimInjectionPrepared;
        private bool leadCompensationEnabled;
        private GUIStyle? hudLabelStyle;
        private float nextSolutionRefreshTime;
        private float nextControllerRefreshTime;
        private int lastLoggedSolutionGeneration = -1;
        private string lastStatus = "Press Left Ctrl while scoped to range";

        public override void OnInitializeMelon()
        {
            Instance = this;
            try
            {
                harmony = new HarmonyLib.Harmony(
                    "furryAxw.SprocketLaserRangefinder");
                harmony.PatchAll(typeof(SprocketLaserRangefinderMod).Assembly);
            }
            catch (Exception exception)
            {
                LoggerInstance.Error(
                    $"[SLRF] Harmony patch registration failed: {exception}");
                harmony = null;
                Instance = null;
                return;
            }

            depthRenderer = new HdrpDepthMapRenderer(MaximumRangeMeters);
            LoggerInstance.Msg(
                "Sprocket Laser Rangefinder 0.1.1 initialized.");
            LoggerInstance.Msg(
                "[SLRF] depth-only scalar range; no ranged world point; " +
                "ballistics=live cannon data + native curves + semi-implicit Euler");
        }

        public override void OnUpdate()
        {
            RefreshControllerAndBindings();

            bool rangeDown = IsKeyDown(VirtualKeyRange);
            if (rangeDown && !rangeKeyWasDown)
                RequestRange();
            rangeKeyWasDown = rangeDown;

            bool clearDown = IsKeyDown(VirtualKeyClear);
            if (clearDown && !clearKeyWasDown && HasActiveRangeState())
            {
                ClearRange("Range cleared manually");
                LoggerInstance.Msg("[SLRF] range-cleared manual");
            }
            clearKeyWasDown = clearDown;

            bool leadDown = IsKeyDown(VirtualKeyLeadToggle);
            if (leadDown && !leadKeyWasDown && IsScopeActive())
                ToggleLeadCompensation();
            leadKeyWasDown = leadDown;

            TryRefreshCurrentCannon();
        }

        public override void OnGUI()
        {
            if (!IsScopeActive())
                return;

            BuildHudText();
            const float width = 430.0f * 2.0f / 3.0f;
            const float height = 54.0f;
            float windowX = Mathf.Max(0.0f, (Screen.width - width) * 0.5f);
            const float windowY = 8.0f;
            hudLabelStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Box(new Rect(windowX, windowY, width, height), string.Empty);
            GUI.Label(
                new Rect(
                    windowX + 10.0f,
                    windowY + 6.0f,
                    width - 20.0f,
                    height - 10.0f),
                hudText.ToString(),
                hudLabelStyle);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            ReleaseDepthBinding();
            controller = null;
            currentCannon = null;
            currentBehaviour = null;
            currentModelValid = false;
            projectilesMain = null;
            scopeWasActive = false;
            ClearRange("Scene changed");
            nextControllerRefreshTime = 0.0f;
        }

        public override void OnDeinitializeMelon()
        {
            ReleaseDepthBinding();
            depthRenderer?.Dispose();
            depthRenderer = null;
            harmony?.UnpatchSelf();
            harmony = null;
            Instance = null;
        }

        internal bool ExecuteOwnedDepthPass(
            FullScreenCustomPass pass,
            CustomPassContext context)
        {
            if (depthPass == null ||
                pass == null ||
                pass.Pointer != depthPass.Pointer)
            {
                return false;
            }

            if (depthRenderer == null || boundCamera == null)
                return true;

            Camera? executingCamera = context.hdCamera == null
                ? null
                : context.hdCamera.camera;
            if (executingCamera == null ||
                executingCamera.GetInstanceID() != boundCamera.GetInstanceID())
            {
                return true;
            }

            if (depthSampleRequested && depthPrimed && !depthReadbackInFlight)
                TryBeginDepthReadback();

            bool recorded = depthRenderer.TryRecord(
                context,
                DepthMapOutput.TextureOnly);
            if (recorded)
            {
                depthPrimed = true;
                CaptureDepthFrameMetadata();
            }
            else
            {
                lastStatus = "Depth record failed";
                LoggerInstance.Warning(
                    $"[SLRF] depth-record-failed {depthRenderer.LastError}");
            }

            return true;
        }

        internal void PrepareAutomaticAim(
            VehicleController candidate,
            Ray inputAimRay)
        {
            if (controller == null ||
                candidate == null ||
                candidate.Pointer != controller.Pointer)
            {
                return;
            }

            operatorAimRayValid =
                IsFinite(inputAimRay.origin) &&
                IsFinite(inputAimRay.direction) &&
                inputAimRay.direction.sqrMagnitude > 1e-8f;
            operatorAimRay = inputAimRay;
            nativeAimInjectionPrepared = false;
            aimCommandValid = false;

            if (!rangeValid ||
                candidate.ScopeControl == null ||
                !candidate.ScopeControl.Scoped)
            {
                return;
            }

            if ((!solutionValid ||
                 Time.unscaledTime >= nextSolutionRefreshTime) &&
                operatorAimRayValid)
            {
                nextSolutionRefreshTime =
                    Time.unscaledTime + BallisticRefreshSeconds;
                RefreshBallisticSolution(operatorAimRay);
            }

            nativeAimInjectionPrepared =
                solutionValid && operatorAimRayValid;
        }

        internal void InjectNativeAimPosition(
            GunLayer layer,
            ref Vector3 position)
        {
            if (!nativeAimInjectionPrepared ||
                !solutionValid ||
                !operatorAimRayValid ||
                layer == null ||
                !IsControlledGunLayer(layer))
            {
                return;
            }

            IAimable? target = layer.ControlTarget;
            if (target == null)
                return;

            IReadOnlyAimable? readOnlyTarget =
                target.TryCast<IReadOnlyAimable>();
            if (readOnlyTarget == null)
                return;

            Vector3 pivotPosition = readOnlyTarget.PivotPosition;
            Vector3 offset = position - pivotPosition;
            float distance = offset.magnitude;
            if (!float.IsFinite(distance) || distance <= 0.001f)
                return;

            if (!TryCreateLookFrame(
                    operatorAimRay.direction,
                    out Quaternion commandLosFrame))
            {
                return;
            }

            // UpdateControl's aim ray is the stable operator LOS. Camera
            // Transform.forward follows the weapon rig on some vehicles and
            // would feed the previous command back into the next one.
            Vector3 commandedDirection =
                (commandLosFrame *
                 localAngularTable *
                 Vector3.forward).normalized;
            if (!IsFinite(commandedDirection) ||
                commandedDirection.sqrMagnitude <= 1e-8f)
            {
                return;
            }

            position = pivotPosition + commandedDirection * distance;
            aimCommandValid = true;
        }

        internal void CompleteAutomaticAim(VehicleController candidate)
        {
            if (controller == null ||
                candidate == null ||
                candidate.Pointer != controller.Pointer)
            {
                return;
            }

            nativeAimInjectionPrepared = false;
            if (!rangeValid ||
                !solutionValid ||
                candidate.ScopeControl == null ||
                !candidate.ScopeControl.Scoped)
            {
                return;
            }

            if (!aimCommandValid)
                lastStatus = "No native gun-layer target";
        }

        private void RefreshControllerAndBindings()
        {
            if ((controller == null || controller.gameObject == null) &&
                Time.unscaledTime >= nextControllerRefreshTime)
            {
                nextControllerRefreshTime =
                    Time.unscaledTime + ControllerRefreshSeconds;
                controller = UnityEngine.Object.FindObjectOfType<VehicleController>();
            }

            Camera? desiredCamera = null;
            var scopeControl = controller?.ScopeControl;
            bool scopeActive = scopeControl != null && scopeControl.Scoped;
            if (scopeActive)
                desiredCamera = scopeControl!.Camera;

            if (!scopeActive || desiredCamera == null)
            {
                if (scopeWasActive)
                {
                    LoggerInstance.Msg("[SLRF] range-retained scope-exited");
                }

                scopeWasActive = false;
                ReleaseDepthBinding();
                return;
            }

            scopeWasActive = true;
            if (boundCamera != null &&
                boundCamera.gameObject != null &&
                boundCamera.GetInstanceID() == desiredCamera.GetInstanceID() &&
                depthPassObject != null)
            {
                return;
            }

            BindDepthPass(desiredCamera);
        }

        private void BindDepthPass(Camera camera)
        {
            ReleaseDepthBinding();
            try
            {
                depthPassObject = new GameObject(
                    "Sprocket Laser Rangefinder Depth Host")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                depthPassObject.SetActive(false);
                depthPassObject.transform.SetParent(camera.transform, false);
                depthPassVolume =
                    depthPassObject.AddComponent<CustomPassVolume>();
                depthPassVolume.isGlobal = true;
                depthPassVolume.useTargetCamera = true;
                depthPassVolume.targetCamera = camera;
                depthPassVolume.priority = 1100.0f;
                depthPassVolume.injectionPoint =
                    CustomPassInjectionPoint.AfterPostProcess;

                CustomPass basePass = depthPassVolume.AddPassOfType(
                    Il2CppType.Of<FullScreenCustomPass>());
                depthPass = basePass.TryCast<FullScreenCustomPass>();
                if (depthPass == null)
                    throw new InvalidOperationException(
                        "Could not create HDRP depth pass.");

                depthPass.name = DepthPassName;
                depthPass.targetColorBuffer = CustomPass.TargetBuffer.Camera;
                depthPass.targetDepthBuffer = CustomPass.TargetBuffer.None;
                depthPass.clearFlags = ClearFlag.None;
                depthPass.enabled = true;
                boundCamera = camera;
                depthPrimed = false;
                depthFrameMetadataValid = false;
                depthPassObject.SetActive(true);
                LoggerInstance.Msg(
                    $"[SLRF] depth-bound camera={BuildTransformPath(camera.transform)}");
            }
            catch (Exception exception)
            {
                LoggerInstance.Error($"[SLRF] depth-bind-failed {exception}");
                ReleaseDepthBinding();
            }
        }

        private void ReleaseDepthBinding()
        {
            if (depthPass != null ||
                depthPassObject != null ||
                boundCamera != null)
            {
                depthBindingGeneration++;
            }

            if (depthPass != null)
                depthPass.enabled = false;
            if (depthPassObject != null)
                UnityEngine.Object.Destroy(depthPassObject);
            depthPass = null;
            depthPassVolume = null;
            depthPassObject = null;
            boundCamera = null;
            depthPrimed = false;
            depthFrameMetadataValid = false;
            depthSampleRequested = false;
            stableFrameWaitLogged = false;
        }

        private void RequestRange()
        {
            if (!IsScopeActive() || boundCamera == null)
            {
                lastStatus = "Range rejected: enter the scope";
                return;
            }

            rangeRequestGeneration++;
            rangeValid = false;
            solutionValid = false;
            localAngularTable = Quaternion.identity;
            aimCommandValid = false;
            operatorAimRayValid = false;
            operatorAimRay = default;
            nativeAimInjectionPrepared = false;
            depthSampleRequested = true;
            stableFrameWaitLogged = false;
            nextSolutionRefreshTime = 0.0f;
            lastStatus = depthPrimed
                ? "Ranging: waiting for depth"
                : "Ranging: priming depth";
            LoggerInstance.Msg(
                $"[SLRF] range-request generation={rangeRequestGeneration}");
        }

        private bool TryGetCurrentScopeRay(
            out Vector2 viewport,
            out Ray ray)
        {
            viewport = default;
            ray = default;
            if (controller == null || controller.ScopeControl == null)
                return false;

            Camera? camera = boundCamera ?? controller.ScopeControl.Camera;
            if (camera == null)
                return false;

            viewport = ResolveAimViewport(controller.AimPointInViewport);
            ray = camera.ViewportPointToRay(
                new Vector3(viewport.x, viewport.y, 0.0f));
            return IsFinite(ray.origin) &&
                   IsFinite(ray.direction) &&
                   ray.direction.sqrMagnitude > 1e-8f;
        }

        private void CaptureDepthFrameMetadata()
        {
            if (!TryGetCurrentScopeRay(out Vector2 viewport, out Ray ray) ||
                boundCamera == null)
            {
                depthFrameMetadataValid = false;
                return;
            }

            depthFrameViewport = viewport;
            depthFrameAxisCosine = Mathf.Max(
                0.001f,
                Vector3.Dot(
                    ray.direction.normalized,
                    boundCamera.transform.forward));
            depthFrameCameraRotation = boundCamera.transform.rotation;
            depthFrameMetadataValid = true;
        }

        private void TryBeginDepthReadback()
        {
            RenderTexture? texture = depthRenderer?.NormalizedDepthTexture;
            if (texture == null ||
                boundCamera == null ||
                !depthFrameMetadataValid)
            {
                return;
            }

            try
            {
                float rotation = Quaternion.Angle(
                    depthFrameCameraRotation,
                    boundCamera.transform.rotation);
                if (rotation > MaximumDepthFrameRotationDegrees)
                {
                    lastStatus = "Ranging: waiting for stable scope";
                    if (!stableFrameWaitLogged)
                    {
                        stableFrameWaitLogged = true;
                        LoggerInstance.Msg(
                            $"[SLRF] depth-wait-frame-rotation " +
                            $"rotation={rotation:F3}deg");
                    }
                    return;
                }

                int x = Mathf.Clamp(
                    Mathf.FloorToInt(depthFrameViewport.x * texture.width),
                    0,
                    texture.width - 1);
                int y = Mathf.Clamp(
                    Mathf.FloorToInt(depthFrameViewport.y * texture.height),
                    0,
                    texture.height - 1);

                depthReadbackAxisCosine = depthFrameAxisCosine;
                depthReadbackBindingGeneration = depthBindingGeneration;
                depthReadbackRangeGeneration = rangeRequestGeneration;
                depthReadbackInFlight = true;
                depthSampleRequested = false;
                stableFrameWaitLogged = false;
                managedDepthReadbackHandler ??= OnDepthReadbackComplete;
                depthReadbackHandler ??=
                    DelegateSupport.ConvertDelegate<
                        Il2CppSystem.Action<AsyncGPUReadbackRequest>>(
                        managedDepthReadbackHandler);

                AsyncGPUReadback.Request(
                    texture,
                    0,
                    x,
                    1,
                    y,
                    1,
                    0,
                    1,
                    depthReadbackHandler);
            }
            catch (Exception exception)
            {
                depthReadbackInFlight = false;
                lastStatus = "Depth readback request failed";
                LoggerInstance.Error(
                    $"[SLRF] depth-readback-request-failed {exception}");
            }
        }

        private void OnDepthReadbackComplete(AsyncGPUReadbackRequest request)
        {
            depthReadbackInFlight = false;
            try
            {
                if (depthReadbackBindingGeneration != depthBindingGeneration ||
                    depthReadbackRangeGeneration != rangeRequestGeneration ||
                    !IsScopeActive())
                {
                    LoggerInstance.Msg(
                        "[SLRF] stale depth readback discarded");
                    return;
                }

                if (request.hasError)
                {
                    lastStatus = "Depth readback failed";
                    LoggerInstance.Warning("[SLRF] depth-readback-error");
                    return;
                }

                var data = request.GetData<float>(0);
                if (data.Length < 1)
                    return;

                normalizedDepthValue = data[0];
                float eyeDepth =
                    (1.0f - normalizedDepthValue) * MaximumRangeMeters;
                float rayDistance = eyeDepth / depthReadbackAxisCosine;
                if (float.IsFinite(rayDistance) &&
                    rayDistance >= 0.0f &&
                    rayDistance < MinimumRangeMeters)
                {
                    lastStatus = $"Target closer than {MinimumRangeMeters:F0} m";
                    LoggerInstance.Msg(
                        $"[SLRF] depth=too-close distance={rayDistance:F2}m " +
                        $"minimum={MinimumRangeMeters:F0}m");
                    return;
                }

                bool valid = float.IsFinite(rayDistance) &&
                             normalizedDepthValue > 0.00001f &&
                             rayDistance >= MinimumRangeMeters &&
                             rayDistance < MaximumRangeMeters - 1.0f;
                if (!valid)
                {
                    lastStatus = "No depth return";
                    LoggerInstance.Msg(
                        $"[SLRF] depth=no-return q={normalizedDepthValue:F6}");
                    return;
                }

                CommitRange(rayDistance);
                LoggerInstance.Msg(
                    $"[SLRF] ranged depth={rayDistance:F2}m " +
                    $"q={normalizedDepthValue:F6}");
            }
            catch (Exception exception)
            {
                lastStatus = "Depth callback failed";
                LoggerInstance.Error($"[SLRF] depth-callback-failed {exception}");
            }
        }

        private bool TryRefreshCurrentCannon()
        {
            currentModelValid = false;
            if (controller == null || controller.activeGunner == null)
                return false;

            GameObject? weaponObject = controller.activeGunner.WeaponGameObject;
            if (weaponObject == null)
                return false;

            Cannon? cannon = weaponObject.GetComponent<Cannon>() ??
                             weaponObject.GetComponentInParent<Cannon>();
            if (cannon == null ||
                cannon.Behaviour == null ||
                cannon.ShellBlueprint == null)
            {
                return false;
            }

            projectilesMain ??=
                UnityEngine.Object.FindObjectOfType<ProjectilesMain>();
            if (projectilesMain == null ||
                projectilesMain.shellDragCoefficientCurve == null ||
                projectilesMain.shellMachNumberDragCoefficientCurve == null)
            {
                return false;
            }

            float diameterMeters =
                cannon.ShellBlueprint.Diameter * 0.001f;
            float massKilograms = cannon.ShellBlueprint.ProjectileMass;
            if (diameterMeters <= 0.0f || massKilograms <= 0.0f)
                return false;

            currentCannon = cannon;
            currentBehaviour = cannon.Behaviour;
            currentModel = new BallisticModel(
                diameterMeters,
                massKilograms,
                projectilesMain.shellDragCoefficientCurve,
                projectilesMain.shellMachNumberDragCoefficientCurve);
            currentModelValid = true;
            return true;
        }

        private void RefreshBallisticSolution(Ray commandLosRay)
        {
            if (!currentModelValid ||
                currentCannon == null ||
                currentBehaviour == null ||
                !TryGetMuzzle(out _, out Vector3 muzzlePosition))
            {
                solutionValid = false;
                lastStatus = "No active cannon";
                return;
            }

            float muzzleVelocity = ResolveMuzzleVelocity();
            Vector3 inheritedVelocity = leadCompensationEnabled
                ? currentBehaviour.InheritedVelocity
                : Vector3.zero;
            solutionValid = BallisticSolver.TrySolveLowArc(
                currentModel,
                commandLosRay,
                rangedDistance,
                muzzlePosition,
                muzzleVelocity,
                inheritedVelocity,
                Time.fixedDeltaTime,
                out BallisticSolution newSolution);
            if (!solutionValid)
            {
                lastStatus = "No low-arc ballistic solution";
                return;
            }

            Vector3 commandLosDirection = commandLosRay.direction.normalized;
            if (!TryCreateLookFrame(
                    commandLosDirection,
                    out Quaternion commandLosFrame) ||
                !TryCreateLookFrame(
                    newSolution.Direction,
                    out Quaternion solutionFrame))
            {
                solutionValid = false;
                lastStatus = "Invalid angular table";
                return;
            }

            solution = newSolution;
            localAngularTable =
                Quaternion.Inverse(commandLosFrame) * solutionFrame;
            lastStatus =
                $"Table {solution.ElevationCorrectionDegrees:+0.00;-0.00;0.00} deg";

            if (lastLoggedSolutionGeneration != rangeRequestGeneration)
            {
                lastLoggedSolutionGeneration = rangeRequestGeneration;
                LoggerInstance.Msg(
                    $"[SLRF] table range={rangedDistance:F2}m " +
                    $"correction={solution.ElevationCorrectionDegrees:+0.000;-0.000;0.000}deg " +
                    $"tof={solution.TimeOfFlight:F3}s " +
                    $"miss={solution.MissDistance:F3}m " +
                    $"lead={(leadCompensationEnabled ? "on" : "off")}");
            }
        }

        private bool TryGetMuzzle(
            out Transform muzzleTransform,
            out Vector3 muzzlePosition)
        {
            muzzleTransform = null!;
            muzzlePosition = default;
            if (currentBehaviour == null || currentBehaviour.barrels == null)
                return false;

            var barrels = currentBehaviour.barrels;
            if (barrels.Length == 0 ||
                barrels[barrels.Length - 1] == null)
            {
                return false;
            }

            muzzleTransform = barrels[barrels.Length - 1].transform;
            if (muzzleTransform == null)
                return false;
            muzzlePosition = currentBehaviour.ShellSpawnPosition;
            return true;
        }

        private float ResolveMuzzleVelocity()
        {
            if (currentCannon == null)
                return 0.0f;
            if (currentCannon.muzzleVelocity > 0.0f)
                return currentCannon.muzzleVelocity;
            return currentCannon.Blueprint == null
                ? 0.0f
                : currentCannon.Blueprint.MuzzleVelocity;
        }

        private void CommitRange(float distance)
        {
            rangeValid = true;
            rangedDistance = distance;
            solutionValid = false;
            localAngularTable = Quaternion.identity;
            aimCommandValid = false;
            operatorAimRayValid = false;
            operatorAimRay = default;
            nativeAimInjectionPrepared = false;
            nextSolutionRefreshTime = 0.0f;
            lastStatus = $"Range {distance:F1} m; applying table";
        }

        private void ClearRange(string reason)
        {
            rangeRequestGeneration++;
            rangeValid = false;
            rangedDistance = 0.0f;
            solutionValid = false;
            solution = default;
            localAngularTable = Quaternion.identity;
            aimCommandValid = false;
            operatorAimRayValid = false;
            operatorAimRay = default;
            nativeAimInjectionPrepared = false;
            depthSampleRequested = false;
            stableFrameWaitLogged = false;
            nextSolutionRefreshTime = 0.0f;
            lastStatus = reason;
        }

        private void BuildHudText()
        {
            hudText.Clear();
            hudText.AppendLine(
                $"LASER RANGEFINDER   LEAD {(leadCompensationEnabled ? "ON" : "OFF")}");
            hudText.Append("RANGE ");
            if (depthSampleRequested ||
                (depthReadbackInFlight &&
                 depthReadbackRangeGeneration == rangeRequestGeneration))
                hudText.Append("RANGING...");
            else if (rangeValid)
                hudText.Append($"{rangedDistance:F1}m");
            else
                hudText.Append("----");

            hudText.Append(" TABLE ");
            if (solutionValid)
            {
                hudText.Append(
                    $"{solution.ElevationCorrectionDegrees:+0.00;-0.00;0.00}°" +
                    $" TOF {solution.TimeOfFlight:F2}s");
            }
            else
            {
                hudText.Append(rangeValid ? "SOLVING..." : "----");
            }
        }

        private void ToggleLeadCompensation()
        {
            leadCompensationEnabled = !leadCompensationEnabled;
            solutionValid = false;
            solution = default;
            localAngularTable = Quaternion.identity;
            aimCommandValid = false;
            operatorAimRayValid = false;
            operatorAimRay = default;
            nativeAimInjectionPrepared = false;
            nextSolutionRefreshTime = 0.0f;
            lastLoggedSolutionGeneration = -1;
            lastStatus = leadCompensationEnabled
                ? "Vehicle-motion lead enabled"
                : "Vehicle-motion lead disabled";
            LoggerInstance.Msg(
                $"[SLRF] lead-compensation=" +
                $"{(leadCompensationEnabled ? "on" : "off")}");
        }

        private bool IsScopeActive()
        {
            return controller != null &&
                   controller.ScopeControl != null &&
                   controller.ScopeControl.Scoped;
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetKeyState(virtualKey) & 0x8000) != 0;
        }

        private bool HasActiveRangeState()
        {
            return rangeValid ||
                   solutionValid ||
                   depthSampleRequested ||
                   (depthReadbackInFlight &&
                    depthReadbackRangeGeneration == rangeRequestGeneration);
        }

        private bool IsControlledGunLayer(GunLayer candidate)
        {
            var layers = controller?.gunLayers;
            if (layers == null)
                return false;

            for (int index = 0; index < layers.Length; index++)
            {
                IGunLayerControl? layer = layers[index];
                GunLayer? concreteLayer = layer?.TryCast<GunLayer>();
                if (concreteLayer != null &&
                    concreteLayer.Pointer == candidate.Pointer)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryCreateLookFrame(
            Vector3 direction,
            out Quaternion frame)
        {
            frame = Quaternion.identity;
            if (!IsFinite(direction) || direction.sqrMagnitude <= 1e-8f)
                return false;

            Vector3 normalized = direction.normalized;
            Vector3 referenceUp =
                Mathf.Abs(Vector3.Dot(normalized, Vector3.up)) < 0.999f
                    ? Vector3.up
                    : Vector3.forward;
            frame = Quaternion.LookRotation(normalized, referenceUp);
            return float.IsFinite(frame.x) &&
                   float.IsFinite(frame.y) &&
                   float.IsFinite(frame.z) &&
                   float.IsFinite(frame.w);
        }

        private static Vector2 ResolveAimViewport(Vector2 viewport)
        {
            if (!float.IsFinite(viewport.x) ||
                !float.IsFinite(viewport.y) ||
                viewport.x < 0.0f || viewport.x > 1.0f ||
                viewport.y < 0.0f || viewport.y > 1.0f)
            {
                return new Vector2(0.5f, 0.5f);
            }

            return viewport;
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) &&
                   float.IsFinite(value.y) &&
                   float.IsFinite(value.z);
        }

        private static string BuildTransformPath(Transform? transform)
        {
            if (transform == null)
                return "<none>";

            string path = transform.name ?? "<unnamed>";
            Transform? current = transform.parent;
            while (current != null)
            {
                path = $"{current.name ?? "<unnamed>"}/{path}";
                current = current.parent;
            }
            return path;
        }
    }

    [HarmonyPatch(typeof(FullScreenCustomPass), nameof(FullScreenCustomPass.Execute))]
    internal static class LaserRangefinderDepthPassPatch
    {
        private static bool Prefix(
            FullScreenCustomPass __instance,
            CustomPassContext __0)
        {
            SprocketLaserRangefinderMod? mod =
                SprocketLaserRangefinderMod.Instance;
            if (mod == null)
                return true;
            return !mod.ExecuteOwnedDepthPass(__instance, __0);
        }
    }

    [HarmonyPatch(typeof(VehicleController), nameof(VehicleController.UpdateControl))]
    internal static class LaserRangefinderAimPatch
    {
        private static void Prefix(VehicleController __instance, Ray __1)
        {
            SprocketLaserRangefinderMod.Instance?.PrepareAutomaticAim(
                __instance,
                __1);
        }

        private static void Postfix(VehicleController __instance)
        {
            SprocketLaserRangefinderMod.Instance?.CompleteAutomaticAim(
                __instance);
        }
    }

    [HarmonyPatch(typeof(GunLayer), nameof(GunLayer.AimAtPosition))]
    internal static class LaserRangefinderGunLayerAimPatch
    {
        private static void Prefix(GunLayer __instance, ref Vector3 position)
        {
            SprocketLaserRangefinderMod.Instance?.InjectNativeAimPosition(
                __instance,
                ref position);
        }
    }

}
