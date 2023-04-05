using HarmonyLib;
using MessagePack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
#if AI || HS2
using AIChara;
#elif RG
using Chara;
#endif

namespace ExtensibleSaveFormat
{
    public partial class ExtendedSave
    {
        internal static partial class Hooks
        {
            private static bool cardReadEventCalled;

            internal static void InstallHooks()
            {
                var hi = Harmony.CreateAndPatchAll(typeof(Hooks), GUID);

#if KK
                var vrType = AccessTools.TypeByName("VR.VRClassRoomCharaFile");
                if (vrType != null)
                {
                    var vrTarget = AccessTools.DeclaredMethod(vrType, "Start");
                    hi.Patch(original: vrTarget,
                        prefix: new HarmonyMethod(typeof(Hooks), nameof(CustomScenePreHook)),
                        postfix: new HarmonyMethod(typeof(Hooks), nameof(CustomScenePostHook)));
                }

                // Fix ext data getting lost in KK Party live mode. Not needed in KK.
                if (UnityEngine.Application.productName == BepisPlugins.Constants.GameProcessNameSteam)
                {
                    var t = typeof(LiveCharaSelectSprite)
                            .GetNestedType("<Start>c__AnonStorey0", AccessTools.allDeclared)
                            .GetNestedType("<Start>c__AnonStorey1", AccessTools.allDeclared)
                            .GetMethod("<>m__3", AccessTools.allDeclared);
                    hi.Patch(t, transpiler: new HarmonyMethod(typeof(Hooks), nameof(Hooks.PartyLiveCharaFixTpl)));
                }
#endif
            }

            #region ChaFile

            #region Loading
#if KK || KKS
            [HarmonyPrefix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.LoadFile), typeof(BinaryReader), typeof(bool), typeof(bool))]
#elif RG
            [HarmonyPrefix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.LoadFile), typeof(Il2CppSystem.IO.BinaryReader), typeof(int), typeof(bool), typeof(bool))]
#else
            [HarmonyPrefix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.LoadFile), typeof(BinaryReader), typeof(int), typeof(bool), typeof(bool))]
#endif

            private static void ChaFileLoadFilePreHook() => cardReadEventCalled = false;

#if RG
            private static void ChaFileLoadFileHook(ChaFile file, BlockHeader header, Il2CppSystem.IO.BinaryReader reader)
#else
            private static void ChaFileLoadFileHook(ChaFile file, BlockHeader header, BinaryReader reader)
#endif
            {
                var info = header.SearchInfo(Marker);

                if (LoadEventsEnabled && info != null && info.version == DataVersion.ToString())
                {
                    long originalPosition = reader.BaseStream.Position;
#if RG
                    long basePosition = originalPosition;
                    foreach (var lstInfo in header.lstInfo)
                        basePosition -= lstInfo.size;
#else
                    long basePosition = originalPosition - header.lstInfo.Sum(x => x.size);
#endif
                    reader.BaseStream.Position = basePosition + info.pos;

                    byte[] data = reader.ReadBytes((int)info.size);

                    reader.BaseStream.Position = originalPosition;

                    cardReadEventCalled = true;

                    try
                    {
#if RG
                        Il2CppSystem.Buffers.ReadOnlySequence<byte> buffer = new Il2CppSystem.Buffers.ReadOnlySequence<byte>((IntPtr)data[0]);
                        var dictionary = MessagePackSerializer.Deserialize<Dictionary<string, PluginData>>(ref buffer);
#else
                        var dictionary = MessagePackSerializer.Deserialize<Dictionary<string, PluginData>>(data);
#endif
                        internalCharaDictionary.Set(file, dictionary);
                    }
                    catch (Exception e)
                    {
                        internalCharaDictionary.Set(file, new Dictionary<string, PluginData>());
                        string warning = $"Invalid or corrupted extended data in card \"{file.CharaFileName}\" - {e.Message}";
                        Logger.LogWarning(warning);
                    }

                    CardReadEvent(file);
                }
                else
                {
                    internalCharaDictionary.Set(file, new Dictionary<string, PluginData>());
                }
            }

#if !RG
#if KK || KKS
            [HarmonyTranspiler, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.LoadFile), typeof(BinaryReader), typeof(bool), typeof(bool))]
#else
            [HarmonyTranspiler, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.LoadFile), typeof(BinaryReader), typeof(int), typeof(bool), typeof(bool))]
