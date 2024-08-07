﻿using KSPAPIExtensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

using EMRController.Config;
using EMRController.Utils;
using System.Reflection;
using RealFuels; // Pike change


namespace EMRController
{
	public class EMRController : PartModule
	{

		#region startup

		[SerializeField]
		public byte[] mixtureConfigNodesSerialized;

		[KSPField]
		public string currentConfigName;

		private MixtureConfigNodeProcessor processor;

        private MixtureConfigNodePair CurrentNodePair {
			get {
				if (processor == null) {
					DeserializeNodes();
				}

				MixtureConfigNodePair pair = processor.GetForConfigName(currentConfigName);
				if (pair != null) {
					return pair;
				}

				return MixtureConfigNodePair.NotConfigured();
			}
		}

		public override void OnLoad(ConfigNode node)
		{
			EMRUtils.Log("OnLoad called");
            if (GameSceneFilter.AnyInitializing.IsLoaded()) {
				EMRUtils.Log("Loading");
				LoadMixtureConfigNodes(node);
				EMRUtils.Log("Loaded");
			}
			base.OnLoad(node);
		}

		private void LoadMixtureConfigNodes(ConfigNode node)
		{
			processor = new MixtureConfigNodeProcessor(node);
			mixtureConfigNodesSerialized = processor.Serialized;
            EMRUtils.Log("Serialized ratios");
        }
		#endregion

		#region In Flight Controls
		[KSPField]
		public bool emrInClosedLoop;

		[KSPEvent(guiActive = true, guiActiveEditor = false)]


        public void ChangeEMRMode()
		{
			if (!CurrentNodePair.Disabled) {
				emrInClosedLoop = !emrInClosedLoop;
				UpdateInFlightEMRParams();
                InFlightUIChanged(null, null);
            }
        }

		[KSPAction("Change EMR Mode")]
		public void ChangeEMRModeAction(KSPActionParam param)
		{
			ChangeEMRMode();
		}

		[KSPField(isPersistant = true, guiName = "Current EMR", guiActiveEditor = false, guiUnits = ":1"),
			UI_FloatEdit(incrementSmall = 0.1f, incrementLarge = 1.0f, incrementSlide = 0.01f, sigFigs = 2, unit = ":1", scene = UI_Scene.Flight)]
		public float currentEMR;

		[KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "EMR")]
		public string closedLoopEMRText;

