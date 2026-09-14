using System.Runtime.CompilerServices;

// Lets other mods declare [KSPAssemblyDependency("KSPTethers", 1, 0)].
[assembly: KSPAssembly("KSPTethers", 1, 0, 0)]

// The offline rope-solver tests compile against the same sources.
[assembly: InternalsVisibleTo("RopeSimTests")]
