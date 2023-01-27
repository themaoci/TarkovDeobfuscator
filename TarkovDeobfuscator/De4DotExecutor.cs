using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovDeobfuscator
{
    internal class De4DotExecutor
    {
        internal static void RunDe4dot(string[] args) 
        {
            de4dot.cui.Program.Main(args);
        }
    }
}
