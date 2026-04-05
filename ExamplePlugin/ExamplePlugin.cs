using BepInEx;
using R2API;
using R2API.Utils;
using RoR2;
using RoR2.Skills;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

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

        private SkillDef golemLaserSkillDef;
        private SkillFamily golemLaserSkillFamily;
        private const float DroneLaserCooldownSeconds = 3f;
        public void Awake()
        {
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

            // Use a local clone helper instead of PrefabAPI.InstantiateClone which may not be available in this build context.
            droneBodyPrefab = InstantiateClone(gunnerDroneBody, "LaserDroneBody", true);
            droneMasterPrefab = InstantiateClone(gunnerDroneMaster, "LaserDroneMaster", true);

            if (droneBodyPrefab == null) Logger.LogError("droneBodyPrefab is null after cloning.");
            if (droneMasterPrefab == null) Logger.LogError("droneMasterPrefab is null after cloning.");

            ConfigureBody(droneBodyPrefab);
            ConfigureMaster(droneMasterPrefab, droneBodyPrefab);
            EnsureMuzzleLaserChild(droneBodyPrefab);

            GrabGolemLaserSkill(golemBody);
            CustomizeLaserSkillForDrone();
            ReplaceDronePrimarySkillWithLaser(droneBodyPrefab);
            TuneAI(droneMasterPrefab);
        }

        private GameObject InstantiateClone(GameObject original, string cloneName, bool register)
        {
            GameObject clone = R2API.PrefabAPI.InstantiateClone(original, cloneName, register);

            return clone;
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
        }

        private void ConfigureMaster(GameObject masterPrefab, GameObject bodyPrefab)
        {
            CharacterMaster master = masterPrefab.GetComponent<CharacterMaster>();
            master.bodyPrefab = bodyPrefab;
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

            ContentAddition.AddSkillDef(golemLaserSkillDef);

            Logger.LogInfo($"Customized Stone Golem laser cooldown to {DroneLaserCooldownSeconds:0.0}s for the drone.");
        }

        private void EnsureMuzzleLaserChild(GameObject bodyPrefab)
        {
            if (!bodyPrefab)
                return;

            ChildLocator locator = bodyPrefab.GetComponentInChildren<ChildLocator>();
            if (!locator)
            {
                Logger.LogWarning("Drone body has no ChildLocator; cannot create MuzzleLaser.");
                return;
            }

            string[] sourceNames =
            {
                "GunBarrel",
                "MuzzleRight",
                "Muzzle",
                "MuzzleFront",
                "GunMuzzle"
            };

            Transform source = null;
            foreach (string candidate in sourceNames)
            {
                source = locator.FindChild(candidate);
                if (source)
                    break;
            }

            if (!source)
            {
                Logger.LogWarning("Failed to find a muzzle transform to anchor MuzzleLaser; laser visuals may originate from the origin.");
                return;
            }

            Transform muzzleLaser = locator.FindChild("MuzzleLaser");
            if (!muzzleLaser)
            {
                GameObject go = new GameObject("MuzzleLaser");
                muzzleLaser = go.transform;
            }

            muzzleLaser.SetParent(source, false);
            muzzleLaser.localPosition = Vector3.zero;
            muzzleLaser.localRotation = Quaternion.identity;
            muzzleLaser.localScale = Vector3.one;

            EnsureChildLocatorEntry(locator, "MuzzleLaser", muzzleLaser);
            Logger.LogInfo($"Added 'MuzzleLaser' child under '{source.name}' for Stone Golem laser states.");
        }

        private void MergeChildLocators(ChildLocator target, ChildLocator source)
        {
            if (!target || !source)
                return;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo pairsField = typeof(ChildLocator).GetField("transformPairs", flags);
            if (pairsField == null)
                return;

            Array sourcePairs = pairsField.GetValue(source) as Array;
            if (sourcePairs == null || sourcePairs.Length == 0)
                return;

            Array targetPairs = pairsField.GetValue(target) as Array;
            Type pairType = sourcePairs.GetType().GetElementType();
            if (pairType == null)
                return;

            FieldInfo nameField = pairType.GetField("name", flags);
            FieldInfo transformField = pairType.GetField("transform", flags);
            if (nameField == null || transformField == null)
                return;

            List<object> merged = new List<object>();
            HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (targetPairs != null)
            {
                for (int i = 0; i < targetPairs.Length; i++)
                {
                    object entry = targetPairs.GetValue(i);
                    if (entry != null)
                    {
                        merged.Add(entry);
                        string existingName = nameField.GetValue(entry) as string;
                        if (!string.IsNullOrEmpty(existingName))
                        {
                            existingNames.Add(existingName);
                        }
                    }
                }
            }

            List<string> addedNames = new List<string>();
            for (int i = 0; i < sourcePairs.Length; i++)
            {
                object entry = sourcePairs.GetValue(i);
                if (entry == null)
                    continue;

                string name = nameField.GetValue(entry) as string;
                if (string.IsNullOrEmpty(name) || !existingNames.Add(name))
                    continue;

                Transform transform = transformField.GetValue(entry) as Transform;
                object newPair = Activator.CreateInstance(pairType);
                nameField.SetValue(newPair, name);
                transformField.SetValue(newPair, transform);
                merged.Add(newPair);
                addedNames.Add(name);
            }

            Array mergedArray = Array.CreateInstance(pairType, merged.Count);
            for (int i = 0; i < merged.Count; i++)
            {
                mergedArray.SetValue(merged[i], i);
            }
            pairsField.SetValue(target, mergedArray);

            foreach (string added in addedNames)
            {
                if (!string.IsNullOrEmpty(added))
                {
                    target.FindChild(added);
                }
            }

            if (addedNames.Count > 0)
            {
                Logger.LogInfo($"Merged ChildLocator entries for laser transforms: {string.Join(", ", addedNames)}");
            }
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
            // AISkillDriver type may not be available in this compilation context, so use reflection to find components named 'AISkillDriver'.
            Component[] components = masterPrefab.GetComponents<Component>();

            foreach (Component comp in components)
            {
                var type = comp.GetType();
                if (type.Name != "AISkillDriver")
                    continue;

                // Get skillSlot value (enum) and check if it's Primary
                object skillSlotObj = GetFieldOrPropertyValue(type, comp, "skillSlot");
                if (skillSlotObj is SkillSlot && (SkillSlot)skillSlotObj == SkillSlot.Primary)
                {
                    SetFieldOrProperty(type, comp, "minDistance", 5f);
                    SetFieldOrProperty(type, comp, "maxDistance", 300f);
                    SetFieldOrProperty(type, comp, "requireLineOfSight", true);

                    SetEnumField(type, comp, "moveTargetType", "CurrentEnemy");
                    SetEnumField(type, comp, "movementType", "ChaseMoveTarget");
                    SetEnumField(type, comp, "aimType", "AtCurrentEnemy");
                    SetEnumField(type, comp, "buttonPressType", "Hold");
                }
            }
        }

        private object GetFieldOrPropertyValue(Type type, object obj, string name)
        {
            FieldInfo f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(obj);
            PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            return null;
        }

        private bool SetFieldOrProperty(Type type, object obj, string name, object value)
        {
            FieldInfo f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                f.SetValue(obj, value);
                return true;
            }
            PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanWrite)
            {
                p.SetValue(obj, value);
                return true;
            }
            return false;
        }

        private void SetEnumField(Type type, object obj, string name, string enumValueName)
        {
            FieldInfo f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Type enumType = null;
            if (f != null) enumType = f.FieldType;
            else
            {
                PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) enumType = p.PropertyType;
            }

            if (enumType == null || !enumType.IsEnum) return;

            try
            {
                object enumValue = Enum.Parse(enumType, enumValueName);
                SetFieldOrProperty(type, obj, name, enumValue);
            }
            catch
            {
                // ignore parse/set failures
            }
        }

        private void EnsureChildLocatorEntry(ChildLocator locator, string alias, Transform target)
        {
            if (!locator || string.IsNullOrEmpty(alias) || !target)
                return;

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

        private void RegisterDrone()
        {
            LanguageAPI.Add("LASER_DRONE_NAME", "Laser Drone");
            LanguageAPI.Add("LASER_DRONE_PRIMARY_NAME", "Stone Laser");
            LanguageAPI.Add("LASER_DRONE_PRIMARY_DESC", "Charge and fire a piercing stone laser with a short cooldown.");

            ContentAddition.AddBody(droneBodyPrefab);
            ContentAddition.AddMaster(droneMasterPrefab);
        }

        private void CloneAndRegisterSkillFamilyAssets(SkillFamily family)
        {
            if (family == null) return;
            foreach (var variant in family.variants)
            {
                // SkillFamily.Variant is a struct — don't compare to null. Just check skillDef.
                if (variant.skillDef == null) continue;
                var sd = variant.skillDef;
                // Common GameObject fields in SkillDef: projectilePrefab, muzzleEffectPrefab, impactEffectPrefab
                TryCloneAndRegisterPrefabField(sd, "projectilePrefab");
                TryCloneAndRegisterPrefabField(sd, "muzzleEffectPrefab");
                TryCloneAndRegisterPrefabField(sd, "impactEffectPrefab");
                // Some SkillDefs reference other prefab-like fields; scan all fields for GameObject references as well
                var sdType = sd.GetType();
                var fields = sdType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    if (f.FieldType == typeof(GameObject))
                    {
                        try
                        {
                            var go = f.GetValue(sd) as GameObject;
                            if (go != null)
                            {
                                TryCloneAndRegisterGameObject(go);
                            }
                        }
                        catch { }
                    }
                }
            }
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
    }
}
