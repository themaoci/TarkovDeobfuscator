using System.Diagnostics;

namespace TarkovDeobfuscator
{
    internal class Application
    {
        static string m_Action = "none";
        static string m_GameManagedPath = "Managed";
        internal static Stopwatch Stopwatch;
        static void Main(string[] args)
        {
            Stopwatch = new Stopwatch();
            Stopwatch.Start();
            if (args.Length >= 1) 
            {
                m_Action = args[0];
                if (args.Length >= 2) 
                {
                    m_GameManagedPath = args[1];
                    if (!Directory.Exists(m_GameManagedPath)) 
                    {
                        m_GameManagedPath = "Managed";
                    }
                }
            }

            switch (m_Action) 
            {
                case "-remap":
                    Deobf.DeobfuscateAssembly("Assembly-CSharp.dll", m_GameManagedPath, true, false, true);

                    break;
                case "-override":
                    Deobf.DeobfuscateAssembly("Assembly-CSharp.dll", m_GameManagedPath, true, true, false);

                    break;
                case "-both":
                    Deobf.DeobfuscateAssembly("Assembly-CSharp.dll", m_GameManagedPath, true, true, true);

                    break;
                case "-help":
                    Console.WriteLine($"Argument 1:\n" +
                        $"-remap  ->> remaps assembly from game based on given config file 'remap config'" +
                        $"-override  ->> 'propably overrides already remapped assembly or what not'" +
                        $"-both  ->> does both above actions" +
                        $"-none  ->> or without any arguments simply clears assembly");
                    break;
                case "-none":
                default:
                    Deobf.DeobfuscateAssembly("Assembly-CSharp.dll", m_GameManagedPath);
                    break;
            }
            Stopwatch.Stop();
            Console.WriteLine($"ExecutionTime: {Stopwatch.ElapsedMilliseconds}ms");
        }
    }
}