#endif
            private static IEnumerable<CodeInstruction> ChaFileLoadFileTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                List<CodeInstruction> newInstructionSet = new List<CodeInstruction>(instructions);

                //get the index of the first searchinfo call
                int searchInfoIndex = newInstructionSet.FindIndex(instruction => CheckCallVirtName(instruction, "SearchInfo"));

                //get the index of the last seek call
                int lastSeekIndex = newInstructionSet.FindLastIndex(instruction => CheckCallVirtName(instruction, "Seek"));

                var blockHeaderLocalBuilder = newInstructionSet[searchInfoIndex - 2]; //get the localbuilder for the blockheader

                //insert our own hook right after the last seek
                newInstructionSet.InsertRange(lastSeekIndex + 2, //we insert AFTER the NEXT instruction, which is right before the try block exit
                    new[] {
                    new CodeInstruction(OpCodes.Ldarg_0), //push the ChaFile instance
                    new CodeInstruction(blockHeaderLocalBuilder.opcode, blockHeaderLocalBuilder.operand), //push the BlockHeader instance
                    new CodeInstruction(OpCodes.Ldarg_1), //push the binaryreader instance
                    new CodeInstruction(OpCodes.Call, typeof(Hooks).GetMethod(nameof(ChaFileLoadFileHook), AccessTools.all)), //call our hook
                    });

                return newInstructionSet;
            }
#endif

#if KK || KKS
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.LoadFile), typeof(BinaryReader), typeof(bool), typeof(bool))]
            private static void ChaFileLoadFilePostHook(ChaFile __instance, bool __result, BinaryReader br)
#elif RG
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.LoadFile), typeof(Il2CppSystem.IO.BinaryReader), typeof(int), typeof(bool), typeof(bool))]
            private static void ChaFileLoadFilePostHook(ChaFile __instance, bool __result, Il2CppSystem.IO.BinaryReader br, int lang, bool noLoadPNG, bool noLoadStatus)
#else
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.LoadFile), typeof(BinaryReader), typeof(int), typeof(bool), typeof(bool))]
            private static void ChaFileLoadFilePostHook(ChaFile __instance, bool __result, BinaryReader br)
#endif
            {
                if (!__result || __instance.PngData == null || __instance.FacePngData == null)
                    return;
#if RG
                br.BaseStream.Position = __instance.PngData.Length + 107 + __instance.FacePngData.Length;
                var blockHeaderSize = br.ReadInt32();
                var blockHeaderBytes = br.ReadBytes(blockHeaderSize);
                BlockHeader blockHeader = MessagePackSerializer.Deserialize<BlockHeader>(blockHeaderBytes, null, Il2CppSystem.Threading.CancellationToken.None);
                ChaFileLoadFileHook(__instance, blockHeader, br);
#endif

#if KK // Doesn't work in KKS because KK cards go through a different load path
                //Compatibility for ver 1 and 2 ext save data
                if (br.BaseStream.Position != br.BaseStream.Length)
                {
                    long originalPosition = br.BaseStream.Position;

                    try
                    {
                        string marker = br.ReadString();
                        int version = br.ReadInt32();

                        if (marker == "KKEx" && version == 2)
                        {
                            int length = br.ReadInt32();

                            if (length > 0)
                            {
                                if (!LoadEventsEnabled)
                                {
                                    br.BaseStream.Seek(length, SeekOrigin.Current);
                                }
                                else
                                {
                                    byte[] bytes = br.ReadBytes(length);
                                    var dictionary = MessagePackSerializer.Deserialize<Dictionary<string, PluginData>>(bytes);

                                    cardReadEventCalled = true;
                                    internalCharaDictionary.Set(__instance, dictionary);

                                    CardReadEvent(__instance);
                                }
                            }
                        }
                        else
                        {
                            br.BaseStream.Position = originalPosition;
                        }
                    }
                    catch (EndOfStreamException) { } //Incomplete/non-existant data
                    catch (SystemException) { } //Invalid/unexpected deserialized data
                }
#endif

                //If the event wasn't called at this point, it means the card doesn't contain any data, but we still need to call the even for consistency.
                if (cardReadEventCalled == false)
                {
                    internalCharaDictionary.Set(__instance, new Dictionary<string, PluginData>());
                    CardReadEvent(__instance);
                }
            }
            #endregion

            #region Saving

            private static byte[] currentlySavingData = null;

