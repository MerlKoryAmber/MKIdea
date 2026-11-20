using System.Runtime.CompilerServices;

// Внутреннее ядра (PlanRunner.ToHistory, Env и т. п.) — для обоих приложений.
[assembly: InternalsVisibleTo("LinuxOps")]
[assembly: InternalsVisibleTo("LinuxOps.Web")]
