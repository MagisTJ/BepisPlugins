using System;
using BepInEx;
using BepisPlugins;
using UnityEngine;
using Shared;

namespace ExtensibleSaveFormat
{
    [BepInProcess(Constants.GameProcessName)]
    [BepInProcess(Constants.StudioProcessName)]
    [BepInPlugin(GUID, PluginName, Version)]
    public partial class ExtendedSave : BepInEx.IL2CPP.BasePlugin 
    {
        public ExtendedSave()
        {
            AppDomain.CurrentDomain.UnhandledException += ExceptionHandler;
            Application.runInBackground = true;
            Logger = Log;
        }

        private static void ExceptionHandler(object sender, UnhandledExceptionEventArgs e) => Logger.LogError("\r\n\r\nUnhandled Exception:" + (e.ExceptionObject as Exception).ToString());


        public override void Load()
        {
            #region[Harmony Patching]

            try
            {
                Hooks.InstallHooks();
            }
            catch 
            { 
                Logger.LogMessage("FAILED to Apply Hooks's!");
            }

            #endregion

            Il2CppTypeSupport.Initialize();
        }
    }
}