#if KK || KKS
            [HarmonyPrefix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.SaveFile), typeof(BinaryWriter), typeof(bool))]
#elif !RG
            [HarmonyPrefix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.SaveFile), typeof(BinaryWriter), typeof(bool), typeof(int))]
            private static void ChaFileSaveFilePreHook(ChaFile __instance, BinaryWriter bw, bool savePng, int lang) => CardWriteEvent(__instance);
#endif

#if RG
            private static void ChaFileSaveFileHook(ChaFile file, ref BlockHeader header, ref long[] array3)
#else
            private static void ChaFileSaveFileHook(ChaFile file, BlockHeader header, ref long[] array3)
#endif
            {
                Dictionary<string, PluginData> extendedData = GetAllExtendedData(file);
                if (extendedData == null)
                {
                    currentlySavingData = null;
                    return;
                }

                //Remove null entries
                List<string> keysToRemove = new List<string>();
                foreach (var entry in extendedData)
                    if (entry.Value == null)
                        keysToRemove.Add(entry.Key);
                foreach (var key in keysToRemove)
                    extendedData.Remove(key);

                currentlySavingData = MessagePackSerializer.Serialize(extendedData);

                //get offset
                long offset = array3.Sum();
                long length = currentlySavingData.LongLength;

                //insert our custom data length at the end
                Array.Resize(ref array3, array3.Length + 1);
                array3[array3.Length - 1] = length;

                //add info about our data to the block header
                BlockHeader.Info info = new BlockHeader.Info
                {
                    name = Marker,
                    version = DataVersion.ToString(),
                    pos = offset,
                    size = length
                };

                header.lstInfo.Add(info);
            }

#if KK || KKS
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.SaveFile), typeof(BinaryWriter), typeof(bool))]
            private static void ChaFileSaveFilePostHook(bool __result, BinaryWriter bw)
#elif RG
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.SaveFile), typeof(Il2CppSystem.IO.BinaryWriter), typeof(bool), typeof(int))]
            private static void ChaFileSaveFilePostHook(ChaFile __instance, bool __result, Il2CppSystem.IO.BinaryWriter bw)
#else
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.SaveFile), typeof(BinaryWriter), typeof(bool), typeof(int))]
            private static void ChaFileSaveFilePostHook(bool __result, BinaryWriter bw)
#endif
            {
#if RG
                if (!__result)
                    return;

                // set the binary writer to the begging of the block header, we are going to re-write all of
                // the block data since we cant insert the header into the middle of the stream through a transpiler
                bw.OutStream.Position = __instance.PngData.Length + 107 + __instance.FacePngData.Length;

                byte[] customBytes = __instance.GetCustomBytes();
                byte[] coordinateBytes = __instance.GetCoordinateBytes();
                byte[] parameterBytes = __instance.GetParameterBytes();
                byte[] gameInfoBytes = __instance.GetGameInfoBytes();
                byte[] statusBytes = __instance.GetStatusBytes();
                const int blockCount = 5;
                string[] blockNames = new string[]
                {
                    ChaFileCustom.BlockName,
                    ChaFileCoordinate.BlockName,
                    ChaFileParameter.BlockName,
                    ChaFileGameInfo.BlockName,
                    ChaFileStatus.BlockName,
                };
                string[] blockVersions = new string[]
                {
                    ChaFileDefine.ChaFileCustomVersion.ToString(),
                    ChaFileDefine.ChaFileCoordinateVersion.ToString(),
                    ChaFileDefine.ChaFileParameterVersion.ToString(),
                    ChaFileDefine.ChaFileGameInfoVersion.ToString(),
                    ChaFileDefine.ChaFileStatusVersion.ToString(),
                };
                long[] blockSizes = new long[blockCount];
                blockSizes[0] = (long)((customBytes == null) ? 0 : customBytes.Length);
                blockSizes[1] = (long)((coordinateBytes == null) ? 0 : coordinateBytes.Length);
                blockSizes[2] = (long)((parameterBytes == null) ? 0 : parameterBytes.Length);
                blockSizes[3] = (long)((gameInfoBytes == null) ? 0 : gameInfoBytes.Length);
                blockSizes[4] = (long)((statusBytes == null) ? 0 : statusBytes.Length);
                long[] blockPositions = new long[]
                {
                    0,
                    blockSizes[0],
                    blockSizes[0] + blockSizes[1],
                    blockSizes[0] + blockSizes[1] + blockSizes[2],
                    blockSizes[0] + blockSizes[1] + blockSizes[2] + blockSizes[3]
                };
                BlockHeader blockHeader = new BlockHeader();

                for (int block = 0; block < blockCount; block++)
                {
                    BlockHeader.Info item = new BlockHeader.Info
                    {
                        name = blockNames[block],
                        version = blockVersions[block],
                        size = blockSizes[block],
                        pos = blockPositions[block]
                    };

                    blockHeader.lstInfo.Add(item);
                }

                ChaFileSaveFileHook(__instance, ref blockHeader, ref blockSizes);

                var blockData = MessagePackSerializer.Serialize<BlockHeader>(blockHeader, null, Il2CppSystem.Threading.CancellationToken.None);

                bw.Write(blockData.Length);
                bw.Write(blockData);
                long totalBlockSize = 0L;
                foreach (long blockSize in blockSizes)
                {
                    totalBlockSize += blockSize;
                }
                bw.Write(totalBlockSize);
                bw.Write(customBytes);
                bw.Write(coordinateBytes);
                bw.Write(parameterBytes);
                bw.Write(gameInfoBytes);
                bw.Write(statusBytes);

#endif
                if (!__result || currentlySavingData == null)
                    return;

                bw.Write(currentlySavingData);
            }

