﻿using KSPAPIExtensions;
using RP0.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using static RP0.ProceduralAvionics.ProceduralAvionicsUtils;

namespace RP0.ProceduralAvionics
{
    class ModuleProceduralAvionics : ModuleAvionics, IPartMassModifier, IPartCostModifier
    {
        private const string KwFormat = "{0:0.##}";
        private const string WFormat = "{0:0}";
        private const float FloatTolerance = 1.00001f;
        private const float InternalTanksTotalVolumeUtilization = 0.246f; //Max utilization for 2 spheres within a cylindrical container worst case scenario
        private const float InternalTanksAvailableVolumeUtilization = 0.5f;

        #region Fields

        [KSPField(isPersistant = true, guiName = "Contr. Mass", guiActiveEditor = true, guiUnits = "\u2009t", groupName = PAWGroup, groupDisplayName = PAWGroup),
         UI_FloatEdit(scene = UI_Scene.Editor, minValue = 0f, incrementLarge = 10f, incrementSmall = 1f, incrementSlide = 0.05f, sigFigs = 3, unit = "\u2009t")]
        public float controllableMass = -1;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Configuration", groupName = PAWGroup, groupDisplayName = PAWGroup), UI_ChooseOption(scene = UI_Scene.Editor)]
        public string avionicsConfigName;

        [KSPField(isPersistant = true)]
        public string avionicsTechLevel;

        [KSPField(guiActiveEditor = true, guiName = "Avionics Utilization", groupName = PAWGroup)]
        public string utilizationDisplay;

        [KSPField(guiActiveEditor = true, guiName = "Power Requirements", groupName = PAWGroup)]
        public string powerRequirementsDisplay;

        [KSPField(guiActiveEditor = true, guiName = "Avionics Mass", groupName = PAWGroup)]
        public string massDisplay;

        [KSPField(guiActiveEditor = true, guiName = "Avionics Cost", groupName = PAWGroup)]
        public string costDisplay;

        public bool IsScienceCore => CurrentProceduralAvionicsTechNode.massExponent == 0 && CurrentProceduralAvionicsTechNode.powerExponent == 0 && CurrentProceduralAvionicsTechNode.costExponent == 0;

        [KSPField(guiName = "Desired Utilization", guiActiveEditor = true, guiFormat = "P1", groupName = PAWGroup),
         UI_FloatRange(scene = UI_Scene.Editor, minValue = .01f, maxValue = .999f, stepIncrement = .001f, suppressEditorShipModified = true)]
        public float targetUtilization = 1;

        private static bool _configsLoaded = false;

        private bool _started = false;

        [KSPEvent(active = true, guiActiveEditor = true, guiName = "Resize to Utilization", groupName = PAWGroup)]
        void SeekVolume()
        {
            if (GetSeekVolumeMethod(out PartModule PPart) is System.Reflection.MethodInfo method)
            {
                float targetVolume = GetAvionicsVolume() / targetUtilization;
                Log($"SeekVolume() target utilization {targetUtilization:P1}, CurrentAvionicsVolume for max util: {GetAvionicsVolume()}, Desired Volume: {targetVolume}");
                try
                {
                    method.Invoke(PPart, new object[] { targetVolume });
                }
                catch (Exception e) { Debug.LogError($"{e?.InnerException.Message ?? e.Message}"); }
            }
        }

        private System.Reflection.MethodInfo GetSeekVolumeMethod(out PartModule PPart)
        {
            PPart = null;
            if (part.Modules.Contains("ProceduralPart"))
            {
                PPart = part.Modules["ProceduralPart"];
                System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                return PPart.GetType().GetMethod("SeekVolume", flags);
            }
            return null;
        }

        public ProceduralAvionicsConfig CurrentProceduralAvionicsConfig { get; private set; }

