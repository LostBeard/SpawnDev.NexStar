// SpawnDev.NexStar desktop test runner: SpawnDev.UnitTesting's ConsoleRunner enumerates and runs the
// [TestMethod]s of SpawnDev.NexStar.Tests, following PlaywrightMultiTest's console contract
// (no args = list the tests, one arg = run that test).
using Microsoft.Extensions.DependencyInjection;
using SpawnDev.NexStar.Tests;
using SpawnDev.UnitTesting;

var services = new ServiceCollection();
services.AddSingleton<ProtocolFramingTests>();
services.AddSingleton<ProtocolCodecTests>();
services.AddSingleton<ClockTests>();
services.AddSingleton<CommandChannelTests>();
services.AddSingleton<ServiceTests>();
services.AddSingleton<HardwareReadOnlyTests>();
var sp = services.BuildServiceProvider();
var runner = new UnitTestRunner(sp, true);
await ConsoleRunner.Run(args, runner);