#if !RG
#if KK || KKS
            [HarmonyTranspiler, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.SaveFile), typeof(BinaryWriter), typeof(bool))]
#else
            [HarmonyTranspiler, HarmonyPatch(typeof(ChaFile), nameof(ChaFile.SaveFile), typeof(BinaryWriter), typeof(bool), typeof(int))]
#endif
            private static IEnumerable<CodeInstruction> ChaFileSaveFileTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                List<CodeInstruction> newInstructionSet = new List<CodeInstruction>(instructions);

#if AI || HS2
                string blockHeader = "AIChara.BlockHeader";
#else
                string blockHeader = "BlockHeader";
#endif

                //get the index of the last blockheader creation
                int blockHeaderIndex = newInstructionSet.FindLastIndex(instruction => CheckNewObjTypeName(instruction, blockHeader));

                //get the index of array3 (which contains data length info)
                int array3Index = newInstructionSet.FindIndex(instruction =>
                {
                    //find first int64 array
                    return instruction.opcode == OpCodes.Newarr &&
                               instruction.operand.ToString() == "System.Int64";
                });

                LocalBuilder blockHeaderLocalBuilder = (LocalBuilder)newInstructionSet[blockHeaderIndex + 1].operand; //get the local index for the block header
                LocalBuilder array3LocalBuilder = (LocalBuilder)newInstructionSet[array3Index + 1].operand; //get the local index for array3

                //insert our own hook right after the blockheader creation
                newInstructionSet.InsertRange(blockHeaderIndex + 2, //we insert AFTER the NEXT instruction, which is the store local for the blockheader
                    new[] {
                        new CodeInstruction(OpCodes.Ldarg_0), //push the ChaFile instance
                        new CodeInstruction(OpCodes.Ldloc_S, blockHeaderLocalBuilder), //push the BlockHeader instance 
                        new CodeInstruction(OpCodes.Ldloca_S, array3LocalBuilder), //push the array3 instance as ref
                        new CodeInstruction(OpCodes.Call, typeof(Hooks).GetMethod(nameof(ChaFileSaveFileHook), AccessTools.all)), //call our hook
                    });

                return newInstructionSet;
            }
#endif
            #endregion

            #endregion

            #region ChaFileCoordinate

            #region Loading

#if !RG
#if KK || KKS
            [HarmonyTranspiler, HarmonyPatch(typeof(ChaFileCoordinate), nameof(ChaFileCoordinate.LoadFile), typeof(Stream))]
#else
            [HarmonyTranspiler, HarmonyPatch(typeof(ChaFileCoordinate), nameof(ChaFileCoordinate.LoadFile), typeof(Stream), typeof(int))]
