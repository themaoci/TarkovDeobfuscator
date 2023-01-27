using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Reflection;
using de4dot.cui;

namespace TarkovDeobfuscator
{
    public class Deobf
    {
        public static bool DEBUG = false;
        #region Logger - move somewhere else !!!
        public delegate void LogHandler(string text);
        public static event LogHandler OnLog;
        public static List<string> Logged = new List<string>();

        internal static void Log(string text, bool unimportant = false)
        {
            if (!DEBUG && unimportant) return;
            if (OnLog != null)
            {
                OnLog(text);
            }
            else
            {
                Debug.WriteLine(text);
                Console.WriteLine(text);
                Logged.Add(text);
            }
        }
        #endregion



        public static bool DeobfuscateAssembly(string assemblyPath, string managedPath, bool createBackup = true, bool overwriteExisting = false, bool doRemapping = false)
        {
            var de4dotLocation = Path.Combine(Directory.GetCurrentDirectory(), "Deobfuscator", "de4dot.exe");

            // get --strtok Token for string deobfuscation
            GetStringDeobfuscationToken(assemblyPath);
            Application.Stopwatch.Stop();
            // run de4dot internally for assembly deobfuscation
            De4DotExecutor.RunDe4dot(new string[] {
                $"--un-name",
                $"!^<>[a-z0-9]$&!^<>[a-z0-9]__.*$&![A-Z][A-Z]\\$<>.*$&^[a-zA-Z_<{{$][a-zA-Z_0-9<>{{}}$.`-]*$",
                $"{assemblyPath}",
                $"--strtyp",
                $"delegate",
                $"--strtok",
                $"{m_StringDeobfuscationToken}"
            });
            Application.Stopwatch.Start();

            // Fixes "ResolutionScope is null" by rewriting the assembly
#pragma warning disable CS8604 // Possible null reference argument.
            var AssemblyDllPath_Cleaned = Path.Combine(Path.GetDirectoryName(assemblyPath), Path.GetFileNameWithoutExtension(assemblyPath) + "-cleaned.dll");
#pragma warning restore CS8604 // Possible null reference argument.
            if (!File.Exists(AssemblyDllPath_Cleaned))
            {
                Log($"File does not exist in: {AssemblyDllPath_Cleaned}");
                return false;
            }

            // add resolved paths to resolver and pass it into AssemblyResolver
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedPath);

            using (var memoryStream = new MemoryStream(File.ReadAllBytes(AssemblyDllPath_Cleaned)))
            using (var assemblyDefinition = AssemblyDefinition.ReadAssembly(memoryStream, new ReaderParameters(){ AssemblyResolver = resolver }))
            {
                assemblyDefinition.Write(AssemblyDllPath_Cleaned);
            }

            Log($"Elapsed: {Application.Stopwatch.ElapsedMilliseconds}ms (Starting)");
            if (doRemapping)
                RemapKnownClasses(managedPath, AssemblyDllPath_Cleaned);
            Log($"Elapsed: {Application.Stopwatch.ElapsedMilliseconds}ms (RemapKnownClasses)");
            if (createBackup)
                BackupExistingAssembly(assemblyPath);
            Log($"Elapsed: {Application.Stopwatch.ElapsedMilliseconds}ms (BackupExistingAssembly)");
            if (overwriteExisting)
                OverwriteExistingAssembly(assemblyPath, AssemblyDllPath_Cleaned);
            Log($"Elapsed: {Application.Stopwatch.ElapsedMilliseconds}ms (OverwriteExistingAssembly)");

            Log($"DeObfuscation complete!");

            return true;
        }
        #region Deobfuscation functions (move somewhere else...)
        private static void GetStringDeobfuscationToken(string assemblyPath)
        {
            using (var assemblyDefinition = AssemblyDefinition.ReadAssembly(assemblyPath))
            {
                var potentialStringDelegates = new List<MethodDefinition>();

                foreach (var type in assemblyDefinition.MainModule.Types)
                {
                    foreach (var method in type.Methods)
                    {
                        if (method.ReturnType.FullName != "System.String"
                            || method.Parameters.Count != 1
                            || method.Parameters[0].ParameterType.FullName != "System.Int32"
                            || method.Body == null
                            || !method.IsStatic)
                        {
                            continue;
                        }

                        if (!method.Body.Instructions.Any(x =>
                            x.OpCode.Code == Code.Callvirt &&
                            ((MethodReference)x.Operand).FullName == "System.Object System.AppDomain::GetData(System.String)"))
                        {
                            continue;
                        }

                        potentialStringDelegates.Add(method);
                        Log($"String Delegate Candidate: {type.Namespace}.{type.Name}.{method.Name}");
                    }
                }

                var deobfRid = potentialStringDelegates[0].MetadataToken;

                m_StringDeobfuscationToken = $"0x{((uint)deobfRid.TokenType | deobfRid.RID):x4}";

                Log($"Deobfuscation token: {m_StringDeobfuscationToken}");
            }
        }

