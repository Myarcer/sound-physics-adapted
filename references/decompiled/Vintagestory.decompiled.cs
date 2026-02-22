using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vintagestory.Client;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: AssemblyTitle("Vintage Story Client")]
[assembly: AssemblyDescription("www.vintagestory.at")]
[assembly: AssemblyCompany("Tyron Madlener (Anego Studios)")]
[assembly: AssemblyProduct("Vintage Story")]
[assembly: AssemblyCopyright("Copyright © 2016-2024 Anego Studios")]
[assembly: ComVisible(false)]
[assembly: Guid("782e0587-e211-4a27-a5ac-3ecb057860dc")]
[assembly: AssemblyFileVersion("1.21.6")]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyVersion("1.0.0.0")]
[module: RefSafetyRules(11)]
namespace Vintagestory;

public class ClientWindows
{
	public static void Main(string[] args)
	{
		ClientProgram.Main(args);
	}
}