#endif
            private static IEnumerable<CodeInstruction> ChaFileCoordinateLoadTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                bool set = false;
                List<CodeInstruction> instructionsList = instructions.ToList();
                for (int i = 0; i < instructionsList.Count; i++)
                {
                    CodeInstruction inst = instructionsList[i];
#if HS2 || KKS
                    if (set == false && inst.opcode == OpCodes.Ldc_I4_1 && instructionsList[i + 1].opcode == OpCodes.Stloc_3 && (instructionsList[i + 2].opcode == OpCodes.Leave || instructionsList[i + 2].opcode == OpCodes.Leave_S))
#else
                    if (set == false && inst.opcode == OpCodes.Ldc_I4_1 && instructionsList[i + 1].opcode == OpCodes.Stloc_1 && (instructionsList[i + 2].opcode == OpCodes.Leave || instructionsList[i + 2].opcode == OpCodes.Leave_S))
#endif
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Ldloc_0);
                        yield return new CodeInstruction(OpCodes.Call, typeof(Hooks).GetMethod(nameof(ChaFileCoordinateLoadHook), AccessTools.all));
                        set = true;
                    }

                    yield return inst;
                }
                if (!set) throw new InvalidOperationException("Didn't find any matches");
            }
#endif

#if RG
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileCoordinate), nameof(ChaFileCoordinate.LoadFile), typeof(Il2CppSystem.IO.Stream), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(bool))]
            private static void ChaFileCoordinateLoadPostHook(ChaFileCoordinate __instance, bool __result, Il2CppSystem.IO.Stream st, int lang, bool clothes, bool accessory, bool hair, bool skipPng)
            {
                if (!__result)
                    return;

                if (st == null)
                    return;

                if (!st.CanRead)
                    return;

                Il2CppSystem.IO.BinaryReader binaryReader = new Il2CppSystem.IO.BinaryReader(st);
                try
                {
                    Illusion.IO.PngFile.SkipPng(binaryReader);
                    if (binaryReader.BaseStream.Length - binaryReader.BaseStream.Position == 0L)
                        return;

                    var loadProductNo = binaryReader.ReadInt32();
                    if (loadProductNo > 100)
                        return;

                    var productString = binaryReader.ReadString();
                    if (productString != "【RG_Clothes】")
                        return;

                    var loadVersion = new Il2CppSystem.Version(binaryReader.ReadString());
                    if (loadVersion > ChaFileDefine.ChaFileClothesVersion)
                        return;

                    var language = binaryReader.ReadInt32();
                    var coordinateName = binaryReader.ReadString();
                    int count = binaryReader.ReadInt32();
                    byte[] data = binaryReader.ReadBytes(count);

                    ChaFileCoordinateLoadHook(__instance, binaryReader);
                }
                catch (EndOfStreamException)
                {
                    return;
                }
            }
#endif

#if RG
            private static void ChaFileCoordinateLoadHook(ChaFileCoordinate coordinate, Il2CppSystem.IO.BinaryReader br)
#else
            private static void ChaFileCoordinateLoadHook(ChaFileCoordinate coordinate, BinaryReader br)
#endif
            {
                try
                {
                    string marker = br.ReadString();
                    int version = br.ReadInt32();

                    int length = br.ReadInt32();

                    if (marker == Marker && version == DataVersion && length > 0)
                    {
                        if (!LoadEventsEnabled)
                        {
                            br.BaseStream.Seek(length, Il2CppSystem.IO.SeekOrigin.Current);
                            internalCoordinateDictionary.Set(coordinate, new Dictionary<string, PluginData>());
                        }
                        else
                        {
                            byte[] bytes = br.ReadBytes(length);
#if RG
                            Il2CppSystem.Buffers.ReadOnlySequence<byte> buffer = new Il2CppSystem.Buffers.ReadOnlySequence<byte>((IntPtr)bytes[0]);
                            var dictionary = MessagePackSerializer.Deserialize<Dictionary<string, PluginData>>(ref buffer);
#else
                            var dictionary = MessagePackSerializer.Deserialize<Dictionary<string, PluginData>>(bytes);
#endif
                            internalCoordinateDictionary.Set(coordinate, dictionary);
                        }
                    }
                    else
                        internalCoordinateDictionary.Set(coordinate, new Dictionary<string, PluginData>()); //Overriding with empty data just in case there is some remnant from former loads.

                }
                catch (EndOfStreamException)
                {
                    // Incomplete/non-existant data
                    internalCoordinateDictionary.Set(coordinate, new Dictionary<string, PluginData>());
                }
                catch (InvalidOperationException)
                {
                    // Invalid/unexpected deserialized data
                    internalCoordinateDictionary.Set(coordinate, new Dictionary<string, PluginData>());
                }

                //Firing the event in any case
                CoordinateReadEvent(coordinate);
            }

            #endregion

            #region Saving

