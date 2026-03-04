using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class NO_EjectFirstPerson : BaseUnityPlugin
{
    public const string PluginGuid = "com.nicho.nuclearoption.ejectfirstperson";
    public const string PluginName = "NO_EjectFirstPerson";
    public const string PluginVersion = "1.1.8";

    private static NO_EjectFirstPerson _instance;
    private Harmony _harmony;

  

    // Camera pose we want right before render (prevents game camera from re-leveling)
    private Vector3 desiredPos;
    private Quaternion desiredRot;
    private bool desiredPoseValid;
    private CameraPoseEnforcer poseEnforcer;
  // Config
    private ConfigEntry<bool> cfgEnabled;
    private ConfigEntry<bool> cfgHideBody;
    private static ConfigEntry<bool> cfgAssumeFollowedIsLocal;
    private ConfigEntry<float> cfgEyeForward;
    private ConfigEntry<float> cfgEyeUp;
    private ConfigEntry<float> cfgEyeRight;
    private ConfigEntry<float> cfgNearClip;
    private ConfigEntry<bool> cfgLockFovToCurrent;
    private ConfigEntry<float> cfgFov;
    private ConfigEntry<bool> cfgDebug;

    // Runtime
    private bool active;
    private PilotDismounted currentPilot;
    private Transform headTransform;
    private Transform rotationBasis;
    private Transform fpAnchor;
    private Vector3 fpLocalPos;
    private Quaternion fpLocalRot = Quaternion.identity;
    private float savedNearClip = 0.2f;
    private float savedFov = 60f;
    private readonly List<Renderer> disabledRenderers = new List<Renderer>();

    // Look
    private float panView;
    private float tiltView;
    private float panSmoothed;
    private float tiltSmoothed;

    private void Awake()
    {
        _instance = this;

        cfgEnabled = Config.Bind("General", "Enabled", true, "Enable first-person camera while ejecting (following PilotDismounted).");
        cfgAssumeFollowedIsLocal = Config.Bind("General", "AssumeFollowedPilotIsLocal", true, "If true, activates whenever the camera is following a PilotDismounted (recommended for single-player). If false, uses network local-player checks.");
        cfgHideBody = Config.Bind("General", "HidePilotRenderers", false, "Hide the local pilot body renderers while in first-person (reduces clipping).");
        cfgEyeForward = Config.Bind("Camera", "EyeForwardOffset", 0.04f, "Meters forward from head bone.");
        cfgEyeUp = Config.Bind("Camera", "EyeUpOffset", 0.02f, "Meters up from head bone.");
        cfgEyeRight = Config.Bind("Camera", "EyeRightOffset", 0.0f, "Meters right from head bone.");
        cfgNearClip = Config.Bind("Camera", "NearClip", 0.03f, "Near clip plane while in first-person.");
        cfgLockFovToCurrent = Config.Bind("Camera", "LockFovToCurrent", true, "If true, keep whatever FOV the player currently has. If false, use FixedFov.");
        cfgFov = Config.Bind("Camera", "FixedFov", 70f, "Fixed FOV used when LockFovToCurrent is false.");
        cfgDebug = Config.Bind("Debug", "VerboseLogs", false, "If true, logs which PilotDismounted instance the camera is attached to.");

        try
        {
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Patches));
        }
        catch (Exception e)
        {
            Logger.LogError(e);
        }
    }

    private void OnDestroy()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        DisableFirstPerson();
    }

    private void LateUpdate()
    {
        if (!cfgEnabled.Value) { if (active) DisableFirstPerson(); return; }

        var camMgr = SceneSingleton<CameraStateManager>.i;
        if (camMgr == null || camMgr.mainCamera == null) { if (active) DisableFirstPerson(); return; }

        // Activate if we're following a PilotDismounted that belongs to the local player
        var pd = camMgr.followingUnit as PilotDismounted;
        if (pd != null && (cfgAssumeFollowedIsLocal.Value || IsLocal(pd)))
        {
            if (!active || currentPilot != pd)
                EnableFirstPerson(pd);

            UpdateLookInput();
            ApplyFirstPersonTransform(camMgr);
        }
        else
        {
            if (active) DisableFirstPerson();
        }
    }

    private static bool IsLocal(PilotDismounted pd)
    {
        try
        {
            // GameManager.IsLocalPlayer(Player) exists in game code.
            return pd != null && pd.Networkplayer != null && GameManager.IsLocalPlayer(pd.Networkplayer);
        }
        catch
        {
            return false;
        }
    }

    private void EnableFirstPerson(PilotDismounted pd)
    {
        DisableFirstPerson(); // reset previous

        currentPilot = pd;
        active = true;
        if (cfgDebug.Value)
            Logger.LogInfo($"[EjectFP] Attach -> PilotDismounted instanceID={pd.GetInstanceID()} name={pd.gameObject.name}");

        var camMgr = SceneSingleton<CameraStateManager>.i;
        if (camMgr != null && camMgr.mainCamera != null)
        {
            savedNearClip = camMgr.mainCamera.nearClipPlane;
            savedFov = camMgr.mainCamera.fieldOfView;
            camMgr.mainCamera.nearClipPlane = Mathf.Max(0.001f, cfgNearClip.Value);
            if (!cfgLockFovToCurrent.Value) camMgr.mainCamera.fieldOfView = cfgFov.Value;
        

            // Ensure we enforce our pose right before render so the game cannot re-level roll.
            poseEnforcer = camMgr.mainCamera.GetComponent<CameraPoseEnforcer>();
            if (poseEnforcer == null) poseEnforcer = camMgr.mainCamera.gameObject.AddComponent<CameraPoseEnforcer>();
            poseEnforcer.owner = this;

            // IMPORTANT:
            // The game's orbit camera forcibly levels roll using Vector3.up.
            // While we're in first-person, switch to freeState so the orbit state's
            // LookRotation/LookAt code stops re-leveling the view.
            try
            {
                if (camMgr.currentState != camMgr.freeState)
                {
                    camMgr.freeState.DontResetRotationFlag = true;
                    camMgr.SwitchState(camMgr.freeState);
                }
                // Ensure the camera is not parented under cameraPivot (orbit state parents it).
                if (camMgr.transform.parent != null) camMgr.transform.SetParent(null);
                if (camMgr.cameraPivot != null && camMgr.cameraPivot.parent != null) camMgr.cameraPivot.SetParent(null);
            }
            catch { }

        }

        headTransform = ResolveHeadTransform(pd);
        rotationBasis = ResolveRotationBasis(pd, headTransform);
        // Create/attach an anchor under the best physics-following basis so the camera is truly "rigged" to the pilot body.
        try
        {
            var parent = rotationBasis != null ? rotationBasis : pd.transform;
            if (fpAnchor == null)
            {
                var go = new GameObject("EjectFPAnchor");
                go.hideFlags = HideFlags.HideAndDontSave;
                fpAnchor = go.transform;
            }
            fpAnchor.SetParent(parent, worldPositionStays: true);

            // Initialize pose immediately
            Transform posAnchor = headTransform != null ? headTransform : parent;
            fpAnchor.position = posAnchor.position;
            fpAnchor.rotation = parent.rotation;

            fpLocalPos = new Vector3(cfgEyeRight.Value, cfgEyeUp.Value, cfgEyeForward.Value);
            fpLocalRot = Quaternion.identity;
        }
        catch { }


        panView = 0f; tiltView = 0f;
        panSmoothed = 0f; tiltSmoothed = 0f;

        if (cfgHideBody.Value)
            HidePilotRenderers(pd);

        Logger.LogInfo($"Enabled first-person eject camera for {pd}");
    }

    private void DisableFirstPerson()
    {
        if (!active) return;

        var camMgr = SceneSingleton<CameraStateManager>.i;
        if (camMgr != null && camMgr.mainCamera != null)
        {
            camMgr.mainCamera.nearClipPlane = savedNearClip;
            if (!cfgLockFovToCurrent.Value) camMgr.mainCamera.fieldOfView = savedFov;
        }

        RestorePilotRenderers();
        try
        {
            var camMgr2 = SceneSingleton<CameraStateManager>.i;
            if (camMgr2 != null && camMgr2.mainCamera != null && fpAnchor != null)
            {
                if (camMgr2.mainCamera.transform.parent == fpAnchor)
                    camMgr2.mainCamera.transform.SetParent(null, true);
            }
        }
        catch { }


        active = false;
        currentPilot = null;
        headTransform = null;
        rotationBasis = null;
        desiredPoseValid = false;
        if (poseEnforcer != null) poseEnforcer.owner = null;
    }

    private void ApplyFirstPersonTransform(CameraStateManager camMgr)
    {
        if (currentPilot == null) return;

        // Re-resolve anchors if needed (spawn timing / animator init)
        if (headTransform == null) headTransform = ResolveHeadTransform(currentPilot);
        if (rotationBasis == null) rotationBasis = ResolveRotationBasis(currentPilot, headTransform);

        // Update anchor transforms and desired camera local pose.
        Transform posAnchor = headTransform != null ? headTransform : (rotationBasis != null ? rotationBasis : currentPilot.transform);
        Transform rotBasis = rotationBasis != null ? rotationBasis : (headTransform != null ? headTransform : currentPilot.transform);

        Quaternion baseRot = rotBasis.rotation;

        // Maintain an anchor parented to the physics-following basis so the camera cannot drift away.
        if (fpAnchor != null)
        {
            fpAnchor.position = posAnchor.position;
            fpAnchor.rotation = baseRot;
        }

        // Local offsets (in anchor space) so position + roll/pitch/yaw follows the pilot body.
        fpLocalPos = new Vector3(cfgEyeRight.Value, cfgEyeUp.Value, cfgEyeForward.Value);

        // Free-look relative to the pilot basis (no world-up stabilization).
        fpLocalRot = Quaternion.Euler(tiltSmoothed, panSmoothed, 0f);

        // For the pose enforcer (if something temporarily unparents the camera).
        Vector3 pos = (fpAnchor != null ? fpAnchor.position : posAnchor.position) + (baseRot * fpLocalPos);
        Quaternion rot = baseRot * fpLocalRot;

        desiredPos = pos;
        desiredRot = rot;
        desiredPoseValid = true;

        // Also set now.
        camMgr.mainCamera.transform.SetPositionAndRotation(pos, rot);

    }

    private void UpdateLookInput()
    {
        // Similar to cockpit cam: allow mouse look when cursor is hidden and radial menu not in use,
        // or when the Free Look button is held.
        try
        {
            bool allow = false;
            if (PlayerSettings.virtualJoystickEnabled)
            {
                allow = GameManager.playerInput.GetButton("Free Look");
            }
            else
            {
                allow = (!Cursor.visible && !RadialMenuMain.IsInUse()) || GameManager.playerInput.GetButton("Free Look");
            }

            if (allow)
            {
                float sens = PlayerSettings.viewSensitivity;
                float inv = PlayerSettings.viewInvertPitch ? -1f : 1f;

                panView += GameManager.playerInput.GetAxis("Pan View") * 120f * sens * Time.unscaledDeltaTime;
                tiltView += GameManager.playerInput.GetAxis("Tilt View") * 120f * sens * Time.unscaledDeltaTime * inv;
                CursorManager.Refresh();
            }
            else
            {
                // gently return to center
                panView = Mathf.Lerp(panView, 0f, 6f * Time.unscaledDeltaTime);
                tiltView = Mathf.Lerp(tiltView, 0f, 6f * Time.unscaledDeltaTime);
            }

            panView = Mathf.Clamp(panView, -165f, 165f);
            tiltView = Mathf.Clamp(tiltView, -80f, 80f);

            float smooth = Mathf.Max(PlayerSettings.viewSmoothing, 0.01f);
            panSmoothed = Mathf.Lerp(panSmoothed, panView, Mathf.Min(2f * Time.unscaledDeltaTime / smooth, 1f));
            tiltSmoothed = Mathf.Lerp(tiltSmoothed, tiltView, Mathf.Min(2f * Time.unscaledDeltaTime / smooth, 1f));
        }
        catch
        {
            // If input classes ever change, just keep last values.
        }
    }

    private static Transform ResolveHeadTransform(PilotDismounted pd)
    {
        if (pd == null) return null;

        try
        {
            // Try animator humanoid head
            var animField = AccessTools.Field(typeof(PilotDismounted), "animator");
            var animator = animField != null ? animField.GetValue(pd) as Animator : null;
            if (animator != null)
            {
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null) return head;
            }
        }
        catch { }

        // Fallback: search by common names
        try
        {
            foreach (var t in pd.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n == "head" || n.Contains("head") || n.Contains("helmet") || n.Contains("eyes") || n.Contains("eye"))
                    return t;
            }
        }
        catch { }

        return null;
    }



