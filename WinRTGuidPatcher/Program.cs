﻿using Mono.Cecil;
using System;
using System.IO;

namespace WinRTGuidPatcher
{
    class Program
    {
        static void Main(string[] args)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory("TestData");
            resolver.AddSearchDirectory("C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/5.0.0/ref/net5.0");
            var guidPatcher = new GuidPatcher(
                "TestData/Windows.dll",
                resolver);
            int numPatches = guidPatcher.ProcessAssembly();
            Directory.CreateDirectory("Output");
            guidPatcher.SaveAssembly("Output");

            Console.WriteLine($"{numPatches} IID calculations/fetches patched");
        }
    }
}