#if !RG
#if KK || KKS
            [HarmonyTranspiler, HarmonyPatch(typeof(ChaFileCoordinate), nameof(ChaFileCoordinate.SaveFile), typeof(string))]
#else
            [HarmonyTranspiler, HarmonyPatch(typeof(ChaFileCoordinate), nameof(ChaFileCoordinate.SaveFile), typeof(string), typeof(int))]
#endif
            private static IEnumerable<CodeInstruction> ChaFileCoordinateSaveTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                bool hooked = false;
                List<CodeInstruction> instructionsList = instructions.ToList();
                for (int i = 0; i < instructionsList.Count; i++)
                {
                    CodeInstruction inst = instructionsList[i];
                    yield return inst;

                    //find the end of the using(BinaryWriter) block
                    if (!hooked && inst.opcode == OpCodes.Callvirt && (instructionsList[i + 1].opcode == OpCodes.Leave || instructionsList[i + 1].opcode == OpCodes.Leave_S))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0); //push the ChaFileInstance
                        yield return new CodeInstruction(instructionsList[i - 2]); //push the BinaryWriter (copying the instruction to do so)
                        yield return new CodeInstruction(OpCodes.Call, typeof(Hooks).GetMethod(nameof(ChaFileCoordinateSaveHook), AccessTools.all)); //call our hook
                        hooked = true;
                    }
                }
                if (!hooked) throw new InvalidOperationException("Didn't find any matches");
            }
