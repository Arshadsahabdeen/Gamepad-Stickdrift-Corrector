using StickFix.Cli;
using StickFix.Windows;

const string Usage = """
    StickFix prototype

      stickfix devices                 List connected XInput pads
      stickfix selftest                Prove the virtual pad works (no physical pad needed; close games first)
      stickfix raw [--slot N]          Show live physical values (changes nothing)
      stickfix rest [--slot N]         Measure and classify drift
      stickfix calibrate [--slot N] [--strength minimal|balanced|strong]
                                       Measure and save a profile (asks first)
      stickfix run [--slot N] [--raw] [--profile PATH]
                                       Forward physical to virtual. --raw skips correction
    """;

try
{
    var options = new Options(args.Skip(1).ToArray());
    return args.FirstOrDefault() switch
    {
        "devices" => Commands.Devices(),
        "selftest" => Commands.SelfTest(),
        "raw" => Commands.Raw(options),
        "rest" => Commands.Rest(options),
        "calibrate" => Commands.Calibrate(options),
        "run" => Commands.Run(options),
        _ => Fail(Usage),
    };
}
catch (Exception e) when (e is UsageException or VirtualPadException or StickFix.Core.ProfileException)
{
    return Fail(e.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
