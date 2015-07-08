using System.Collections.Generic;

namespace OmniSharp.Cake
{
    public class CakeContext
    {
        public HashSet<string> CakeFiles { get; } = new HashSet<string>();
        public HashSet<string> References { get; } = new HashSet<string>();
        public HashSet<string> Usings { get; } = new HashSet<string>();

        public string Path { get; set; }
    }
}