#endif
#if RG
            [HarmonyPrefix, HarmonyPatch(typeof(ChaFileCoordinate), nameof(ChaFileCoordinate.SaveFile), typeof(string), typeof(int))]
            private static bool ChaFileCoordinateSavePreHook(ChaFileCoordinate __instance, string path, int lang)
            {
                var directoryName = Path.GetDirectoryName(path);
                if (!Directory.Exists(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
                __instance.coordinateFileName = Path.GetFileName(path);

                Il2CppSystem.IO.FileStream fileStream = new Il2CppSystem.IO.FileStream(path, Il2CppSystem.IO.FileMode.Create, Il2CppSystem.IO.FileAccess.Write);
                Il2CppSystem.IO.BinaryWriter binaryWriter = new Il2CppSystem.IO.BinaryWriter(fileStream);

                if (__instance.pngData != null)
                {
                    binaryWriter.Write(__instance.pngData);
                }
                binaryWriter.Write(100);
                binaryWriter.Write("【RG_Clothes】");
                binaryWriter.Write(ChaFileDefine.ChaFileClothesVersion.ToString());
                binaryWriter.Write(lang);
                binaryWriter.Write(__instance.coordinateName);
                byte[] array = __instance.SaveBytes();
                binaryWriter.Write(array.Length);
                binaryWriter.Write(array);

                ChaFileCoordinateSaveHook(__instance, binaryWriter);           

                return false;
            }
#endif
#if RG
            private static void ChaFileCoordinateSaveHook(ChaFileCoordinate file, Il2CppSystem.IO.BinaryWriter bw)
#else
            private static void ChaFileCoordinateSaveHook(ChaFileCoordinate file, BinaryWriter bw)
#endif
            {
                CoordinateWriteEvent(file);

                Logger.Log(BepInEx.Logging.LogLevel.Debug, "Coordinate hook!");

                Dictionary<string, PluginData> extendedData = GetAllExtendedData(file);
                if (extendedData == null)
                    return;

                //Remove null entries
                List<string> keysToRemove = new List<string>();
                foreach (var entry in extendedData)
                    if (entry.Value == null)
                        keysToRemove.Add(entry.Key);
                foreach (var key in keysToRemove)
                    extendedData.Remove(key);

                byte[] data = MessagePackSerializer.Serialize(extendedData);

                bw.Write(Marker);
                bw.Write(DataVersion);
                bw.Write(data.Length);
                bw.Write(data);
            }

            #endregion

            #endregion

            #region Helper

            private static bool CheckCallVirtName(CodeInstruction instruction, string name) => instruction.opcode == OpCodes.Callvirt &&
                       //need to do reflection fuckery here because we can't access MonoMethod which is the operand type, not MehtodInfo like normal reflection
                       instruction.operand.GetType().GetProperty("Name", AccessTools.all).GetGetMethod().Invoke(instruction.operand, null).ToString().ToString() == name;

            private static bool CheckNewObjTypeName(CodeInstruction instruction, string name) => instruction.opcode == OpCodes.Newobj &&
                       //need to do reflection fuckery here because we can't access MonoCMethod which is the operand type, not ConstructorInfo like normal reflection
                       instruction.operand.GetType().GetProperty("DeclaringType", AccessTools.all).GetGetMethod().Invoke(instruction.operand, null).ToString() == name;

            #endregion

            #region Extended Data Override Hooks
#if EC || KK || KKS
            //Prevent loading extended data when loading the list of characters in Chara Maker since it is irrelevant here
            [HarmonyPrefix, HarmonyPatch(typeof(ChaCustom.CustomCharaFile), nameof(ChaCustom.CustomCharaFile.Initialize))]
            private static void CustomScenePreHook() => LoadEventsEnabled = false;
            [HarmonyPostfix, HarmonyPatch(typeof(ChaCustom.CustomCharaFile), nameof(ChaCustom.CustomCharaFile.Initialize))]
            private static void CustomScenePostHook() => LoadEventsEnabled = true;
            //Prevent loading extended data when loading the list of coordinates in Chara Maker since it is irrelevant here
            [HarmonyPrefix, HarmonyPatch(typeof(ChaCustom.CustomCoordinateFile), nameof(ChaCustom.CustomCoordinateFile.Initialize))]
            private static void CustomCoordinatePreHook() => LoadEventsEnabled = false;
            [HarmonyPostfix, HarmonyPatch(typeof(ChaCustom.CustomCoordinateFile), nameof(ChaCustom.CustomCoordinateFile.Initialize))]
            private static void CustomCoordinatePostHook() => LoadEventsEnabled = true;
#else
            [HarmonyPrefix, HarmonyPatch(typeof(CharaCustom.CustomCharaFileInfoAssist), nameof(CharaCustom.CustomCharaFileInfoAssist.AddList))]
            private static void LoadCharacterListPrefix() => LoadEventsEnabled = false;
            [HarmonyPostfix, HarmonyPatch(typeof(CharaCustom.CustomCharaFileInfoAssist), nameof(CharaCustom.CustomCharaFileInfoAssist.AddList))]
            private static void LoadCharacterListPostfix() => LoadEventsEnabled = true;

            [HarmonyPrefix, HarmonyPatch(typeof(CharaCustom.CvsO_CharaLoad), nameof(CharaCustom.CvsO_CharaLoad.UpdateCharasList))]
            private static void CvsO_CharaLoadUpdateCharasListPrefix() => LoadEventsEnabled = false;
            [HarmonyPostfix, HarmonyPatch(typeof(CharaCustom.CvsO_CharaLoad), nameof(CharaCustom.CvsO_CharaLoad.UpdateCharasList))]
            private static void CvsO_CharaLoadUpdateCharasListPostfix() => LoadEventsEnabled = true;

            [HarmonyPrefix, HarmonyPatch(typeof(CharaCustom.CvsO_CharaSave), nameof(CharaCustom.CvsO_CharaSave.UpdateCharasList))]
            private static void CvsO_CharaSaveUpdateCharasListPrefix() => LoadEventsEnabled = false;
            [HarmonyPostfix, HarmonyPatch(typeof(CharaCustom.CvsO_CharaSave), nameof(CharaCustom.CvsO_CharaSave.UpdateCharasList))]
            private static void CvsO_CharaSaveUpdateCharasListPostfix() => LoadEventsEnabled = true;
#endif
            #endregion

#if KK
            private static IEnumerable<CodeInstruction> PartyLiveCharaFixTpl(IEnumerable<CodeInstruction> instructions)
            {
                return new CodeMatcher(instructions).MatchForward(true, new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(ChaFile), nameof(ChaFile.CopyCustom))))
                                                    .ThrowIfInvalid("CopyCustom not found")
                                                    .Advance(1)
                                                    .ThrowIfNotMatch("Ldloc_0 not found", new CodeMatch(OpCodes.Ldloc_0))
                                                    .Advance(1)
                                                    .Insert(new CodeInstruction(OpCodes.Dup),
                                                            new CodeInstruction(OpCodes.Ldarg_1),
                                                            CodeInstruction.Call(typeof(Hooks), nameof(Hooks.PartyLiveCharaFix)))
                                                    .Instructions();
            }
            private static void PartyLiveCharaFix(ChaFile target, SaveData.Heroine source)
            {
                // Copy ext data over to the new chafile
                var data = internalCharaDictionary.Get(source.charFile);
                if (data != null) internalCharaDictionary.Set(target, data);
            }