/// <summary>
/// Pick a transform whose rotation follows the actual ejection physics (roll/pitch/yaw),
/// not an animator-stabilized head bone. We avoid Rigidbody/PhysicsModule references
/// to keep compile-time dependencies minimal.
/// </summary>
private static Transform ResolveRotationBasis(PilotDismounted pd, Transform head)
{
    if (pd == null) return null;

    // Goal: choose a rotation source that actually carries roll/pitch from physics/ragdoll,
    // not something the game "levels" for camera comfort.
    //
    // Best signal is usually a Rigidbody on the pilot body rig (pelvis/spine/chest/head).
    // We resolve Rigidbody via reflection to avoid adding UnityEngine.PhysicsModule references.

    // 1) Prefer a body Rigidbody (pelvis/spine/chest/head) and explicitly avoid seat/eject rigidbodies.
    try
    {
        var rbType = Type.GetType("UnityEngine.Rigidbody, UnityEngine.PhysicsModule");
        if (rbType != null)
        {
            var comps = pd.GetComponentsInChildren(rbType, true);
            Transform best = null;

            int ScoreName(string ln)
            {
                // higher is better
                int s = 0;
                if (ln.Contains("pelvis") || ln.Contains("hip")) s += 100;
                if (ln.Contains("spine")) s += 80;
                if (ln.Contains("chest") || ln.Contains("torso")) s += 70;
                if (ln.Contains("neck")) s += 60;
                if (ln.Contains("head")) s += 50;
                if (ln.Contains("arm") || ln.Contains("leg")) s += 10;

                // penalize seat/eject bits (they can detach)
                if (ln.Contains("seat") || ln.Contains("eject") || ln.Contains("ejection") || ln.Contains("capsule") || ln.Contains("pod")) s -= 200;
                if (ln.Contains("parachute") || ln.Contains("chute")) s -= 100;
                return s;
            }

            int bestScore = int.MinValue;

            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i] as Component;
                if (c == null) continue;

                var t = c.transform;
                if (t == null) continue;

                var ln = (t.name ?? "").ToLowerInvariant();
                int score = ScoreName(ln);

                // Fallback score: deeper transforms tend to be body parts rather than roots
                score += Mathf.Clamp(t.GetSiblingIndex(), 0, 10);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = t;
                }
            }

            if (best != null && bestScore > -50)
                return best;
        }
    }
    catch { }

    // 2) If we have a head, prefer a non-head ancestor inside PilotDismounted (less likely to be stabilized).
    try
    {
        if (head != null)
        {
            var p1 = head.parent;
            var p2 = p1 != null ? p1.parent : null;
            if (p2 != null && p2.IsChildOf(pd.transform)) return p2;
            if (p1 != null && p1.IsChildOf(pd.transform)) return p1;
        }
    }
    catch { }

    // 3) Name-based fallback (avoid seat/eject names if possible)
    Transform bestBody = null;
    Transform bestRootish = null;

    try
    {
        foreach (var t in pd.GetComponentsInChildren<Transform>(true))
        {
            var n = t.name;
            if (string.IsNullOrEmpty(n)) continue;
            string ln = n.ToLowerInvariant();

            if (ln.Contains("seat") || ln.Contains("eject") || ln.Contains("ejection") || ln.Contains("capsule") || ln.Contains("pod"))
                continue;

            if (bestBody == null && (ln.Contains("pelvis") || ln.Contains("hips") || ln.Contains("spine") || ln.Contains("chest") || ln.Contains("torso") || ln.Contains("body")))
                bestBody = t;

            if (bestRootish == null && (ln == "root" || ln.Contains("root") || ln.Contains("pivot") || ln.Contains("main")))
                bestRootish = t;
        }
    }
    catch { }

    if (bestBody != null) return bestBody;
    if (bestRootish != null) return bestRootish;

    // 4) Final fallback: pilot root
    return pd.transform;
}


    private void HidePilotRenderers(PilotDismounted pd)
    {
        disabledRenderers.Clear();
        try
        {
            foreach (var r in pd.GetComponentsInChildren<Renderer>(true))
            {
                // avoid hiding parachute canopy if you want to see it (keep it by default)
                string n = r.name.ToLowerInvariant();
                if (n.Contains("parachute") || n.Contains("chute")) continue;

                if (r.enabled)
                {
                    r.enabled = false;
                    disabledRenderers.Add(r);
                }
            }
        }
        catch { }
    }

    private void RestorePilotRenderers()
    {
        if (disabledRenderers.Count == 0) return;
        foreach (var r in disabledRenderers)
        {
            if (r != null) r.enabled = true;
        }
        disabledRenderers.Clear();
    }

    internal static class Patches
    {
        // Prevent the built-in ORBIT camera from overwriting our roll/pitch/yaw while
        // we're doing first-person ejection view. Orbit state hard-codes Vector3.up
        // which forces a level horizon.
        [HarmonyPatch(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState))]
        [HarmonyPrefix]
        private static bool CameraOrbitState_UpdateState_Prefix(CameraStateManager cam)
        {
            try
            {
                if (_instance == null || !_instance.active) return true;
                var pd = cam != null ? cam.followingUnit as PilotDismounted : null;
                if (pd != null && (cfgAssumeFollowedIsLocal.Value || IsLocal(pd)))
                {
                    // We handle camera placement ourselves (see LateUpdate + CameraPoseEnforcer).
                    return false;
                }
            }
            catch { }
            return true;
        }

        
        // Prevent the built-in FREE camera from drifting away while we're rigged to the pilot anchor.
        [HarmonyPatch(typeof(CameraFreeState), nameof(CameraFreeState.UpdateState))]
        [HarmonyPrefix]
        private static bool CameraFreeState_UpdateState_Prefix(CameraStateManager cam)
        {
            try
            {
                if (_instance == null || !_instance.active) return true;
                var pd = cam != null ? cam.followingUnit as PilotDismounted : null;
                if (pd != null && (cfgAssumeFollowedIsLocal.Value || IsLocal(pd)))
                {
                    return false;
                }
            }
            catch { }
            return true;
        }
