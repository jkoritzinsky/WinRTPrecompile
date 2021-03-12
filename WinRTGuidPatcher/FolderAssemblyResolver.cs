using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinRTGuidPatcher
{
    class FolderAssemblyResolver : DefaultAssemblyResolver
    {
        public FolderAssemblyResolver(DirectoryInfo directory)
        {
            foreach (var file in directory.EnumerateFiles("*.dll"))
            {
                try
                {
                    var definition = AssemblyDefinition.ReadAssembly(file.FullName, new ReaderParameters(ReadingMode.Deferred)
                    {
                        InMemory = true,
                        ReadWrite = false,
                        AssemblyResolver = this
                    });
                    RegisterAssembly(definition);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
