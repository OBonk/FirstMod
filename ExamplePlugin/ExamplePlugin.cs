using BepInEx;
using R2API;
using R2API.Utils;
using RoR2;
using RoR2.CharacterAI;
using RoR2.Skills;
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace DroneLaserTest
{
    [BepInDependency(PrefabAPI.PluginGUID)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class DroneLaserTestPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.fatalkp.dronelasertest";
        public const string PluginName = "Drone Laser Test";
        public const string PluginVersion = "0.1.0";

        private GameObject droneBodyPrefab;
        private GameObject droneMasterPrefab;

        private AssetBundle mainAssetBundle;
        private GameObject customModelPrefab;
        private Sprite customIcon;

        private SkillDef golemLaserSkillDef;
        private SkillFamily golemLaserSkillFamily;
        private const float DroneLaserCooldownSeconds = 3f;
        public void Awake()
        {
            LoadAssets();
            CreateDrone();
            RegisterDrone();
        }

        public void Update()
        {
            // Assumption: you meant F2.
            if (Input.GetKeyDown(KeyCode.F2))
            {
                TrySpawnDroneForLocalPlayer();
            }
        }

        private void CreateDrone()
        {
            // These paths may differ depending on your setup/game version.
            // Use your asset inspection tools if needed.
            GameObject gunnerDroneBody = Addressables
                .LoadAssetAsync<GameObject>("RoR2/Base/Drones/Drone1Body.prefab")
                .WaitForCompletion();

            GameObject gunnerDroneMaster = Addressables
                .LoadAssetAsync<GameObject>("RoR2/Base/Drones/Drone1Master.prefab")
                .WaitForCompletion();

            GameObject golemBody = Addressables
                .LoadAssetAsync<GameObject>("RoR2/Base/Golem/GolemBody.prefab")
                .WaitForCompletion();

            // Diagnostics: ensure assets loaded
            if (gunnerDroneBody == null) Logger.LogError("Failed to load Gunner Drone body prefab.");
            if (gunnerDroneMaster == null) Logger.LogError("Failed to load Gunner Drone master prefab.");
            if (golemBody == null) Logger.LogError("Failed to load Golem body prefab.");

            droneBodyPrefab = PrefabAPI.InstantiateClone(gunnerDroneBody, "LaserDroneBody", true);
            droneMasterPrefab = PrefabAPI.InstantiateClone(gunnerDroneMaster, "LaserDroneMaster", true);

            if (droneBodyPrefab == null) Logger.LogError("droneBodyPrefab is null after cloning.");
            if (droneMasterPrefab == null) Logger.LogError("droneMasterPrefab is null after cloning.");

            ConfigureBody(droneBodyPrefab);
            ConfigureMaster(droneMasterPrefab, droneBodyPrefab);
            ApplyCustomModel(droneBodyPrefab);
            EnsureMuzzleLaserChild(droneBodyPrefab);

            GrabGolemLaserSkill(golemBody);
            CustomizeLaserSkillForDrone();
            ReplaceDronePrimarySkillWithLaser(droneBodyPrefab);
            TuneAI(droneMasterPrefab);
        }

        private void ConfigureBody(GameObject bodyPrefab)
        {
            CharacterBody body = bodyPrefab.GetComponent<CharacterBody>();

            body.baseNameToken = "LASER_DRONE_NAME";
            body.baseMaxHealth = 180f;
            body.levelMaxHealth = 54f;
            body.baseDamage = 12f;
            body.levelDamage = 2.4f;
            body.baseArmor = 20f;
            body.baseMoveSpeed = 10f;
            body.baseAcceleration = 80f;

            if (customIcon)
            {
                body.portraitIcon = customIcon.texture;
            }
        }

        private void ConfigureMaster(GameObject masterPrefab, GameObject bodyPrefab)
        {
            CharacterMaster master = masterPrefab.GetComponent<CharacterMaster>();
            master.bodyPrefab = bodyPrefab;
        }

        private void ApplyCustomModel(GameObject bodyPrefab)
        {
            if (!bodyPrefab)
                return;

            if (!customModelPrefab)
            {
                Logger.LogInfo("Custom laser drone model prefab not loaded; keeping stock Gunner Drone visuals.");
                return;
            }

            CharacterBody body = bodyPrefab.GetComponent<CharacterBody>();
            ModelLocator modelLocator = bodyPrefab.GetComponent<ModelLocator>();

            if (!body || !modelLocator)
            {
                Logger.LogWarning("Missing CharacterBody or ModelLocator on the drone prefab, cannot apply custom model.");
                return;
            }

            Transform modelBase = modelLocator.modelBaseTransform;
            if (!modelBase)
            {
                GameObject baseObj = new GameObject("ModelBase");
                baseObj.transform.SetParent(bodyPrefab.transform);
                baseObj.transform.localPosition = Vector3.zero;
                baseObj.transform.localRotation = Quaternion.identity;
                baseObj.transform.localScale = Vector3.one;
                modelBase = baseObj.transform;
                modelLocator.modelBaseTransform = modelBase;
            }

            Transform oldModel = modelLocator.modelTransform;
            Animator oldAnimator = oldModel ? oldModel.GetComponent<Animator>() : null;
            HurtBoxGroup oldHurtBoxes = oldModel ? oldModel.GetComponent<HurtBoxGroup>() ?? oldModel.GetComponentInChildren<HurtBoxGroup>() : null;

            GameObject newModel = InstantiatePreparedCustomModel();
            if (!newModel)
            {
                Logger.LogWarning("Failed to prepare custom laser drone model; keeping stock Gunner Drone visuals.");
                return;
            }

            newModel.transform.SetParent(modelBase, false);
            newModel.transform.localPosition = Vector3.zero;
            newModel.transform.localRotation = Quaternion.identity;
            newModel.transform.localScale = Vector3.one;

            modelLocator.modelTransform = newModel.transform;

            CharacterModel characterModel = newModel.GetComponent<CharacterModel>();
            if (!characterModel)
            {
                characterModel = newModel.AddComponent<CharacterModel>();
                characterModel.autoPopulateLightInfos = true;
                characterModel.baseRendererInfos = BuildRendererInfos(newModel);
            }
            else
            {
                characterModel.body = body;
                if (characterModel.baseRendererInfos == null || characterModel.baseRendererInfos.Length == 0)
                {
                    characterModel.baseRendererInfos = BuildRendererInfos(newModel);
                }
            }

            characterModel.body = body;
            characterModel.mainSkinnedMeshRenderer ??= newModel.GetComponentInChildren<SkinnedMeshRenderer>();

            HurtBoxGroup newHurtBoxGroup = newModel.GetComponent<HurtBoxGroup>() ?? newModel.GetComponentInChildren<HurtBoxGroup>();
            if (newHurtBoxGroup)
            {
                body.mainHurtBox = newHurtBoxGroup.mainHurtBox;
                body.hurtBoxGroup = newHurtBoxGroup;
            }
            else if (oldHurtBoxes)
            {
                body.mainHurtBox = oldHurtBoxes.mainHurtBox;
                body.hurtBoxGroup = oldHurtBoxes;
            }

            ChildLocator childLocator = newModel.GetComponent<ChildLocator>();
            if (!childLocator)
            {
                childLocator = newModel.AddComponent<ChildLocator>();
            }
            if (childLocator)
            {
                PopulateChildLocatorIfMissing(childLocator, childLocator.transform);
                Transform aim = childLocator.FindChild("AimOrigin") ?? childLocator.FindChild("Head") ?? childLocator.FindChild("Muzzle");
                if (aim)
                {
                    body.aimOriginTransform = aim;
                }
                characterModel.childLocator = childLocator;
            }

            Animator newAnimator = newModel.GetComponent<Animator>();
            if (!newAnimator && oldAnimator)
            {
                newAnimator = newModel.AddComponent<Animator>();
            }
            if (newAnimator && oldAnimator)
            {
                newAnimator.runtimeAnimatorController = oldAnimator.runtimeAnimatorController;
                newAnimator.avatar = oldAnimator.avatar;
                newAnimator.applyRootMotion = oldAnimator.applyRootMotion;
                newAnimator.updateMode = oldAnimator.updateMode;
                newAnimator.cullingMode = oldAnimator.cullingMode;
            }

            FixupRendererMaterials(newModel, oldModel);

            if (oldModel && oldModel.gameObject != newModel)
            {
                UnityEngine.Object.Destroy(oldModel.gameObject);
            }

            Logger.LogInfo("Applied custom laser drone model prefab to the cloned body.");
        }

        private CharacterModel.RendererInfo[] BuildRendererInfos(GameObject modelRoot)
        {
            if (!modelRoot)
                return Array.Empty<CharacterModel.RendererInfo>();

            Renderer[] renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            CharacterModel.RendererInfo[] infos = new CharacterModel.RendererInfo[renderers.Length];

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                infos[i] = new CharacterModel.RendererInfo
                {
                    renderer = renderer,
                    defaultMaterial = renderer.sharedMaterial,
                    defaultShadowCastingMode = renderer.shadowCastingMode,
                    ignoreOverlays = false
                };
            }

            return infos;
        }

        private GameObject InstantiatePreparedCustomModel()
        {
            if (!customModelPrefab)
                return null;

            GameObject instance = UnityEngine.Object.Instantiate(customModelPrefab);
            if (!instance)
                return null;

            instance.name = customModelPrefab.name;

            Transform extracted = ExtractVisualModelTransform(instance.transform);
            if (!extracted)
            {
                Logger.LogWarning("Custom model prefab had no identifiable visual root; using entire prefab.");
                extracted = instance.transform;
            }

            if (extracted != instance.transform)
            {
                extracted.SetParent(null, false);
                UnityEngine.Object.Destroy(instance);
            }

            GameObject model = extracted.gameObject;

            if (!HasRenderableGeometry(model))
            {
                Logger.LogError("Custom laser drone model prefab contains no renderers; aborting swap.");
                UnityEngine.Object.Destroy(model);
                return null;
            }

            StripGameplayComponents(model);
            return model;
        }

        private Transform ExtractVisualModelTransform(Transform root)
        {
            if (!root)
                return null;

            ModelLocator locator = root.GetComponent<ModelLocator>() ?? root.GetComponentInChildren<ModelLocator>(true);
            if (locator && locator.modelTransform)
                return locator.modelTransform;

            string[] preferredNames = { "mdlLaserDrone", "mdlDrone1", "mdlDrone", "mdl", "ModelBase" };
            foreach (string name in preferredNames)
            {
                Transform found = FindChildRecursive(root, name);
                if (found)
                {
                    if (string.Equals(name, "ModelBase", StringComparison.OrdinalIgnoreCase) && found.childCount > 0)
                    {
                        return found.GetChild(0);
                    }
                    return found;
                }
            }

            return root;
        }

        private void StripGameplayComponents(GameObject root)
        {
            if (!root)
                return;

            Type[] componentTypes =
            {
                typeof(CharacterBody),
                typeof(CharacterDirection),
                typeof(CharacterMotor),
                typeof(InputBankTest),
                typeof(SkillLocator),
                typeof(EntityStateMachine),
                typeof(AISkillDriver),
                typeof(ModelLocator),
                typeof(NetworkIdentity),
                typeof(Rigidbody),
                typeof(Rigidbody2D),
                typeof(CharacterJoint),
                typeof(ConfigurableJoint),
                typeof(SpringJoint)
            };

            int removed = 0;
            foreach (Type type in componentTypes)
            {
                Component[] comps = root.GetComponentsInChildren(type, true);
                foreach (Component comp in comps)
                {
                    if (!comp)
                        continue;
                    UnityEngine.Object.Destroy(comp);
                    removed++;
                }
            }

            if (removed > 0)
            {
                Logger.LogInfo($"Stripped {removed} gameplay components from custom model prefab to prevent duplicate body behaviour.");
            }
        }

        private bool HasRenderableGeometry(GameObject modelRoot)
        {
            if (!modelRoot)
                return false;

            Renderer[] renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            return renderers != null && renderers.Length > 0;
        }

        private void FixupRendererMaterials(GameObject newModel, Transform oldModel)
        {
            if (!newModel)
                return;

            Renderer[] newRenderers = newModel.GetComponentsInChildren<Renderer>(true);
            if (newRenderers == null || newRenderers.Length == 0)
            {
                Logger.LogWarning("Custom model prefab has no renderers after instantiation.");
                return;
            }

            Renderer[] oldRenderers = oldModel ? oldModel.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
            Material fallbackMaterial = oldRenderers != null && oldRenderers.Length > 0
                ? oldRenderers[0].sharedMaterial
                : null;
            Shader fallbackShader = fallbackMaterial ? fallbackMaterial.shader : null;

            if (!fallbackShader)
            {
                fallbackShader = Shader.Find("Hopoo Games/Deferred/Standard") ?? Shader.Find("Standard");
            }

            int materialsFixed = 0;
            foreach (Renderer renderer in newRenderers)
            {
                if (!renderer)
                    continue;

                renderer.gameObject.SetActive(true);
                renderer.enabled = true;
                renderer.forceRenderingOff = false;

                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                {
                    if (fallbackMaterial)
                    {
                        renderer.sharedMaterial = UnityEngine.Object.Instantiate(fallbackMaterial);
                        materialsFixed++;
                    }
                    continue;
                }

                bool updatedArray = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    Material mat = materials[i];
                    if (!mat)
                    {
                        if (fallbackMaterial)
                        {
                            materials[i] = UnityEngine.Object.Instantiate(fallbackMaterial);
                            updatedArray = true;
                            materialsFixed++;
                        }
                        continue;
                    }

                    if (!mat.shader || mat.shader.name == "Hidden/InternalErrorShader")
                    {
                        if (fallbackShader)
                        {
                            mat.shader = fallbackShader;
                            materialsFixed++;
                        }
                    }
                }

                if (updatedArray)
                {
                    renderer.sharedMaterials = materials;
                }
            }

            Logger.LogInfo($"Custom model renderers: {newRenderers.Length}. Materials adjusted: {materialsFixed}.");
        }

        private Transform FindChildRecursive(Transform root, string childName)
        {
            if (!root || string.IsNullOrEmpty(childName))
                return null;

            if (string.Equals(root.name, childName, StringComparison.OrdinalIgnoreCase))
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                Transform match = FindChildRecursive(child, childName);
                if (match)
                    return match;
            }

            return null;
        }

        private void GrabGolemLaserSkill(GameObject golemBodyPrefab)
        {
            if (golemBodyPrefab == null)
            {
                Logger.LogError("golemBodyPrefab is null.");
                return;
            }

            SkillLocator golemSkillLocator = golemBodyPrefab.GetComponent<SkillLocator>();
            if (golemSkillLocator == null)
            {
                Logger.LogError("Golem prefab has no SkillLocator.");
            }

            // Log available skill slots for diagnostics
            void LogSkillSlot(string slotName, GenericSkill skill)
            {
                if (skill == null)
                {
                    Logger.LogInfo($"Golem {slotName}: <missing>");
                }
                else if (skill.skillDef == null)
                {
                    Logger.LogInfo($"Golem {slotName}: GenericSkill present but skillDef is null");
                }
                else
                {
                    Logger.LogInfo($"Golem {slotName}: {skill.skillDef.skillName} (token: {skill.skillDef.skillNameToken})");
                }
            }

            if (golemSkillLocator != null)
            {
                LogSkillSlot("primary", golemSkillLocator.primary);
                LogSkillSlot("secondary", golemSkillLocator.secondary);
                LogSkillSlot("utility", golemSkillLocator.utility);
                LogSkillSlot("special", golemSkillLocator.special);
            }


            // Prefer the secondary skillDef if available
            if (golemSkillLocator != null && golemSkillLocator.secondary != null && golemSkillLocator.secondary.skillDef != null)
            {
                golemLaserSkillDef = golemSkillLocator.secondary.skillDef;
                Logger.LogInfo($"Grabbed Stone Golem laser SkillDef from secondary: {golemLaserSkillDef.skillName}");
                return;
            }

            // Fallback: search all GenericSkill components on the prefab for a SkillDef mentioning 'laser'
            golemLaserSkillDef = FindLaserSkillDefInPrefab(golemBodyPrefab);
            if (golemLaserSkillDef != null)
            {
                Logger.LogInfo($"Grabbed Stone Golem laser SkillDef by fallback search: {golemLaserSkillDef.skillName}");
                return;
            }

            // Final fallback: try loading the SkillDef asset directly (common path for Golem laser)
            try
            {
                Logger.LogInfo("Attempting Addressables load of 'RoR2/Base/Golem/GolemBodyLaser.asset' as final fallback...");
                var sd = Addressables.LoadAssetAsync<SkillDef>("RoR2/Base/Golem/GolemBodyLaser.asset").WaitForCompletion();
                if (sd != null)
                {
                    golemLaserSkillDef = sd;
                    Logger.LogInfo($"Loaded Golem laser SkillDef from asset: {golemLaserSkillDef.skillName}");
                    // Also try to load the SkillFamily which often contains the full setup (projectiles/effects)
                    try
                    {
                        var fam = Addressables.LoadAssetAsync<SkillFamily>("RoR2/Base/Golem/GolemBodySecondaryFamily.asset").WaitForCompletion();
                        if (fam != null)
                        {
                            golemLaserSkillFamily = fam;
                            Logger.LogInfo($"Loaded Golem laser SkillFamily from asset: {fam.name}");
                        }
                    }
                    catch (Exception) { }
                    return;
                }
                else
                {
                    Logger.LogError("Addressables fallback did not return a SkillDef.");
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"Addressables load failed: {e.Message}");
            }

            Logger.LogError("Stone Golem secondary SkillDef was null and all fallbacks failed to find a laser SkillDef.");
        }

        private void CustomizeLaserSkillForDrone()
        {
            if (golemLaserSkillDef == null)
            {
                Logger.LogWarning("Cannot customize Stone Golem laser for the drone because the SkillDef was null.");
                return;
            }

            golemLaserSkillDef = ScriptableObject.Instantiate(golemLaserSkillDef);
            golemLaserSkillDef.skillName = "LaserDroneGolemLaser";
            golemLaserSkillDef.skillNameToken = "LASER_DRONE_PRIMARY_NAME";
            golemLaserSkillDef.skillDescriptionToken = "LASER_DRONE_PRIMARY_DESC";

            golemLaserSkillDef.baseRechargeInterval = DroneLaserCooldownSeconds;
            golemLaserSkillDef.baseMaxStock = 1;
            golemLaserSkillDef.requiredStock = 1;
            golemLaserSkillDef.stockToConsume = 1;
            golemLaserSkillDef.rechargeStock = 1;
            golemLaserSkillDef.beginSkillCooldownOnSkillEnd = false;
            golemLaserSkillDef.canceledFromSprinting = false;
            golemLaserSkillDef.cancelSprintingOnActivation = false;
            golemLaserSkillDef.mustKeyPress = false;
            golemLaserSkillDef.isCombatSkill = true;
            if (customIcon)
            {
                golemLaserSkillDef.icon = customIcon;
            }

            ContentAddition.AddSkillDef(golemLaserSkillDef);

            Logger.LogInfo($"Customized Stone Golem laser cooldown to {DroneLaserCooldownSeconds:0.0}s for the drone.");
        }

        private void EnsureMuzzleLaserChild(GameObject bodyPrefab)
        {
            if (!bodyPrefab)
                return;

            ChildLocator locator = null;
            ModelLocator modelLocator = bodyPrefab.GetComponent<ModelLocator>();
            Transform modelTransform = modelLocator ? modelLocator.modelTransform : null;

            if (modelLocator && modelLocator.modelTransform)
            {
                locator = modelLocator.modelTransform.GetComponent<ChildLocator>();
            }

            locator ??= bodyPrefab.GetComponentInChildren<ChildLocator>();
            if (!locator && modelTransform)
            {
                locator = modelTransform.gameObject.AddComponent<ChildLocator>();
            }

            if (locator && modelTransform)
            {
                PopulateChildLocatorIfMissing(locator, modelTransform);
            }

            if (!locator)
            {
                Logger.LogWarning("Drone body has no ChildLocator; cannot create MuzzleLaser.");
                return;
            }

            Transform muzzleLaser = locator.FindChild("MuzzleLaser") ?? FindChildRecursive(modelTransform, "MuzzleLaser");
            if (!muzzleLaser)
            {
                Transform source = locator.FindChild("Muzzle") ?? locator.FindChild("MuzzleGun") ?? FindChildRecursive(modelTransform, "Muzzle") ?? FindChildRecursive(modelTransform, "MuzzleGun");
                if (!source)
                {
                    Logger.LogWarning("Failed to find a muzzle transform to anchor MuzzleLaser; laser visuals may originate from the origin.");
                    return;
                }

                GameObject go = new GameObject("MuzzleLaser");
                muzzleLaser = go.transform;
                muzzleLaser.SetParent(source, false);
                muzzleLaser.localPosition = Vector3.zero;
                muzzleLaser.localRotation = Quaternion.identity;
                muzzleLaser.localScale = Vector3.one;
                Logger.LogInfo($"Created fallback 'MuzzleLaser' under '{source.name}'.");
            }
            else
            {
                Logger.LogInfo("Using existing 'MuzzleLaser' transform from the custom model prefab.");
            }

            EnsureChildLocatorEntry(locator, "MuzzleLaser", muzzleLaser);
        }

        private SkillDef FindLaserSkillDefInPrefab(GameObject prefab)
        {
            if (prefab == null) return null;
            var skills = prefab.GetComponentsInChildren<GenericSkill>(true);
            foreach (var gs in skills)
            {
                if (gs == null || gs.skillDef == null) continue;
                var sd = gs.skillDef;
                string name = sd.skillName ?? string.Empty;
                string token = sd.skillNameToken ?? string.Empty;
                if (name.IndexOf("laser", StringComparison.OrdinalIgnoreCase) >= 0 || token.IndexOf("laser", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return sd;
                }
            }
            return null;
        }

        private void ReplaceDronePrimarySkillWithLaser(GameObject bodyPrefab)
        {
            if (!golemLaserSkillDef)
                return;

            SkillLocator skillLocator = bodyPrefab.GetComponent<SkillLocator>();
            if (!skillLocator || !skillLocator.primary)
            {
                Logger.LogError("Drone has no primary GenericSkill.");
                return;
            }

            GenericSkill primarySkill = skillLocator.primary;

            // Replace the Gunner Drone's normal primary with the Golem laser.
            primarySkill.SetFieldValue<SkillFamily>("_skillFamily", null); // optional cleanup if you use reflection helpers
            primarySkill.skillName = "LaserDronePrimary";

            SkillFamily family = ScriptableObject.CreateInstance<SkillFamily>();
            family.variants = new SkillFamily.Variant[1];
            family.variants[0] = new SkillFamily.Variant
            {
                skillDef = golemLaserSkillDef,
                unlockableDef = null,
                viewableNode = new ViewablesCatalog.Node(golemLaserSkillDef.skillNameToken, false, null)
            };

            primarySkill._skillFamily = family;

            // Optional: disable the drone's old secondary so it mostly just uses laser.
            if (skillLocator.secondary)
            {
                SkillFamily secondaryFamily = ScriptableObject.CreateInstance<SkillFamily>();
                secondaryFamily.variants = new SkillFamily.Variant[0];
                skillLocator.secondary._skillFamily = secondaryFamily;
            }
        }

        private void TuneAI(GameObject masterPrefab)
        {
            AISkillDriver[] drivers = masterPrefab.GetComponents<AISkillDriver>();
            foreach (AISkillDriver driver in drivers)
            {
                if (driver.skillSlot == SkillSlot.Primary)
                {
                    driver.minDistance = 5f;
                    driver.maxDistance = 300f;
                    driver.selectionRequiresTargetLoS = true;
                    driver.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
                    driver.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
                    driver.aimType = AISkillDriver.AimType.AtCurrentEnemy;
                    driver.buttonPressType = AISkillDriver.ButtonPressType.Hold;
                }
            }
        }

        private void EnsureChildLocatorEntry(ChildLocator locator, string alias, Transform target)
        {
            if (!locator || string.IsNullOrEmpty(alias) || !target)
                return;

            PopulateChildLocatorIfMissing(locator, locator.transform);

            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo pairsField = typeof(ChildLocator).GetField("transformPairs", flags);
            if (pairsField == null)
                return;

            Array pairs = pairsField.GetValue(locator) as Array;
            Type pairType = pairs != null ? pairs.GetType().GetElementType() : typeof(ChildLocator).GetNestedType("NameTransformPair", BindingFlags.NonPublic);
            if (pairType == null)
                return;

            FieldInfo nameField = pairType.GetField("name", flags);
            FieldInfo transformField = pairType.GetField("transform", flags);
            if (nameField == null || transformField == null)
                return;

            if (pairs != null)
            {
                for (int i = 0; i < pairs.Length; i++)
                {
                    object entry = pairs.GetValue(i);
                    if (entry == null)
                        continue;
                    string existingName = nameField.GetValue(entry) as string;
                    if (string.Equals(existingName, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        transformField.SetValue(entry, target);
                        locator.FindChild(alias);
                        return;
                    }
                }
            }

            object newPair = Activator.CreateInstance(pairType);
            nameField.SetValue(newPair, alias);
            transformField.SetValue(newPair, target);

            List<object> merged = new List<object>();
            if (pairs != null)
            {
                foreach (object entry in pairs)
                {
                    merged.Add(entry);
                }
            }
            merged.Add(newPair);

            Array mergedArray = Array.CreateInstance(pairType, merged.Count);
            for (int i = 0; i < merged.Count; i++)
            {
                mergedArray.SetValue(merged[i], i);
            }
            pairsField.SetValue(locator, mergedArray);

            locator.FindChild(alias);
        }

        private void PopulateChildLocatorIfMissing(ChildLocator locator, Transform root)
        {
            if (!locator || !root)
                return;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo pairsField = typeof(ChildLocator).GetField("transformPairs", flags);
            Type pairType = typeof(ChildLocator).GetNestedType("NameTransformPair", flags);
            if (pairsField == null || pairType == null)
                return;

            Array existing = pairsField.GetValue(locator) as Array;
            if (existing != null && existing.Length > 0)
                return;

            FieldInfo nameField = pairType.GetField("name", flags);
            FieldInfo transformField = pairType.GetField("transform", flags);
            if (nameField == null || transformField == null)
                return;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            Array pairs = Array.CreateInstance(pairType, transforms.Length);
            for (int i = 0; i < transforms.Length; i++)
            {
                object entry = Activator.CreateInstance(pairType);
                nameField.SetValue(entry, transforms[i].name);
                transformField.SetValue(entry, transforms[i]);
                pairs.SetValue(entry, i);
            }

            pairsField.SetValue(locator, pairs);
        }

        private void RegisterDrone()
        {
            LanguageAPI.Add("LASER_DRONE_NAME", "Laser Drone");
            LanguageAPI.Add("LASER_DRONE_PRIMARY_NAME", "Stone Laser");
            LanguageAPI.Add("LASER_DRONE_PRIMARY_DESC", "Charge and fire a piercing stone laser with a short cooldown.");

            ContentAddition.AddBody(droneBodyPrefab);
            ContentAddition.AddMaster(droneMasterPrefab);
        }

        private void TryCloneAndRegisterPrefabField(SkillDef sd, string fieldName)
        {
            try
            {
                var f = sd.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return;
                var go = f.GetValue(sd) as GameObject;
                if (go == null) return;
                TryCloneAndRegisterGameObject(go);
            }
            catch { }
        }

        private void TryCloneAndRegisterGameObject(GameObject go)
        {
            if (go == null) return;
            try
            {
                // Use PrefabAPI if available
                GameObject clone = null;
                try { clone = R2API.PrefabAPI.InstantiateClone(go, go.name + "_DroneClone", true); } catch { }
                if (clone == null)
                {
                    clone = UnityEngine.Object.Instantiate(go);
                    clone.name = go.name + "_DroneClone";
                }

                // Registration: ContentAddition.AddItem does not exist. Log the clone name so you can register manually if needed.
                Logger.LogInfo($"Cloned prefab for registration: {clone.name}");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Failed to clone/register prefab {go.name}: {e.Message}");
            }
        }

        private void TrySpawnDroneForLocalPlayer()
        {
            if (!NetworkServer.active)
            {
                Logger.LogWarning("You can only spawn the drone as host/single-player with this simple F2 test.");
                return;
            }

            if (!LocalUserManager.GetFirstLocalUser().cachedBody)
            {
                Logger.LogWarning("No local player body found.");
                return;
            }

            CharacterBody ownerBody = LocalUserManager.GetFirstLocalUser().cachedBody;

            MasterSummon summon = new MasterSummon
            {
                masterPrefab = droneMasterPrefab,
                position = ownerBody.corePosition + ownerBody.inputBank.aimDirection * 6f,
                rotation = Quaternion.identity,
                summonerBodyObject = ownerBody.gameObject,
                teamIndexOverride = ownerBody.teamComponent.teamIndex,
                ignoreTeamMemberLimit = true,
                useAmbientLevel = true
            };

            CharacterMaster summonedMaster = summon.Perform();

            if (summonedMaster)
                Logger.LogInfo("Spawned Laser Drone.");
            else
                Logger.LogError("Failed to spawn Laser Drone.");
        }
        private void LoadAssets()
        {
            if (mainAssetBundle != null)
                return;

            string assemblyFolder = System.IO.Path.GetDirectoryName(Info.Location);
            string bundlePath = System.IO.Path.Combine(assemblyFolder, "laserdroneprefab");

            if (!File.Exists(bundlePath))
            {
                Logger.LogWarning($"Asset bundle not found at {bundlePath}; custom laser drone model will be skipped.");
                return;
            }

            mainAssetBundle = AssetBundle.LoadFromFile(bundlePath);

            if (mainAssetBundle == null)
            {
                Logger.LogError($"Failed to load asset bundle at: {bundlePath}");
                return;
            }

            const string modelAssetPath = "Assets/RoR2/Base/Characters/Drones/LaserDrone/LaserDroneBody.prefab";
            const string iconAssetPath = "Assets/RoR2/Base/Characters/Drones/LaserDrone/texLaserDrone Icon.asset";

            try
            {
                customModelPrefab = mainAssetBundle.LoadAsset<GameObject>(modelAssetPath);
                Logger.LogInfo(customModelPrefab
                    ? $"Loaded custom laser drone model prefab '{modelAssetPath}'."
                    : "Custom laser drone model prefab was not found inside the asset bundle.");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to load custom model prefab '{modelAssetPath}': {e}");
            }

            try
            {
                customIcon = mainAssetBundle.LoadAsset<Sprite>(iconAssetPath);
                Logger.LogInfo(customIcon
                    ? $"Loaded custom laser drone icon '{iconAssetPath}'."
                    : "Custom laser drone icon sprite was not found inside the asset bundle.");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to load custom icon sprite '{iconAssetPath}': {e}");
            }
        }
    }
}
