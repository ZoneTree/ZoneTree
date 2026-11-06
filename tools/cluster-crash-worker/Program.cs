using System.Globalization;
using ZoneTree.ClusterCrashWorker;
using ZoneTree.Options;

// run <data directory> <write-ahead log mode> <process number> <acknowledgement directory>
if (args.Length != 5 || args[0] != "run")
{
  Console.Error.WriteLine("Usage: run <data directory> <write-ahead log mode> <process number> <acknowledgement directory>");
  return 64;
}
return CrashWorkload.Run(args[1], Enum.Parse<WriteAheadLogMode>(args[2]),
    int.Parse(args[3], CultureInfo.InvariantCulture), args[4]);
