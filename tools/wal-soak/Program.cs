using System.Runtime.InteropServices;

namespace ZoneTree.WalSoak;

static class Program
{
  public static async Task<int> Main(string[] args)
  {
    if (args.Length == 0 || args.Contains("--help")) { Console.WriteLine(Options.Help); return 0; }
    var child = args[0].EndsWith("-child", StringComparison.Ordinal);
    try
    {
      if (child)
      {
        var config = Json.Read<CaseConfig>(args[1]);
        switch (args[0])
        {
          case "worker-child": await Worker.Run(config); break;
          case "check-child": await Checks.Run(config); break;
          case "verify-child": await Checks.Verify(config, args[2], args[3] == "resume"); break;
          default: throw new ArgumentException("Unknown child command.");
        }
        return 0;
      }
      using var stop = new CancellationTokenSource();
      Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
      using var term = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }) : null;
      if (args[0] == "verify")
      {
        if (args.Length != 3 || args[1] != "--case") throw new ArgumentException("Use verify --case PATH.");
        return await Supervisor.VerifyExisting(Path.GetFullPath(args[2]), stop.Token);
      }
      return await Supervisor.Run(Options.Parse(args), stop.Token);
    }
    catch (Exception error)
    {
      if (child) new Wire().Send(new("fatal", Text: error.ToString()));
      else Console.Error.WriteLine(error);
      return error is ArgumentException ? 2 : 1;
    }
  }
}