        public ProceduralAvionicsTechNode CurrentProceduralAvionicsTechNode
        {
            get
            {
                if (CurrentProceduralAvionicsConfig != null && avionicsTechLevel != null && CurrentProceduralAvionicsConfig.TechNodes.ContainsKey(avionicsTechLevel))
                {
                    return CurrentProceduralAvionicsConfig.TechNodes[avionicsTechLevel];
                }
                return new ProceduralAvionicsTechNode();
            }
        }

        public float Utilization => GetAvionicsMass() / MaxAvionicsMass;

        private float MaxAvionicsMass => cachedVolume * CurrentProceduralAvionicsTechNode.avionicsDensity;

        public float InternalTanksVolume { get; private set; }

        #endregion

        #region Get Utilities
        protected override float GetInternalMassLimit() => !IsScienceCore ? controllableMass : 0;

        private void ClampControllableMass()
        {
            var maxControllableMass = GetMaximumControllableMass();
            if (controllableMass > maxControllableMass * FloatTolerance)
            {
                Log($"Resetting procedural mass limit to {maxControllableMass}, was {controllableMass}");
                controllableMass = maxControllableMass;
                MonoUtilities.RefreshContextWindows(part);
            }
        }

        private float GetControllableMass(float avionicsMass) 
        {
            float res = GetInversePolynomial(avionicsMass * 1000, CurrentProceduralAvionicsTechNode.massExponent, CurrentProceduralAvionicsTechNode.massConstant, CurrentProceduralAvionicsTechNode.massFactor);
            return float.IsNaN(res) || float.IsInfinity(res) ? 0 : res;
        }
//        private float GetMaximumControllableMass() => FloorToSliderIncrement(GetControllableMass(MaxAvionicsMass));
        private float GetMaximumControllableMass() => GetControllableMass(MaxAvionicsMass);

        private float GetAvionicsMass() => GetPolynomial(GetInternalMassLimit(), CurrentProceduralAvionicsTechNode.massExponent, CurrentProceduralAvionicsTechNode.massConstant, CurrentProceduralAvionicsTechNode.massFactor) / 1000f;
        private float GetAvionicsCost() => GetPolynomial(GetInternalMassLimit(), CurrentProceduralAvionicsTechNode.costExponent, CurrentProceduralAvionicsTechNode.costConstant, CurrentProceduralAvionicsTechNode.costFactor);
        private float GetAvionicsVolume() => GetAvionicsMass() / CurrentProceduralAvionicsTechNode.avionicsDensity;

        private float GetShieldedAvionicsMass()
        {
            var avionicsMass = GetAvionicsMass();
            return avionicsMass + GetShieldingMass(avionicsMass);
        }

        private float GetShieldingMass(float avionicsMass) => Mathf.Pow(avionicsMass, 2f / 3) * CurrentProceduralAvionicsTechNode.shieldingMassFactor;

        protected override float GetEnabledkW() => GetPolynomial(GetInternalMassLimit(), CurrentProceduralAvionicsTechNode.powerExponent, CurrentProceduralAvionicsTechNode.powerConstant, CurrentProceduralAvionicsTechNode.powerFactor) / 1000f;
        protected override float GetDisabledkW() => GetEnabledkW() * CurrentProceduralAvionicsTechNode.disabledPowerFactor;

        private static float GetPolynomial(float value, float exponent, float constant, float factor) => (Mathf.Pow(value, exponent) + constant) * factor;
        private static float GetInversePolynomial(float value, float exponent, float constant, float factor) => Mathf.Pow(value / factor - constant, 1 / exponent);

        protected override bool GetToggleable() => CurrentProceduralAvionicsTechNode.disabledPowerFactor > 0;

        protected override string GetTonnageString() => "This part can be configured to allow control of vessels up to any mass.";

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => CurrentProceduralAvionicsTechNode.avionicsDensity > 0 ? GetShieldedAvionicsMass() : 0;
        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;
        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) => CurrentProceduralAvionicsTechNode.avionicsDensity > 0 ? GetAvionicsCost() : 0;
        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.FIXED;