		[KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "ISP")]
		public string currentEMRText;

		[KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "Reserve")]
		public string currentReserveText;

		#endregion

		#region In Flight Functions

		private void UpdateInFlightEMRParams()
		{
             if (CurrentNodePair.Disabled) {
				return;
			}

			if (emrInClosedLoop) {
				EMRUtils.Log("Closed Loop Detected");
				float bestEMR = GetOptimalRatioForRemainingFuel();
				EMRUtils.Log("Best EMR computed to be ", bestEMR, ":1");
				string bestEMRSuffix = "";
				if (bestEMR > CurrentNodePair.Max.ratio) {
					EMRUtils.Log("EMR higher than ", CurrentNodePair.Max.ratio, ":1 (was ", bestEMR, ":1), capping");
					bestEMR = CurrentNodePair.Max.ratio;
					bestEMRSuffix = " (max)";
				}
				else if (bestEMR < CurrentNodePair.Min.ratio) {
					EMRUtils.Log("EMR lower than ", CurrentNodePair.Min.ratio, ":1 (was ", bestEMR, ":1), capping");
					bestEMR = CurrentNodePair.Min.ratio;
					bestEMRSuffix = " (min)";
				}

				currentEMR = bestEMR;
				closedLoopEMRText = MathUtils.RoundSigFigs(bestEMR).ToString() + ":1" + bestEMRSuffix;
			}

            Fields["currentEMR"].guiActive = !emrInClosedLoop;
			Fields["closedLoopEMRText"].guiActive = emrInClosedLoop;


            UI_FloatEdit currentEMREditor = (UI_FloatEdit)Fields["currentEMR"].uiControlFlight;
			currentEMREditor.minValue = CurrentNodePair.Min.ratio;
			currentEMREditor.maxValue = CurrentNodePair.Max.ratio;
        }

		private bool inFlightCallbacksBound = false;
		private void BindInFlightCallbacks()
		{
			if (!inFlightCallbacksBound) {
				//EMRUtils.Log("Binding In Flight Callbacks");
				string[] editorNames = new string[] { "currentEMR" };
				foreach (var editorName in editorNames) {
					Fields[editorName].uiControlFlight.onFieldChanged += InFlightUIChanged;
				}
				inFlightCallbacksBound = true;
			}
            //EMRUtils.Log("Done Binding In Flight Callbacks");
            UpdateEnginePropUsage(); //Pike change, added here so usage is updated
        }

		private float lastEMR = -1;
		private void InFlightUIChanged(BaseField baseField, object obj) // Pike note. this updates everytick
		{
			if (CurrentNodePair.Disabled) {
				return;
			}
            currentReserveText = BuildInFlightFuelReserveText();
			//EMRUtils.Log("Setting reserve text for EMR ", currentEMR, ":1 to: ", currentReserveText);

			//only going to do all this if emr actually changed
			if (lastEMR == currentEMR) {
				return;
			}
			lastEMR = currentEMR;
			//EMRUtils.Log("In Flight UI Changed");
			UpdateEngineFloatCurve();//Pike change, this needs to be here to ensure the isp and thrust are correct, but does not updte the Mixture Ratio
            UpdateInFlightIspAndThrustDisplays();
		}

		private void UpdateInFlightIspAndThrustDisplays()
		{
            currentEMRText = BuildIspAndThrustString(GenerateMixtureConfigNodeForRatio(currentEMR));
		}

		private void UpdateEngineFloatCurve()
		{
			// If it's less then the min ratio, we're going to assume that it's not initialized, 
			// so we'll bring it up to max which is normally where it would start
			if (currentEMR < CurrentNodePair.Min.ratio || currentEMR > CurrentNodePair.Max.ratio) {
				currentEMR = CurrentNodePair.Max.ratio;
			}

			float fullRatioDiff = CurrentNodePair.Max.ratio - CurrentNodePair.Min.ratio;
			float currentRatioDiff = currentEMR - CurrentNodePair.Min.ratio;
			float ratioPercentage = currentRatioDiff / fullRatioDiff;

			MixtureConfigNode current = GenerateMixtureConfigNodeForRatio(currentEMR);
            FloatCurve newCurve = FloatCurveTransformer.GenerateForPercentage(CurrentNodePair.Min.atmosphereCurve, CurrentNodePair.Max.atmosphereCurve, ratioPercentage); //Pike this produces a new ISP curve for the atmosphere
            UpdateThrust(current.maxThrust, current.minThrust, newCurve); //Pike change, need to push ISP curve to RealFuels
            engineModule.atmosphereCurve = newCurve;//Pike change, add the atmosphere curve to be loaded int. To be tested without this to see if it  works
			engineModule.maxFuelFlow = current.maxThrust / (newCurve.Evaluate(0.0f) * engineModule.g);

            //EMRUtils.Log("Setting max thrust to ", current.maxThrust);
            //EMRUtils.Log("Fuel flow set to ", engineModule.maxFuelFlow);
        }

        private void UpdateEnginePropUsage()  //Pike change,  Updates the propellant ratios in Real Fuels (used for mass flow rate) but not the MR
		{

			float effectiveSpoolUpTimeBase = engineModuleRF.effectiveSpoolUpTime; //Pike change, attempt to stop engine re-igniting
            Dictionary<int, float> ratios = GetRatiosForEMR(propellantResources, currentEMR);
            foreach (Propellant prop in engineModule.propellants) {
                if (ratios.ContainsKey(prop.id)) {
					prop.ratio = ratios[prop.id];
                    engineModuleRF.propellants[0].ratio = 1f - prop.ratio;//Pike change, added 1f instead of 1 to force float
					engineModuleRF.propellants[1].ratio = prop.ratio;//Pike change [1] is always the oxidizer
					if (engineModuleRF.EngineIgnited) //Pike change, attempt to stop engine re-igniting
                    {
						engineModuleRF.effectiveSpoolUpTime = 0f;

                    }
                    engineModuleRF.OnEngineConfigurationChanged();//Pike change, this seems to re-start ignition but is required for MR updates.TODO: Need to work out how to stop it re-igniting the engine
                    EMRUtils.MyDebugLog("OnEngineConfigurationChanged inside PropUsage and Spool"); //Pike change
					engineModuleRF.effectiveSpoolUpTime = effectiveSpoolUpTimeBase; //Pike change, attempt to stop engine re-igniting

                }
            }
        }

		private string BuildInFlightFuelReserveText()
		{
			//EMRUtils.Log("Building new PropellantResources to build fuel reserve text");
			PropellantResources propResources = new PropellantResources(engineModule);

			Dictionary<int, double> propAmounts = new Dictionary<int, double>();
			foreach (var prop in propResources) {
				double propVolume;
				double propMaxVolume;
				//EMRUtils.Log("About to get In Flight Fuel Reserve Text");
				try {
					//EMRUtils.Log("Trying to get resource totals: ", prop.Name);
					part.GetConnectedResourceTotals(prop.Id, out propVolume, out propMaxVolume);
					propAmounts.Add(prop.Id, propVolume / prop.Ratio);
					//EMRUtils.Log("Found for ", prop.Name, ": ", propVolume);
				}
				catch (Exception ex) {
					//EMRUtils.Log("Error trying to get resource ", prop.Name, " (", ex.Message, ")");
					return "UNKNOWN";
				}
			}

			double minAmount = propAmounts.Min(item => item.Value);
			//EMRUtils.Log("Min Amount: ", minAmount);

			StringBuilder result = StringBuilderCache.Acquire();
			foreach (var kvp in propAmounts) {
				double propDiff = kvp.Value - minAmount;
				//EMRUtils.Log("Diff from min: ", propDiff);
				if (propDiff > 0) {
					//EMRUtils.Log("Diff GT 0");
					if (result.Length > 0) {
						result.Append(Environment.NewLine);
						//EMRUtils.Log("Adding newline");
					}
					PropellantResource propResource = propResources.GetById(kvp.Key);
					double fuelVolume = propDiff * propResource.Ratio;
					if (fuelVolume * propResource.Density > .001) {
						result.Append(propResource.Name).Append(": ").Append(FormatVolumeAndMass(fuelVolume, propResource.Density));
					}
					//EMRUtils.Log("Text now reads: ", result);
				}
			}
			if (result.Length == 0) {
				result.Append("None");
			}
			return result.ToStringAndRelease();
		}

		private string FormatVolumeAndMass(double diff, double density)
		{
			double remainingMass = diff * density;
			return MathUtils.ToStringSI(diff, 4, 0, "L") + " / " + MathUtils.FormatMass(remainingMass, 3);
		}

		private float GetOptimalRatioForRemainingFuel()
		{
			PropellantResources propResources = new PropellantResources(engineModule);
			if (propResources == null) {
				EMRUtils.Log("Could not find any connected resources");
				return CurrentNodePair.Min.ratio;
			}

			double amount;
			double maxAmount;
			EMRUtils.Log("Getting resources totals from part");

			if (part == null) {
				//DELETE ME
				EMRUtils.Log("I don't know how, but part is null");
			}
			part.GetConnectedResourceTotals(propResources.Oxidizer.Id, out amount, out maxAmount); // Pike need to look into this to see if can be used to change ratios

			double remainingOxidizer = amount;

			double remainingFuel = 0;
			foreach (var fuel in propResources.Fuels) {
				part.GetConnectedResourceTotals(fuel.Id, out amount, out maxAmount);
				remainingFuel += amount;
			}

			EMRUtils.Log("Remaining Fuel: " + remainingFuel + ", Remaining Oxidier: " + remainingOxidizer);

			if (remainingOxidizer == 0) {
				return CurrentNodePair.Min.ratio;
			}
			if (remainingFuel == 0) {
				return CurrentNodePair.Max.ratio;
			}

			return (float)((remainingOxidizer * propResources.Oxidizer.Density) / (remainingFuel * propResources.AverageFuelDensity));
		}

		#endregion

		#region Editor Controls
		[KSPField(isPersistant = true, guiName = "Starting EMR", guiActive = false, guiActiveEditor = false, guiUnits = ":1"),
			UI_FloatEdit(incrementSmall = 0.1f, incrementLarge = 1.0f, incrementSlide = 0.01f, sigFigs = 2, unit = ":1")]
		public float startingEMR;

		[KSPField(isPersistant = false, guiActive = false, guiActiveEditor = true, guiName = "ISP")]
		public string startingEMRText;

		[KSPField(isPersistant = true, guiName = "Final EMR", guiActive = false, guiActiveEditor = false, guiUnits = ":1"),
			UI_FloatEdit(incrementSmall = 0.1f, incrementLarge = 1.0f, incrementSlide = 0.01f, sigFigs = 2, unit = ":1")]
		public float finalEMR;

		[KSPField(isPersistant = false, guiActive = false, guiActiveEditor = true, guiName = "ISP")]
		public string finalEMRText;

		[KSPField(isPersistant = true, guiName = "Percentage at Final EMR", guiActive = false, guiActiveEditor = false, guiUnits = "%"),
			UI_FloatRange(minValue = 0, maxValue = 100, stepIncrement = 1, scene = UI_Scene.Editor)]
		public float emrSplitPercentage;

		[KSPField(isPersistant = true, guiName = "Boiloff Reserve Percentage", guiActive = false, guiActiveEditor = true, guiUnits = "%"),
			UI_FloatRange(minValue = -50, maxValue = 50, stepIncrement = 1, scene = UI_Scene.Editor)]
		public float fuelReservePercentage;

		[KSPField(isPersistant = false, guiActive = false, guiActiveEditor = true, guiName = "Extra")]
		public string fuelReserveText;

		[KSPField(isPersistant = true)]
		public bool emrEnabled;

		[KSPEvent(guiActive = true, guiActiveEditor = true)]
		public void ToggleEMR()
		{
			if (!CurrentNodePair.Disabled) {
				emrEnabled = !emrEnabled;
				SetActionsAndGui();
			}
		}

		#endregion

		#region Editor Functions


		ModuleEngines engineModule = null;
        ModuleEnginesRF engineModuleRF = null; //Pike change
        public override void OnStart(StartState state)
		{
			base.OnStart(state);
			if (engineModule == null) {
				engineModule = part.FindModuleImplementing<ModuleEngines>();
                engineModuleRF = part.FindModuleImplementing<ModuleEnginesRF>(); //Pike change 
            }

            if (engineModule == null) {
				EMRUtils.Log("ERROR! Could not find ModuleEngines");
            }

			if (propellantResources == null) {
				propellantResources = new PropellantResources(engineModule);
			}

			EMRUtils.Log("Detecting configs");
			DetectConfig();
			DeserializeNodes();
			BindCallbacks();

			ToggleControlsBasedOnConfigs();
			if (!CurrentNodePair.Disabled) {
				SetEditorFields();
			}
			SetActionsAndGui();

			if (HighLogic.LoadedSceneIsFlight) {
				BindInFlightCallbacks();
				UpdateInFlightEMRParams();
			}

			UpdateIspAndThrustDisplay();
			SetNeededFuel();

			if (HighLogic.LoadedSceneIsFlight) {
				InFlightUIChanged(null, null);
			}
        }

        private void ToggleControlsBasedOnConfigs()
		{
			EMRUtils.Log("Toggling Controls based on configs");
			if (CurrentNodePair.Disabled) {
				emrEnabled = false;
				EMRUtils.Log("Current node pair is disabled, disabling EMR");
			}

			Events["ToggleEMR"].guiActive = !CurrentNodePair.Disabled;
			Events["ToggleEMR"].guiActiveEditor = !CurrentNodePair.Disabled;
			Events["ChangeEMRMode"].guiActive = !CurrentNodePair.Disabled;

			string[] fields = new string[] { "closedLoopEMRText", "currentEMRText", "currentReserveText" };
			foreach (string field in fields) {
				Fields[field].guiActive = !CurrentNodePair.Disabled;
			}

			Actions["ChangeEMRModeAction"].active = !CurrentNodePair.Disabled;
		}

		private PropellantResources propellantResources;
		private void SetNeededFuel()
		{
			if (!CurrentNodePair.Disabled) {
				if (propellantResources == null) {
					propellantResources = new PropellantResources(engineModule);
				}
				SetNewRatios(propellantResources, startingEMR, finalEMR, emrSplitPercentage);
            }
        }

        private void SetNewRatios(PropellantResources propellantResources, float startingEMR, float finalEMR, float emrSplitPercentage) //Pike this deteremine the optimalratio
		{
			Dictionary<int, float> startRatios = GetRatiosForEMR(propellantResources, startingEMR);
			Dictionary<int, float> endRatios = GetRatiosForEMR(propellantResources, finalEMR);

			foreach (var prop in engineModule.propellants) {
				if (endRatios.ContainsKey(prop.id) && startRatios.ContainsKey(prop.id)) {
					var ratioDiff = endRatios[prop.id] - startRatios[prop.id];
					//EMRUtils.Log("Ratio Diff for ", prop.name, ": ", ratioDiff);
					prop.ratio = startRatios[prop.id] + ((emrSplitPercentage / 100) * ratioDiff);
				}
				else {
					prop.ratio = propellantResources.GetById(prop.id).Ratio;
				}
				//EMRUtils.Log("New ratio for ", prop.name, ": ", prop.ratio);
				if (propellantResources.Oxidizer.Id == prop.id && fuelReservePercentage > 0) {
					//EMRUtils.Log("Adjusting oxidizer capacity to account for boiloff");
					prop.ratio = prop.ratio * ((100 - fuelReservePercentage) / 100);
				}
				if (propellantResources.Oxidizer.Id != prop.id && fuelReservePercentage < 0) {
					//EMRUtils.Log("Adjusting fuel capacity to account for boiloff");
					prop.ratio = prop.ratio * ((100 + fuelReservePercentage) / 100);
				}
			} 
        }

        Dictionary<int, float> GetRatiosForEMR(PropellantResources propellantResources, float EMR)
		{
			// right now, the ratio is a volume ratio, so we need to convert that to a mass ratio
            // EMR = oxidizer mass flow rate
            // 1 = fuel mass flow rate

            var oxden = propellantResources.Oxidizer.Density; //Pike change
			var fden = propellantResources.Fuels[0].Density;//Pike change and assumes only one fuel or any further are insignificant
            

			var oxidizerRatio = ((EMR * fden)/(oxden+(EMR*fden))); // Pike Chnage, previously call on propellant resource and used mass flow rate but the ratio result was incorrect (despite inthoery being ale to use two fuels). Cal Re-done to base on density and MER only.
            EMRUtils.MyDebugLog("Oxidizer ratio mod: " + oxidizerRatio); //Pike change

            Dictionary<int, float> ratios = new Dictionary<int, float>();
			ratios.Add(propellantResources.Oxidizer.Id, oxidizerRatio);
			return ratios;
		}

		private bool callbacksBound = false;
		private void BindCallbacks()
		{
			if (!callbacksBound) {
				string[] editorNames = new string[] { "startingEMR", "finalEMR", "emrSplitPercentage", "fuelReservePercentage" };
				foreach (var editorName in editorNames) {
					Fields[editorName].uiControlEditor.onFieldChanged += UIChanged;
				}
				callbacksBound = true;
			}
			//EMRUtils.Log("Bound Callbacks");
		}

		private void UIChanged(BaseField baseField, object obj) // Pike Not sure how often this is updated as could not get the debug to show when this is run
		{
			UpdateIspAndThrustDisplay();
			SetNeededFuel();
			UpdateAllParts();
        }

		private void UpdateAllParts() // Pike Not sure how often this is updated as never called on debug menu
        {
			List<Part> parts;
			if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch.ship != null)
				parts = EditorLogic.fetch.ship.parts;
			else if (HighLogic.LoadedSceneIsFlight && vessel != null)
				parts = vessel.parts;
			else parts = new List<Part>();
			for (int i = parts.Count - 1; i >= 0; --i)
				parts[i].SendMessage("UpdateUsedBy", SendMessageOptions.DontRequireReceiver);

        }

		private void UpdateIspAndThrustDisplay()
		{
			if (CurrentNodePair.Disabled) {
				return;
			}
			startingEMRText = BuildIspAndThrustString(GenerateMixtureConfigNodeForRatio(startingEMR));
			finalEMRText = BuildIspAndThrustString(GenerateMixtureConfigNodeForRatio(finalEMR));
			fuelReserveText = BuildFuelReserveText(fuelReservePercentage);
		}

		private string BuildIspAndThrustString(MixtureConfigNode node)
		{
			return node.atmosphereCurve.Evaluate(0) + "s   Thrust: " + MathUtils.ToStringSI(node.maxThrust, 4, 0, "N");
		}

		private string BuildFuelReserveText(float fuelReservePercentage)
		{
			if (fuelReservePercentage == 0) {
				return "balanced";
			}
			string richProp = fuelReservePercentage > 0 ? "fuel" : "oxidizer";
			return Math.Abs(fuelReservePercentage).ToString() + "% " + richProp;
		}

		private MixtureConfigNode GenerateMixtureConfigNodeForRatio(float ratio)
		{
			float fullRatioDiff = CurrentNodePair.Max.ratio - CurrentNodePair.Min.ratio;
			float currentRatioDiff = ratio - CurrentNodePair.Min.ratio;
			float ratioPercentage = currentRatioDiff / fullRatioDiff;

			MixtureConfigNode resultNode = new MixtureConfigNode() {
				configName = CurrentNodePair.Min.configName,
				ratio = ratio,
				atmosphereCurve = FloatCurveTransformer.GenerateForPercentage(CurrentNodePair.Min.atmosphereCurve, CurrentNodePair.Max.atmosphereCurve, ratioPercentage),
				maxThrust = (ratioPercentage * (CurrentNodePair.Max.maxThrust - CurrentNodePair.Min.maxThrust)) + CurrentNodePair.Min.maxThrust,
				minThrust = (ratioPercentage * (CurrentNodePair.Max.minThrust - CurrentNodePair.Min.minThrust)) + CurrentNodePair.Min.minThrust
			};
			//EMRUtils.Log("Resultant node: ", resultNode);
			return resultNode;
		}

		private void SetEditorFields()
		{
			EMRUtils.Log("Setting editor fields");
			UI_FloatEdit startFloatEdit = (UI_FloatEdit)Fields["startingEMR"].uiControlEditor;
			UI_FloatEdit finalFloatEdit = (UI_FloatEdit)Fields["finalEMR"].uiControlEditor;
			startFloatEdit.minValue = CurrentNodePair.Min.ratio;
			startFloatEdit.maxValue = CurrentNodePair.Max.ratio;
			finalFloatEdit.minValue = CurrentNodePair.Min.ratio;
			finalFloatEdit.maxValue = CurrentNodePair.Max.ratio;

			if (startingEMR < CurrentNodePair.Min.ratio || startingEMR > CurrentNodePair.Max.ratio) {
				startingEMR = CurrentNodePair.Max.ratio;
			}
			if (finalEMR < CurrentNodePair.Min.ratio || finalEMR > CurrentNodePair.Max.ratio) {
				finalEMR = CurrentNodePair.Min.ratio;
			}
        }

		private void SetActionsAndGui()
		{
			Events["ToggleEMR"].guiName = (emrEnabled ? "Hide" : "Show") + " EMR Controller";
			string[] fieldsToShow = new string[] {
				"startingEMR", "finalEMR", "startingEMRText", "finalEMRText",
				"emrSplitPercentage", "fuelReservePercentage", "fuelReserveText"
			};
			foreach (string field in fieldsToShow) {
				Fields[field].guiActiveEditor = emrEnabled;
			}
		}

		private void DeserializeNodes()
		{
			if (processor == null && mixtureConfigNodesSerialized != null) {
				//EMRUtils.Log("ConfigNode Deserialization Needed");
				processor = new MixtureConfigNodeProcessor(mixtureConfigNodesSerialized);
			}
		}
		#endregion

		public void UpdateUsedBy() // Pike Not sure how often this is updated as linked to update all parts
        {
			//EMRUtils.Log("Update Used By Called");
			OnStart(StartState.Editor);
		}

		PartModule mecModule = null;
		private void DetectConfig() //Pike only updated on start
		{
			currentConfigName = "";
			if (mecModule == null) {
				EMRUtils.Log("Detecting ModuleEngineConfigs");
				foreach (var module in part.Modules) {
					if (module.GetType().FullName == "RealFuels.ModuleEngineConfigs") {
						mecModule = module;
						EMRUtils.Log("Found ModuleEngineConfigs");
						break;
					}
				}
			}
			if (mecModule != null) {
				Type moduleType = mecModule.GetType();
				currentConfigName = moduleType.GetField("configuration").GetValue(mecModule).ToString();
			}
		}

		Action<float> ModularEnginesChangeThrust;
		float oldMaxThrust = 1;
		float oldMinThrust = 1;

		private static bool isUpdatingThrust = false;
		private void UpdateThrust(float maxThrust, float minThrust, FloatCurve newISPCurv) //Pike change, updated each tick through float curve
		{
            //EMRUtils.Log("Setting min/max: ", minThrust,"/", maxThrust);
            engineModule.maxThrust = maxThrust; 
            engineModule.minThrust = minThrust; 
            //engineModule.atmosphereCurve = newISPCurv;//Pike Change, test removing this
            UpdateEnginePropUsage();//Pike Change to update usage if the thrust is updated

            if (mecModule != null) {

				if (maxThrust == oldMaxThrust && minThrust == oldMinThrust) {
					return;
				}
				oldMinThrust = minThrust;
				oldMaxThrust = maxThrust;
                //SetNeededFuel(); // Pike change didn't work.
                

                // This is doing the same thing that calling ChangeThrust does for setting the maxThrust, but there's no method there to do that
                List<ConfigNode> mecModuleConfigNodes = (List<ConfigNode>)mecModule.GetType().GetField("configs").GetValue(mecModule);
				if (mecModuleConfigNodes != null) {
					foreach (ConfigNode c in mecModuleConfigNodes) {
						c.SetValue("minThrust", minThrust.ToString());
                        c.RemoveNode("atmosphereCurve");//Pike change it appears modifying the ConfigNode is not required as it can be direclty influenced by the engine module.
                        ConfigNode curve = new ConfigNode("atmosphereCurve");//Pike change this is absolutely needed to ensure the ISP is updated
                        FloatCurve newAtmoCurve = newISPCurv;//Pike change
                        newAtmoCurve.Save(curve);//Pike change
                        c.AddNode(curve);//Pike change
                    }
                }
                        // I'm still calling ChangeThrust with the max thrust, since it calls SetConfiguration
                        if (ModularEnginesChangeThrust == null) {
					ModularEnginesChangeThrust = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), mecModule, "ChangeThrust");
				}

				//Here we're doing a double check lock, just to be sure that the delegate doesn't get called simultaniously and crash KSP
				if (!isUpdatingThrust) {
					lock (this) {
						if (!isUpdatingThrust) {
							isUpdatingThrust = true;
							ModularEnginesChangeThrust(maxThrust);
							isUpdatingThrust = false;
						}
					}
				}
            }
        }

		private int updateInterval = 20;
		private int currentUpdateCount = 0;
		public void FixedUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight || !emrEnabled || --currentUpdateCount > 0) {
				return;
			}

			currentUpdateCount = updateInterval;

			float optimalEMR = GetOptimalRatioForRemainingFuel();
			if (optimalEMR > CurrentNodePair.Max.ratio) {
				optimalEMR = CurrentNodePair.Max.ratio;
			}
			if (optimalEMR < CurrentNodePair.Min.ratio) {
				optimalEMR = CurrentNodePair.Min.ratio;
			}

			string text = "Change to " + (emrInClosedLoop ? "Open" : "Closed") + " Loop";
			if (!emrInClosedLoop) {
				text += " (" + MathUtils.RoundSigFigs(optimalEMR).ToString() + ":1)";
			}
			else {
				UpdateInFlightEMRParams();
            }

			Events["ChangeEMRMode"].guiName = text;
			InFlightUIChanged(null, null);
        }
    }
}
