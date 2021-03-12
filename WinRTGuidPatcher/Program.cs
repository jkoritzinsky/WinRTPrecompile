using System;
using System.IO;

namespace WinRTGuidPatcher
{
    class Program
    {
        static void Main(string[] args)
        {
            var guidPatcher = new GuidPatcher("TestData/Windows.dll");
            int numPatches = guidPatcher.ProcessAssembly();
            Directory.CreateDirectory("Output");
            guidPatcher.SaveAssembly("Output");

            Console.WriteLine($"{numPatches} IID calculations/fetches patched");
        }
    }
}