#endif

#if KK || EC || KKS
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileAccessory), nameof(ChaFileAccessory.MemberInit))]
            private static void MemberInit(ChaFileAccessory __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileAccessory.PartsInfo), nameof(ChaFileAccessory.PartsInfo.MemberInit))]
            private static void MemberInit(ChaFileAccessory.PartsInfo __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);

            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileClothes), nameof(ChaFileClothes.MemberInit))]
            private static void MemberInit(ChaFileClothes __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileClothes.PartsInfo), nameof(ChaFileClothes.PartsInfo.MemberInit))]
            private static void MemberInit(ChaFileClothes.PartsInfo __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileClothes.PartsInfo.ColorInfo), nameof(ChaFileClothes.PartsInfo.ColorInfo.MemberInit))]
            private static void MemberInit(ChaFileClothes.PartsInfo.ColorInfo __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);

            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileStatus), nameof(ChaFileStatus.MemberInit))]
            private static void MemberInit(ChaFileStatus __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileStatus), nameof(ChaFileStatus.Copy))]
            private static void Copy(ChaFileStatus __instance, ChaFileStatus src) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(Traverse.Create(src).Property(ExtendedSaveDataPropertyName).GetValue());

            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileParameter), nameof(ChaFileParameter.MemberInit))]
            private static void MemberInit(ChaFileParameter __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileParameter), nameof(ChaFileParameter.Copy))]
            private static void Copy(ChaFileParameter __instance, ChaFileParameter src) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(Traverse.Create(src).Property(ExtendedSaveDataPropertyName).GetValue());

            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileFace), nameof(ChaFileFace.MemberInit))]
            private static void MemberInit(ChaFileFace __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileFace.PupilInfo), nameof(ChaFileFace.PupilInfo.MemberInit))]
            private static void MemberInit(ChaFileFace.PupilInfo __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileFace.PupilInfo), nameof(ChaFileFace.PupilInfo.Copy))]
            private static void Copy(ChaFileFace.PupilInfo __instance, ChaFileFace.PupilInfo src) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(Traverse.Create(src).Property(ExtendedSaveDataPropertyName).GetValue());
#endif

#if KK || KKS
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileMakeup), nameof(ChaFileMakeup.MemberInit))]
            private static void MemberInit(ChaFileMakeup __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);

            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileParameter.Attribute), nameof(ChaFileStatus.MemberInit))]
            private static void MemberInit(ChaFileParameter.Attribute __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileParameter.Awnser), nameof(ChaFileStatus.MemberInit))]
            private static void MemberInit(ChaFileParameter.Awnser __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileParameter.Denial), nameof(ChaFileStatus.MemberInit))]
            private static void MemberInit(ChaFileParameter.Denial __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);

            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileParameter.Attribute), nameof(ChaFileParameter.Attribute.Copy))]
            private static void Copy(ChaFileParameter.Attribute __instance, ChaFileParameter.Attribute src) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(Traverse.Create(src).Property(ExtendedSaveDataPropertyName).GetValue());
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileParameter.Awnser), nameof(ChaFileParameter.Awnser.Copy))]
            private static void Copy(ChaFileParameter.Awnser __instance, ChaFileParameter.Awnser src) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(Traverse.Create(src).Property(ExtendedSaveDataPropertyName).GetValue());
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileParameter.Denial), nameof(ChaFileParameter.Denial.Copy))]
            private static void Copy(ChaFileParameter.Denial __instance, ChaFileParameter.Denial src) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(Traverse.Create(src).Property(ExtendedSaveDataPropertyName).GetValue());
#endif

#if EC
            [HarmonyPostfix, HarmonyPatch(typeof(ChaFileFace.ChaFileMakeup), nameof(ChaFileFace.ChaFileMakeup.MemberInit))]
            private static void MemberInit(ChaFileFace.ChaFileMakeup __instance) => Traverse.Create(__instance).Property(ExtendedSaveDataPropertyName).SetValue(null);
#endif
        }
    }
}