        #endregion

        #region Callbacks

        public override void OnLoad(ConfigNode node)
        {
            if (HighLogic.LoadedScene == GameScenes.LOADING && !_configsLoaded)
            {
                try
                {
                    Log("Loading Avionics Configs");
                    ProceduralAvionicsTechManager.LoadAvionicsConfigs();
                    _configsLoaded = true;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            base.OnLoad(node);
        }

        public override void OnStart(StartState state)
        {
            Log($"OnStart for {part} in {HighLogic.LoadedScene}");
            SetFallbackConfigForLegacyCraft();
            SetupConfigNameFields();
            SetControllableMassForLegacyCraft();
            AvionicsConfigChanged();
            SetupGUI();
            base.OnStart(state);
            massLimit = controllableMass;
            _started = true;
            if (cachedEventData != null)
                OnPartVolumeChanged(cachedEventData);
        }

        public void Start()
        {
            // Delay SetScienceContainer to Unity.Start() to allow PartModule removal
            if (!HighLogic.LoadedSceneIsEditor)
                SetScienceContainer();
        }

        #endregion

        #region OnStart Utilities

        private void SetFallbackConfigForLegacyCraft()
        {
            if (HighLogic.LoadedSceneIsEditor && !ProceduralAvionicsTechManager.GetAvailableConfigs().Contains(avionicsConfigName))
            {
                string s = avionicsConfigName;
                avionicsConfigName = ProceduralAvionicsTechManager.GetPurchasedConfigs().First();
                Log($"Current config ({s}) not available, defaulting to {avionicsConfigName}");
            }
            if (string.IsNullOrEmpty(avionicsTechLevel))
            {
                avionicsTechLevel = ProceduralAvionicsTechManager.GetMaxUnlockedTech(avionicsConfigName);
                Log($"Defaulting avionics tech level to {avionicsTechLevel}");
            }
        }

        private void SetControllableMassForLegacyCraft()
        {
            if (controllableMass < 0)
            {
                controllableMass = HighLogic.LoadedSceneIsFlight ? float.MaxValue : 0;
            }
        }

        private void SetupConfigNameFields()
        {
            Fields[nameof(avionicsConfigName)].guiActiveEditor = true;
            var range = Fields[nameof(avionicsConfigName)].uiControlEditor as UI_ChooseOption;
            range.options = ProceduralAvionicsTechManager.GetPurchasedConfigs().ToArray();

            if (string.IsNullOrEmpty(avionicsConfigName))
            {
                avionicsConfigName = range.options[0];
                Log($"Defaulted config to {avionicsConfigName}");
            }
        }

        private void SetupGUI()
        {
            Fields[nameof(controllableMass)].uiControlEditor.onFieldChanged = ControllableMassChanged;
            Fields[nameof(avionicsConfigName)].uiControlEditor.onFieldChanged = AvionicsConfigChanged;
            Fields[nameof(massLimit)].guiActiveEditor = false;
            if (!(GetSeekVolumeMethod(out PartModule _) is System.Reflection.MethodInfo))
            {
                Events[nameof(SeekVolume)].active = Events[nameof(SeekVolume)].guiActiveEditor = false;
                Fields[nameof(targetUtilization)].guiActiveEditor = false;
            }
        }

        #endregion

        private void UpdateControllableMassSlider()
        {
            Fields[nameof(controllableMass)].guiActiveEditor = !IsScienceCore;
            UI_FloatEdit controllableMassEdit = Fields[nameof(controllableMass)].uiControlEditor as UI_FloatEdit;

            if (CurrentProceduralAvionicsConfig != null && CurrentProceduralAvionicsTechNode != null)
            {
                // Formula for controllable mass given avionics mass is Mathf.Pow(1000*avionicsMass / massFactor - massConstant, 1 / massExponent)
                controllableMassEdit.maxValue = Mathf.Max(GetMaximumControllableMass(), 0.001f);
            }
            else
                controllableMassEdit.maxValue = 0.001f;
            Log($"UpdateControllableMassSlider() MaxCtrlMass: {controllableMassEdit.maxValue}");
            controllableMassEdit.minValue = 0;
            controllableMassEdit.incrementSmall = GetSmallIncrement(controllableMassEdit.maxValue);
            controllableMassEdit.incrementLarge = controllableMassEdit.incrementSmall * 10;
            controllableMassEdit.incrementSlide = GetSliderIncrement(controllableMassEdit.maxValue);
            controllableMassEdit.sigFigs = GetSigFigs(controllableMassEdit.maxValue);
            controllableMassEdit.maxValue = FloorToPrecision(controllableMassEdit.maxValue, controllableMassEdit.incrementSlide);
        }

        #region UI Slider Tools

        private int GetSigFigs(float value)
        {
            var smallIncrementExponent = GetSmallIncrementExponent(value);
            return Math.Max(1 - (int)smallIncrementExponent, 0);
        }

        private float CeilingToSmallIncrement(float value)
        {
            var smallIncrement = GetSmallIncrement(value);
            return Mathf.Ceil(value / smallIncrement) * smallIncrement;
        }

        private float FloorToPrecision(float value, float precision) => Mathf.Floor(value / precision) * precision;

        private float GetSliderIncrement(float value)
        {
            var smallIncrement = GetSmallIncrement(value);
            return Math.Min(smallIncrement / 10, 1f);
        }

        private float GetSmallIncrement(float value)
        {
            var exponent = GetSmallIncrementExponent(value);
            return (float)Math.Pow(10, exponent);
        }

        private double GetSmallIncrementExponent(float maxValue)
        {
            var log = Math.Log(maxValue, 10);
            return Math.Max(Math.Floor(log - 1.3), -2);
        }

        #endregion

        private float cachedVolume = float.MaxValue;
        private BaseEventDetails cachedEventData = null;

        #region Events and Change Handlers

        private void ControllableMassChanged(BaseField arg1, object arg2)
        {
            Log($"ControllableMassChanged to {arg1.GetValue(this)} from {arg2}");
            if (float.IsNaN(controllableMass))
            {
                Debug.LogError("ProcAvi - ControllableMassChanged tried to set to NAN! Resetting to 0.");
                controllableMass = 0;
            }
            ClampControllableMass();
            massLimit = controllableMass;
            SendRemainingVolume();
            RefreshDisplays();
        }

        private void AvionicsConfigChanged(BaseField f, object obj)
        {
            avionicsTechLevel = ProceduralAvionicsTechManager.GetMaxUnlockedTech(avionicsConfigName);
            AvionicsConfigChanged();
        }

        private void AvionicsConfigChanged()
        {
            CurrentProceduralAvionicsConfig = ProceduralAvionicsTechManager.GetProceduralAvionicsConfig(avionicsConfigName);
            Log($"Avionics Config changed to: {avionicsConfigName}.  Tech: {avionicsTechLevel}");
            interplanetary = CurrentProceduralAvionicsTechNode.interplanetary;
            ClampControllableMass();
            if (_started)
            {
                // Don't fire these if cachedVolume isn't known yet.
                Log("UpdateControllableMassSlider in AvionicsConfigChanged");
                UpdateControllableMassSlider();
                SendRemainingVolume();
            }
            if (!GetToggleable())
                systemEnabled = true;
            SetActionsAndGui();
            RefreshDisplays();
            if (HighLogic.LoadedSceneIsEditor)
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        [KSPEvent]
        public void OnPartVolumeChanged(BaseEventDetails eventData)
        {
            float volume = (float)eventData.Get<double>("newTotalVolume");
            Log($"OnPartVolumeChanged to {volume} from {cachedVolume}");
            if (!_started)
            {
                Log("Delaying OnPartVolumeChanged until after Start()");
                cachedEventData = eventData;
                return;
            }
            cachedVolume = volume;
            ClampControllableMass();
            UpdateControllableMassSlider();
            SendRemainingVolume();
            RefreshDisplays();
        }

        private void SendRemainingVolume()
        {
            if (_started && cachedVolume < float.MaxValue)
            {
                Events[nameof(OnPartVolumeChanged)].active = false;
                InternalTanksVolume = SphericalTankUtilities.GetSphericalTankVolume(GetAvailableVolume());
                float availVol = GetAvailableVolume();
                Log($"SendRemainingVolume():  Cached Volume: {cachedVolume}. AvionicsVolume: {GetAvionicsVolume()}.  AvailableVolume: {availVol}.  Internal Tanks: {InternalTanksVolume}");
                SendVolumeChangedEvent(InternalTanksVolume);
                Events[nameof(OnPartVolumeChanged)].active = true;
            }
        }

        private float GetAvailableVolume() => Math.Max(Math.Min((cachedVolume - GetAvionicsVolume()) * InternalTanksAvailableVolumeUtilization, cachedVolume * InternalTanksTotalVolumeUtilization), 0);

        public void SendVolumeChangedEvent(double newVolume)
        {
            var data = new BaseEventDetails(BaseEventDetails.Sender.USER);
            data.Set<string>("volName", "Tankage");
            data.Set<double>("newTotalVolume", newVolume);
            part.SendEvent(nameof(OnPartVolumeChanged), data, 0);
        }

        #endregion

        public new static string BackgroundUpdate(Vessel v,
            ProtoPartSnapshot part_snapshot, ProtoPartModuleSnapshot module_snapshot,
            PartModule proto_part_module, Part proto_part,
            Dictionary<string, double> availableResources, List<KeyValuePair<string, double>> resourceChangeRequest,
            double elapsed_s) => ModuleAvionics.BackgroundUpdate(v, part_snapshot, module_snapshot, proto_part_module, proto_part, availableResources, resourceChangeRequest, elapsed_s);

        private void RefreshDisplays()
        {
            RefreshPowerDisplay();
            massDisplay = MathUtils.FormatMass(CurrentProceduralAvionicsTechNode.avionicsDensity > 0 ? GetShieldedAvionicsMass() : 0);
            costDisplay = $"{Mathf.Round(CurrentProceduralAvionicsTechNode.avionicsDensity > 0 ? GetAvionicsCost() : 0)}";
            utilizationDisplay = $"{Utilization * 100:0.#}%";
            Log($"RefreshDisplays() Controllable mass: {controllableMass}, mass: {massDisplay} cost: {costDisplay}, Utilization: {utilizationDisplay}");
        }

        private void RefreshPowerDisplay()
        {
            var powerConsumptionBuilder = StringBuilderCache.Acquire();
            AppendPowerString(powerConsumptionBuilder, GetEnabledkW());
            float dkW = GetDisabledkW();
            if (dkW > 0)
            {
                powerConsumptionBuilder.Append(" /");
                AppendPowerString(powerConsumptionBuilder, dkW);
            }
            powerRequirementsDisplay = powerConsumptionBuilder.ToStringAndRelease();
        }

        private void AppendPowerString(System.Text.StringBuilder builder, float val)
        {
            if (val >= 1)
                builder.AppendFormat(KwFormat, val).Append("\u2009kW");
            else
                builder.AppendFormat(WFormat, val * 1000).Append("\u2009W");
        }

        private void SetScienceContainer()
        {
            if (!CurrentProceduralAvionicsTechNode.hasScienceContainer)
            {
                if (part.FindModuleImplementing<ModuleScienceContainer>() is ModuleScienceContainer module)
                    part.RemoveModule(module);
            }
            Log($"Setting science container to {(CurrentProceduralAvionicsTechNode.hasScienceContainer ? "enabled." : "disabled.")}");
        }

        [KSPField(guiActiveEditor = true, guiName = "Configure", groupName = PAWGroup),
        UI_Toggle(enabledText = "Hide GUI", disabledText = "Show GUI"),
        NonSerialized]
        public bool showGUI;

        private Rect windowRect = new Rect(200, Screen.height - 400, 400, 300);

        public void OnGUI()
        {
            if (showGUI)
            {
                windowRect = GUILayout.Window(GetInstanceID(), windowRect, WindowFunction, "Configure Procedural Avionics");
            }
        }

        private int selectedConfigIndex = 0;
        void WindowFunction(int windowID)
        {
            var configNames = ProceduralAvionicsTechManager.GetAvailableConfigs().ToArray();
            selectedConfigIndex = GUILayout.Toolbar(selectedConfigIndex, configNames);
            var guiAvionicsConfigName = configNames[selectedConfigIndex];
            var currentlyDisplayedConfigs = ProceduralAvionicsTechManager.GetProceduralAvionicsConfig(guiAvionicsConfigName);
            foreach (var techNode in currentlyDisplayedConfigs.TechNodes.Values)
            {
                if (!techNode.IsAvailable)
                {
                    continue;
                }
                if (techNode == CurrentProceduralAvionicsTechNode)
                {
                    GUILayout.Label("Current Config: " + techNode.name);
                    GUILayout.Label("Storage Container: " + (techNode.hasScienceContainer ? "Yes" : "No"));
                }
                else
                {
                    var switchedConfig = false;
                    var unlockCost = ProceduralAvionicsTechManager.GetUnlockCost(guiAvionicsConfigName, techNode);
                    if (unlockCost == 0)
                    {
                        if (GUILayout.Button("Switch to " + BuildTechName(techNode)))
                        {
                            switchedConfig = true;
                        }
                    }
                    else if (Funding.Instance.Funds < unlockCost)
                    {
                        GUILayout.Label("Can't afford " + BuildTechName(techNode) + BuildCostString(unlockCost));
                    }
                    else if (GUILayout.Button("Purchase " + BuildTechName(techNode) + BuildCostString(unlockCost)))
                    {
                        switchedConfig = true;
                        if (!HighLogic.CurrentGame.Parameters.Difficulty.BypassEntryPurchaseAfterResearch)
                        {
                            switchedConfig = ProceduralAvionicsTechManager.PurchaseConfig(guiAvionicsConfigName, techNode);
                        }
                        if (switchedConfig)
                        {
                            ProceduralAvionicsTechManager.SetMaxUnlockedTech(guiAvionicsConfigName, techNode.name);
                        }

                    }
                    if (switchedConfig)
                    {
                        Log("Configuration window changed, updating part window");
                        SetupConfigNameFields();
                        avionicsTechLevel = techNode.name;
                        CurrentProceduralAvionicsConfig = currentlyDisplayedConfigs;
                        avionicsConfigName = guiAvionicsConfigName;
                        AvionicsConfigChanged();
                        MonoUtilities.RefreshContextWindows(part);
                    }
                }
            }
            GUILayout.Label(" ");
            if (GUILayout.Button("Close"))
            {
                showGUI = false;
            }

            GUI.DragWindow();
        }

        private string BuildTechName(ProceduralAvionicsTechNode techNode)
        {
            var sbuilder = StringBuilderCache.Acquire();
            sbuilder.Append(techNode.name);
            sbuilder.Append(BuildSasAndScienceString(techNode));

            return sbuilder.ToStringAndRelease();
        }

        private static string BuildSasAndScienceString(ProceduralAvionicsTechNode techNode) => techNode.hasScienceContainer ? " {SC}" : "";

        private string BuildCostString(int cost) =>
            (cost == 0 || HighLogic.CurrentGame.Parameters.Difficulty.BypassEntryPurchaseAfterResearch) ? string.Empty : $" ({cost:N})";
    }
}