// If the camera switches away, ensure we restore things
        [HarmonyPatch(typeof(CameraStateManager), nameof(CameraStateManager.SetFollowingUnit))]
        [HarmonyPostfix]
        private static void CameraStateManager_SetFollowingUnit_Postfix(Unit unit)
        {
            try
            {
                if (_instance == null) return;
                // If we're no longer following the same pilot, disable.
                var camMgr = SceneSingleton<CameraStateManager>.i;
                if (camMgr == null) return;

                var pd = camMgr.followingUnit as PilotDismounted;
                if (pd == null || !IsLocal(pd))
                {
                    _instance.DisableFirstPerson();
                }
            }
            catch { }
        }
    }

    // Runs on the actual Camera GameObject. Unity calls this right before rendering.
    // This guarantees our roll/pitch/yaw isn't overwritten by the game's camera state code.
    private class CameraPoseEnforcer : MonoBehaviour
    {
        public NO_EjectFirstPerson owner;

        private void OnPreCull()
        {
            if (owner == null) return;
            if (!owner.active || !owner.desiredPoseValid) return;

            // Only enforce on the main camera we are attached to.
            try
            {
                // Hard-rig the camera to the pilot anchor to prevent any camera-state drift.
                if (owner.fpAnchor != null)
                {
                    if (transform.parent != owner.fpAnchor)
                        transform.SetParent(owner.fpAnchor, false);

                    transform.localPosition = owner.fpLocalPos;
                    transform.localRotation = owner.fpLocalRot;
                }
                else
                {
                    transform.SetPositionAndRotation(owner.desiredPos, owner.desiredRot);
                }
            }
            catch
            {
                transform.SetPositionAndRotation(owner.desiredPos, owner.desiredRot);
            }
        }

        private void OnPreRender()
        {
            // Some camera stacks / scripts modify transforms between PreCull and PreRender.
            // Re-apply as a belt-and-suspenders.
            if (owner == null) return;
            if (!owner.active || !owner.desiredPoseValid) return;

            try
            {
                // Hard-rig the camera to the pilot anchor to prevent any camera-state drift.
                if (owner.fpAnchor != null)
                {
                    if (transform.parent != owner.fpAnchor)
                        transform.SetParent(owner.fpAnchor, false);

                    transform.localPosition = owner.fpLocalPos;
                    transform.localRotation = owner.fpLocalRot;
                }
                else
                {
                    transform.SetPositionAndRotation(owner.desiredPos, owner.desiredRot);
                }
            }
            catch
            {
                transform.SetPositionAndRotation(owner.desiredPos, owner.desiredRot);
            }
        }
    }

}