        #endregion
        private static string m_StringDeobfuscationToken = "";

        private static void OverwriteExistingAssembly(string assemblyPath, string cleanedDllPath, bool deleteCleaned = false)
        {
            // Do final copy to Assembly
            File.Copy(cleanedDllPath, assemblyPath, true);
            // Delete -cleaned
            if (deleteCleaned)
                File.Delete(cleanedDllPath);
        }
        
        private static void RemapKnownClasses(string managedPath, string assemblyPath)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedPath);

            File.Copy(assemblyPath, assemblyPath + ".backup", true);

            var readerParameters = new ReaderParameters { AssemblyResolver = resolver };
            using (var fsAssembly = new FileStream(assemblyPath, FileMode.Open))
            {
                using (var oldAssembly = AssemblyDefinition.ReadAssembly(fsAssembly, readerParameters))
                {
                    if (oldAssembly != null)
                    {
                        var autoRemapperConfig = JsonConvert.DeserializeObject<RemapperConfig>(File.ReadAllText($"{Directory.GetCurrentDirectory()}//Deobfuscator/AutoRemapperConfig.json"));
                        if(autoRemapperConfig == null)
                        {
                            Log($"Unable to find: $\"{{Directory.GetCurrentDirectory()}}//Deobfuscator/AutoRemapperConfig.json\" \n Exiting RemapKnownClasses");
                            return;
                        }
                        Log($"Elapsed: {Application.Stopwatch.ElapsedMilliseconds}ms (start-RemapKnownClasses)");
                        RemapByAutoConfiguration(oldAssembly, autoRemapperConfig);
                        Log($"Elapsed: {Application.Stopwatch.ElapsedMilliseconds}ms (RemapByAutoConfiguration)");
                        RemapByDefinedConfiguration(oldAssembly, autoRemapperConfig);
                        Log($"Elapsed: {Application.Stopwatch.ElapsedMilliseconds}ms (RemapByDefinedConfiguration)");
                        RemapAfterEverything(oldAssembly, autoRemapperConfig);
                        Log($"Elapsed: {Application.Stopwatch.ElapsedMilliseconds}ms (RemapAfterEverything)");
                        oldAssembly.Write(assemblyPath.Replace(".dll", "-remapped.dll"));
                    }
                }
            }
            File.Copy(assemblyPath.Replace(".dll", "-remapped.dll"), assemblyPath, true);
        }
        static Dictionary<string, int> gclassToNameCounts = new Dictionary<string, int>();

        static bool ParameterNameStartsWith(ParameterDefinition p)
        {
            return p.ParameterType.Name.StartsWith("GClass") || p.ParameterType.Name.StartsWith("GStruct") || p.ParameterType.Name.StartsWith("GInterface");
        }
        static bool FieldNameStartsWith(FieldDefinition p)
        {
            return p.FieldType.Name.StartsWith("GClass") || p.FieldType.Name.StartsWith("GStruct") || p.FieldType.Name.StartsWith("GInterface");
        }
        // Replace anti C# naming scheme made by compilers and obfuscators
        static string ReplaceAntiCSNames(string Name)
        {
            return Name.Replace("[]", "").Replace("`1", "").Replace("&", "").Replace(" ", "");
        }
        static bool ShouldSkipThisOne(string Name)
        {
            return Name.StartsWith("GClass", StringComparison.OrdinalIgnoreCase)
            || Name.StartsWith("GStruct", StringComparison.OrdinalIgnoreCase)
            || Name.StartsWith("GInterface", StringComparison.OrdinalIgnoreCase)
            || Name.StartsWith("_")
            || Name.Contains("_")
            || Name.Contains("/");
        }


        static void RemapByAutoConfiguration(AssemblyDefinition oldAssembly, RemapperConfig autoRemapperConfig)
        {
            if (!autoRemapperConfig.EnableAutomaticRemapping)
                return;
            //var gclasses = oldAssembly.MainModule.GetTypes().Where(x =>
            //    x.Name.StartsWith("GClass"));
            var systemAssemblyTypes = Assembly.GetAssembly(typeof(Attribute))?.GetTypes();
            var oldAssemblyTypes = oldAssembly.MainModule.GetTypes();
            if (systemAssemblyTypes == null) systemAssemblyTypes = new Type[] { };
            // Detecting if ParameterType starts with GClass/GStruct/GInterface

            //foreach (var t in oldAssembly.MainModule.GetTypes().Where(x => !x.Name.StartsWith("GClass") && !x.Name.StartsWith("Class")))

           // Log($"t0: {Application.Stopwatch.ElapsedMilliseconds}ms");
            //this sometimes crashes just run it again...
            var sync = new object();
            Parallel.ForEach(oldAssemblyTypes, t =>
            {
                Dictionary<string, int> tempList = new Dictionary<string, int>();
                // Creating Renaming List by the classes being in methods
                t.Methods.Where(x => x.HasParameters && x.Parameters.Any(ParameterNameStartsWith)).ToList()
                .ForEach(m =>
                {
                    // Creating Renaming List by the classes being used as Parameters in methods
                    m.Parameters.Where(ParameterNameStartsWith).ToList()
                    .ForEach(p =>
                    {
                        var n = $"{ReplaceAntiCSNames(p.ParameterType.Name)}.{p.Name}";

                        if (!tempList.ContainsKey(n))
                        {
                            tempList.Add(n, 1);
                        }
                        else 
                        {
                            tempList[n]++;
                        }

                    });
                });

                // Creating Renaming List by the fields in class
                t.Fields.Where(FieldNameStartsWith).ToList()
                .ForEach(prop =>
                {
                    if (!ShouldSkipThisOne(prop.Name))
                    {
                        var n = $"{ReplaceAntiCSNames(prop.FieldType.Name)}.{prop.Name}";

                        if (!tempList.ContainsKey(n))
                        {
                                tempList.Add(n, 1);
                        }
                        else
                        {
                                tempList[n]++;
                        }
                    }
                });
                lock (sync)
                {
                    //foreach (var item in tempList) 
                    //{
                    //    if (gclassToNameCounts.Keys.Contains(item.Key))
                    //    {
                    //        gclassToNameCounts[item.Key] += item.Value;
                    //        continue;
                    //    }
                    //    gclassToNameCounts.Add(item.Key, item.Value);
                    //}
                    gclassToNameCounts = gclassToNameCounts.Concat(tempList.Where(x => {
                        if (gclassToNameCounts.Keys.Contains(x.Key)) 
                        {
                            gclassToNameCounts[x.Key] += x.Value;
                            return false;
                        }
                        return true; 
                    })).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }
            });

            Log($"Classified to rename: {gclassToNameCounts.Count}");
           // Log($"t1: {Application.Stopwatch.ElapsedMilliseconds}ms");

            var autoRemappedClassCount = 0;

            // ----------------------------------------------------------------------------------------
            // Rename classes based on discovery above
            var orderedGClassCounts = gclassToNameCounts.Where(x => x.Value > 0 && !x.Key.Contains("`")).OrderByDescending(x => x.Value);
            var usedNamesCount = new Dictionary<string, int>();
            var renamedClasses = new Dictionary<string, string>();
           // Log($"t2: {Application.Stopwatch.ElapsedMilliseconds}ms");

            //Parallel.ForEach(orderedGClassCounts, g =>
            foreach (var g in orderedGClassCounts)
            {
                var keySplit = g.Key.Split('.');
                var gclassName = keySplit[0];
                var gclassNameNew = keySplit[1];
                if (gclassNameNew.Length <= 3
                    || gclassNameNew.StartsWith("Value", StringComparison.OrdinalIgnoreCase)
                    || gclassNameNew.StartsWith("Attribute", StringComparison.OrdinalIgnoreCase)
                    || gclassNameNew.StartsWith("Instance", StringComparison.OrdinalIgnoreCase)
                    || gclassNameNew.StartsWith("_", StringComparison.OrdinalIgnoreCase)
                    || gclassNameNew.StartsWith("<", StringComparison.OrdinalIgnoreCase)
                    || systemAssemblyTypes.Any(x => x.Name.StartsWith(gclassNameNew, StringComparison.OrdinalIgnoreCase))
                    || oldAssemblyTypes.Any(x => x.Name.Equals(gclassNameNew, StringComparison.OrdinalIgnoreCase))
                    )
                    return;

                var t = oldAssemblyTypes.FirstOrDefault(x => x.Name == gclassName);
                if (t == null)
                    return;

                // Follow standard naming convention, PascalCase all class names
                var newClassName = char.ToUpper(gclassNameNew[0]) + gclassNameNew.Substring(1);

                // Following BSG naming convention, begin Abstract classes names with "Abstract"
                if (t.IsAbstract && !t.IsInterface)
                    newClassName = "Abstract" + newClassName;
                // Follow standard naming convention, Interface names begin with "I"
                else if (t.IsInterface)
                    newClassName = "I" + newClassName;

                if (!usedNamesCount.ContainsKey(newClassName))
                    usedNamesCount.Add(newClassName, 0);

                usedNamesCount[newClassName]++;

                if (usedNamesCount[newClassName] > 1)
                    newClassName += usedNamesCount[newClassName];

                if (!oldAssemblyTypes.Any(x => x.Name == newClassName)
                    && !systemAssemblyTypes.Any(x => x.Name.StartsWith(newClassName, StringComparison.OrdinalIgnoreCase))
                    && !oldAssemblyTypes.Any(x => x.Name.Equals(newClassName, StringComparison.OrdinalIgnoreCase))
                    )
                {
                    var oldClassName = t.Name;
                    t.Name = newClassName;
                    renamedClasses.Add(oldClassName, newClassName);
                    Log($"Remapper: [Auto] {oldClassName} => {newClassName}", true);
                }
            }
          //  Log($"t3: {Application.Stopwatch.ElapsedMilliseconds}ms");

            // end of renaming based on discovery
            // ---------------------------------------------------------------------------------------

            // ------------------------------------------------
            // Auto rename FirearmController sub classes
            foreach (var t in oldAssemblyTypes.Where(x => x.FullName.StartsWith("EFT.Player.FirearmController") && x.Name.StartsWith("GClass")))
            {
                t.Name.Replace("GClass", "FirearmController");
            }
           // Log($"t4: {Application.Stopwatch.ElapsedMilliseconds}ms");

            // ------------------------------------------------
            // Auto rename descriptors
            Parallel.ForEach(oldAssemblyTypes, t =>
            {
                foreach (var m in t.Methods.Where(x => x.Name.StartsWith("ReadEFT")))
                {
                    if (m.ReturnType.Name.StartsWith("GClass"))
                    {
                        var rT = oldAssemblyTypes.FirstOrDefault(x => x == m.ReturnType);
                        if (rT != null)
                        {
                            var oldTypeName = rT.Name;
                            rT.Name = m.Name.Replace("ReadEFT", "");
                            Log($"Remapper: [Auto] {oldTypeName} => {rT.Name}", true);

                        }
                    }
                }
            });
           // Log($"t5: {Application.Stopwatch.ElapsedMilliseconds}ms");
            //    foreach (var t in oldAssemblyTypes)
            //{
            //    foreach (var m in t.Methods.Where(x => x.Name.StartsWith("ReadEFT")))
            //    {
            //        if (m.ReturnType.Name.StartsWith("GClass"))
            //        {
            //            var rT = oldAssemblyTypes.FirstOrDefault(x => x == m.ReturnType);
            //            if (rT != null)
            //            {
            //                var oldTypeName = rT.Name;
            //                rT.Name = m.Name.Replace("ReadEFT", "");
            //                Log($"Remapper: [Auto] {oldTypeName} => {rT.Name}", true);

            //            }
            //        }
            //    }
            //}

            // Testing stuff here.
            // Quick hack to name properties properly in EFT.Player
            foreach (var playerProp in oldAssemblyTypes.FirstOrDefault(x => x.FullName == "EFT.Player").Properties)
            {
                if (playerProp.Name.StartsWith("GClass", StringComparison.OrdinalIgnoreCase))
                {
                    playerProp.Name = playerProp.PropertyType.Name.Replace("Abstract", "");
                }
            }

          //  Log($"t6: {Application.Stopwatch.ElapsedMilliseconds}ms");
            Log($"Remapper: Ensuring EFT classes are public");
            foreach (var t in oldAssemblyTypes)
            {
                if (t.IsClass && t.IsDefinition && t.BaseType != null && t.BaseType.FullName != "System.Object")
                {
                    if (!systemAssemblyTypes.Any(x => x.Name.StartsWith(t.Name, StringComparison.OrdinalIgnoreCase)))
                        t.IsPublic = true;
                }
            }
          //  Log($"t7: {Application.Stopwatch.ElapsedMilliseconds}ms");
            //    foreach (var t in oldAssemblyTypes)
            //{
            //    if (t.IsClass && t.IsDefinition && t.BaseType != null && t.BaseType.FullName != "System.Object")
            //    {
            //        if (!systemAssemblyTypes
            //            .Any(x => x.Name.StartsWith(t.Name, StringComparison.OrdinalIgnoreCase)))
            //            t.IsPublic = true;
            //    }
            //}

            Log($"Remapper: Setting EFT methods to public");
            foreach (var ctf in autoRemapperConfig.TypesToForceAllPublicMethods)
            {
                foreach (var t in oldAssemblyTypes.Where(x => x.Namespace.Contains("EFT", StringComparison.OrdinalIgnoreCase) && x.Name.Contains(ctf, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var m in t.Methods)
                    {
                        if (!m.IsPublic)
                            m.IsPublic = true;
                    }
                }
            }
          //  Log($"t8: {Application.Stopwatch.ElapsedMilliseconds}ms");

            Log($"Remapper: Setting EFT fields/properties to public");
            Parallel.ForEach(autoRemapperConfig.TypesToForceAllPublicFieldsAndProperties, ctf => {
            //foreach (var ctf in autoRemapperConfig.TypesToForceAllPublicFieldsAndProperties)
            //{
                foreach (var t in oldAssemblyTypes.Where(x => x.Namespace.Contains("EFT", StringComparison.OrdinalIgnoreCase) && x.Name.Contains(ctf, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var m in t.Fields)
                    {
                        if (!m.IsPublic)
                            m.IsPublic = true;
                    }
                }
            });
           // Log($"t9: {Application.Stopwatch.ElapsedMilliseconds}ms");

            Log($"Remapper: Setting All Types to public");
            if (autoRemapperConfig.ForceAllToPublic)
            {
               Parallel.ForEach(oldAssemblyTypes, t => {
                    t.IsPublic = true;

                    foreach (var m in t.Fields)
                    {
                        if (!m.IsPublic)
                            m.IsPublic = true;
                    }
                    foreach (var m in t.Methods)
                    {
                        if (!m.IsPublic)
                            m.IsPublic = true;
                    }
                    foreach (var m in t.NestedTypes)
                    {
                        if (!m.IsPublic)
                            m.IsPublic = true;
                    }
                    foreach (var m in t.Events)
                    {
                        if (!m.DeclaringType.IsPublic)
                            m.DeclaringType.IsPublic = true;
                    }
                    foreach (var m in t.Properties)
                    {
                        if (!m.DeclaringType.IsPublic)
                            m.DeclaringType.IsPublic = true;
                    }
                });
               // Log($"t10: {Application.Stopwatch.ElapsedMilliseconds}ms");
                //foreach (var t in oldAssemblyTypes)
                //{
                //    t.IsPublic = true;

                //    foreach (var m in t.Fields)
                //    {
                //        if (!m.IsPublic)
                //            m.IsPublic = true;
                //    }
                //    foreach (var m in t.Methods)
                //    {
                //        if (!m.IsPublic)
                //            m.IsPublic = true;
                //    }
                //    foreach (var m in t.NestedTypes)
                //    {
                //        if (!m.IsPublic)
                //            m.IsPublic = true;
                //    }
                //    foreach (var m in t.Events)
                //    {
                //        if (!m.DeclaringType.IsPublic)
                //            m.DeclaringType.IsPublic = true;
                //    }
                //    foreach (var m in t.Properties)
                //    {
                //        if (!m.DeclaringType.IsPublic)
                //            m.DeclaringType.IsPublic = true;
                //    }
                //}

            }


            autoRemappedClassCount = renamedClasses.Count;
            Log($"Remapper: Auto remapped {autoRemappedClassCount} classes");
        }

        private static void RemapByDefinedConfiguration(AssemblyDefinition oldAssembly, RemapperConfig autoRemapperConfig)
        {
            if (!autoRemapperConfig.EnableDefinedRemapping)
                return;

            int countOfDefinedMappingSucceeded = 0;
            int countOfDefinedMappingFailed = 0;

            foreach (var config in autoRemapperConfig.DefinedRemapping.Where(x => !string.IsNullOrEmpty(x.RenameClassNameTo)))
            {

                try
                {
                    List<TypeDefinition> typeDefinitions = new();
                    var findTypes
                        = oldAssembly.MainModule.GetTypes().ToList();
                    // Filter Types by Class Name Matching
                    findTypes = findTypes.Where(
                        x =>
                            (
                                config.ClassName == null || config.ClassName.Length == 0 || (x.FullName.Contains(config.ClassName))
                            )
                        ).ToList();
                    // Filter Types by Methods
                    findTypes = findTypes.Where(x
                            =>
                                (config.HasMethods == null || config.HasMethods.Length == 0
                                    || (x.Methods.Select(y => y.Name.Split('.')[y.Name.Split('.').Length - 1]).Count(y => config.HasMethods.Contains(y)) >= config.HasMethods.Length))

                            ).ToList();

                    // Filter Types by Virtual Methods
                    if (config.HasMethodsVirtual != null && config.HasMethodsVirtual.Length > 0)
                    {
                        findTypes = findTypes.Where(x
                               =>
                                 (x.Methods.Count(y => y.IsVirtual) > 0
                                    && x.Methods.Where(y => y.IsVirtual).Count(y => config.HasMethodsVirtual.Contains(y.Name)) >= config.HasMethodsVirtual.Length
                                    )
                               ).ToList();
                    }

                    // Filter Types by Events
                    findTypes = findTypes.Where(x
                           =>
                               (config.HasEvents == null || config.HasEvents.Length == 0
                                   || (x.Events.Select(y => y.Name.Split('.')[y.Name.Split('.').Length - 1]).Count(y => config.HasEvents.Contains(y)) >= config.HasEvents.Length))

                           ).ToList();

                    // Filter Types by Field/Properties
                    findTypes = findTypes.Where(
                        x =>
                                (
                                    // fields
                                    (
                                    config.HasFields == null || config.HasFields.Length == 0
                                    || (!config.HasExactFields && x.Fields.Count(y => config.HasFields.Contains(y.Name)) >= config.HasFields.Length)
                                    || (config.HasExactFields && x.Fields.Count(y => y.IsDefinition && config.HasFields.Contains(y.Name)) == config.HasFields.Length)
                                    )
                                    ||
                                    // properties
                                    (
                                    config.HasFields == null || config.HasFields.Length == 0
                                    || (!config.HasExactFields && x.Properties.Count(y => config.HasFields.Contains(y.Name)) >= config.HasFields.Length)
                                    || (config.HasExactFields && x.Properties.Count(y => y.IsDefinition && config.HasFields.Contains(y.Name)) == config.HasFields.Length)

                                    )
                                )).ToList();

                    // Filter Types by Class/Interface
                    findTypes = findTypes.Where(
                        x =>
                            (
                                (!config.IsClass.HasValue     || (config.IsClass.HasValue     && config.IsClass.Value     && (x.IsClass && !x.IsEnum && !x.IsInterface)))
                                && 
                                (!config.IsInterface.HasValue || (config.IsInterface.HasValue && config.IsInterface.Value && (x.IsInterface && !x.IsEnum && !x.IsClass)))
                            )
                        ).ToList();

                    // Filter by Interfaces
                    findTypes = findTypes.Where(x
                        =>
                            (config.HasInterfaces == null || config.HasInterfaces.Length == 0
                                || (x.Interfaces.Select(y => y.InterfaceType.Name.Split('.')[y.InterfaceType.Name.Split('.').Length - 1]).Count(y => config.HasInterfaces.Contains(y)) >= config.HasInterfaces.Length))

                        ).ToList();

                    // Filter by Nested Types
                    findTypes = findTypes.Where(x
                        =>
                            (config.HasNestedTypes == null || config.HasNestedTypes.Length == 0
                                || (x.NestedTypes.Select(y => y.Name.Split('.')[y.Name.Split('.').Length - 1]).Count(y => config.HasNestedTypes.Contains(y)) >= config.HasNestedTypes.Length))

                        ).ToList();

                    // Filter by Properties
                    findTypes = findTypes.Where(x
                        =>
                            (config.HasProperties == null || config.HasProperties.Length == 0
                                || (x.Properties.Select(y => y.Name.Split('.')[y.Name.Split('.').Length - 1]).Count(y => config.HasProperties.Contains(y)) >= config.HasProperties.Length))

                        ).ToList();

                    // Filter with ExactProperties
                    if (config.ExactProperties != null && config.ExactProperties.Length != 0)
                    {
                        foreach (var t in findTypes)
                        {
                            if (t.Properties.Count == config.ExactProperties.Length)
                            {
                                int okField = 0;
                                for (int i = 0; i < config.ExactProperties.Length; i++)
                                {
                                    if (t.Properties[i].Name == config.ExactProperties[i])
                                    {
                                        WriteLog(i + " " + t.Name + " " + t.Properties[i].Name + " == " + config.ExactProperties[i].ToString());
                                        okField++;
                                    }
                                }
                                if (okField == config.ExactProperties.Length)
                                {
                                    WriteLog(t.Name + " is found!");
                                    typeDefinitions.Add(t);
                                }
                            }
                        }
                    }

                    // Filter with ExactFields
                    if (config.ExactFields != null && config.ExactFields.Length != 0)
                    {
                        foreach (var t in findTypes)
                        {
                            if (t.Fields.Count == config.ExactFields.Length)
                            {
                                int okField = 0;
                                for (int i = 0; i < config.ExactFields.Length; i++)
                                {
                                    if (t.Fields[i].Name == config.ExactFields[i])
                                    {
                                        WriteLog(i + " " + t.Name + " " + t.Fields[i].Name + " == " + config.ExactFields[i].ToString());
                                        okField++;
                                    }
                                }
                                if (okField == config.ExactFields.Length)
                                {
                                    WriteLog(t.Name + " is found!");
                                    typeDefinitions.Add(t);
                                }
                            }
                        }
                    }

                    // Filter with ExactMethods
                    if (config.ExactMethods != null && config.ExactMethods.Length != 0)
                    {
                        foreach (var t in findTypes)
                        {
                            if (t.Methods.Count == config.ExactMethods.Length)
                            {
                                int okField = 0;
                                for (int i = 0; i < config.ExactMethods.Length; i++)
                                {
                                    if (t.Methods[i].Name == config.ExactMethods[i])
                                    {
                                        WriteLog(i + " " + t.Name + " " + t.Methods[i].Name + " == " + config.ExactMethods[i].ToString());
                                        okField++;
                                    }
                                }
                                if (okField == config.ExactMethods.Length)
                                {
                                    WriteLog(t.Name + " is found!");
                                    typeDefinitions.Add(t);
                                }
                            }
                        }
                    }

                    // Filter with ExactEvents
                    if (config.ExactEvents != null && config.ExactEvents.Length != 0)
                    {
                        foreach (var t in findTypes)
                        {
                            if (t.Events.Count == config.ExactEvents.Length)
                            {
                                int okField = 0;
                                for (int i = 0; i < config.ExactEvents.Length; i++)
                                {
                                    if (t.Events[i].Name == config.ExactEvents[i])
                                    {
                                        WriteLog(i + " " + t.Name + " " + t.Events[i].Name + " == " + config.ExactEvents[i].ToString());
                                        okField++;
                                    }
                                }
                                if (okField == config.ExactEvents.Length)
                                {
                                    WriteLog(t.Name + " is found!");
                                    typeDefinitions.Add(t);
                                }
                            }
                        }
                    }

                    // Filter with ExactInterfaces
                    if (config.ExactInterfaces != null && config.ExactInterfaces.Length != 0)
                    {
                        foreach (var t in findTypes)
                        {
                            if (t.Interfaces.Count == config.ExactInterfaces.Length)
                            {
                                int okField = 0;
                                for (int i = 0; i < config.ExactInterfaces.Length; i++)
                                {
                                    if (t.Interfaces[i].InterfaceType.Name == config.ExactInterfaces[i])
                                    {
                                        WriteLog(i + " " + t.Name + " " + t.Interfaces[i].InterfaceType.Name + " == " + config.ExactInterfaces[i].ToString());
                                        okField++;
                                    }
                                }
                                if (okField == config.ExactInterfaces.Length)
                                {
                                    WriteLog(t.Name + " is found!");
                                    typeDefinitions.Add(t);
                                }
                            }
                        }
                    }

                    // Filter with ExactNestedTypes
                    if (config.ExactNestedTypes != null && config.ExactNestedTypes.Length != 0)
                    {
                        foreach (var t in findTypes)
                        {
                            if (t.Interfaces.Count == config.ExactNestedTypes.Length)
                            {
                                int okField = 0;
                                for (int i = 0; i < config.ExactNestedTypes.Length; i++)
                                {
                                    if (t.NestedTypes[i].Name == config.ExactNestedTypes[i])
                                    {
                                        WriteLog(i + " " + t.Name + " " + t.NestedTypes[i].Name + " == " + config.ExactNestedTypes[i].ToString());
                                        okField++;
                                    }
                                }
                                if (okField == config.ExactNestedTypes.Length)
                                {
                                    WriteLog(t.Name + " is found!");
                                    typeDefinitions.Add(t);
                                }
                            }
                        }
                    }

                    if (typeDefinitions.Count() > 0)
                    { findTypes = typeDefinitions; }
                    
                    if (findTypes.Any())
                    {
                        if (findTypes.Count() > 1)
                        {
                            findTypes = findTypes
                                .OrderBy(x => !x.Name.StartsWith("GClass") && !x.Name.StartsWith("GInterface"))
                                .ThenBy(x => x.Name.StartsWith("GInterface"))
                                .ToList();

                            var numberOfChangedIndexes = 0;
                            for (var index = 0; index < findTypes.Count(); index++)
                            {
                                var newClassName = config.RenameClassNameTo;
                                var t = findTypes[index];
                                var oldClassName = t.Name;
                                if (t.IsInterface && !newClassName.StartsWith("I"))
                                {
                                    newClassName = newClassName.Insert(0, "I");
                                }

                                newClassName = newClassName + (!t.IsInterface && numberOfChangedIndexes > 0 ? numberOfChangedIndexes.ToString() : "");

                                

                                t.Name = newClassName;
                                if (!t.IsInterface)
                                    numberOfChangedIndexes++;

                                Log($"Remapper: {oldClassName} => {newClassName}", true);
                                countOfDefinedMappingSucceeded++;

                            }
                        }
                        else
                        {
                            var newClassName = config.RenameClassNameTo;
                            var t = findTypes.SingleOrDefault();
                            var oldClassName = t.Name;
                            if (t.IsInterface && !newClassName.StartsWith("I"))
                                newClassName = newClassName.Insert(0, "I");

                            t.Name = newClassName;

                            Log($"Remapper: {oldClassName} => {newClassName}", true);
                            countOfDefinedMappingSucceeded++;
                        }
                    }
                    else
                    {
                        Log($"Remapper: Failed to remap {config.RenameClassNameTo}");
                        countOfDefinedMappingFailed++;

                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }

            Log($"Defined Remapper: SUCCESS: {countOfDefinedMappingSucceeded}");
            Log($"Defined Remapper: FAILED: {countOfDefinedMappingFailed}");
        }

        static void RemapAfterEverything(AssemblyDefinition oldAssembly, RemapperConfig autoRemapperConfig)
        {
            Log($"Remapper: Setting Types to public");
            foreach (var ctf in autoRemapperConfig.ForceTypeToPublic)
            {
                var foundTypes = oldAssembly.MainModule.GetTypes()
                    .Where(x => x.Name.Contains(ctf, StringComparison.OrdinalIgnoreCase));
                foreach (var t in foundTypes)
                {
                    Log(t.FullName + " is now Public", true);
                    if (!t.IsPublic)
                        t.IsPublic = true;
                }
            }

            if (autoRemapperConfig.RenameEmptyToACS)
            {
                Log($"Remapper: Setting No Namespace to ACS.");
                var emptynamespace = oldAssembly.MainModule.GetTypes()
                       .Where(x => !x.FullName.Contains("."));
                foreach (var t in emptynamespace)
                {
                    if (t.FullName.Contains("<Module>"))
                        continue;
                    t.Namespace = "ACS";
                    foreach (var tn in t.NestedTypes)
                    {
                        tn.Namespace = "ACS";
                    }

                }
            }

            
        }

        public static string[] SplitCamelCase(string input)
        {
            return System.Text.RegularExpressions.Regex
                .Replace(input, "(?<=[a-z])([A-Z])", ",", System.Text.RegularExpressions.RegexOptions.Compiled)
                .Trim().Split(',');
        }
        private static void BackupExistingAssembly(string assemblyPath)
        {
            if (!File.Exists(assemblyPath + ".backup"))
                File.Copy(assemblyPath, assemblyPath + ".backup", false);
        }
        public static void WriteLog(string strLog)
        {
            FileInfo logFileInfo = new FileInfo("log.txt");
            DirectoryInfo logDirInfo = new DirectoryInfo(logFileInfo.DirectoryName);
            if (!logDirInfo.Exists) logDirInfo.Create();
            using FileStream fileStream = new FileStream("log.txt", FileMode.Append);
            using StreamWriter log = new StreamWriter(fileStream);
            log.WriteLine(DateTime.Now + " | " + strLog);
        }
    }
}
