using System;
using System.Diagnostics;
using System.Runtime.Loader;
using System.IO;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;

namespace Swashbuckle.AspNetCore.Cli
{
    class Program
    {
        static int Main(string[] args)
        {
            // NOTE: A few things ...

            // 1) The "dotnet swagger tofile" command does not serve the request directly. Instead, it invokes a corresponding
            // command (called _tofile) via "dotnet exec" so that the runtime configuration (*.runtimeconfig & *.deps.json) of the
            // provided startupassembly can be used instead of the tool's. This is neccessary to successfully load the
            // startupassembly and it's transitive dependencies. See https://github.com/dotnet/coreclr/issues/13277 for more.

            // 2) When the request is forwarded to the internal "_tofile" command, the TestHost library needs to be loaded and
            // consumed dynamically. This is because the runtime configuration of the startupassembly is being used instead of
            // the tool's (as described above) and so it's own transitive dependencies can't be resolved automatically.

            var runner = new CommandRunner("dotnet swagger", "Swashbuckle (Swagger) Command Line Tools", Console.Out);

            // > dotnet swagger tofile
            runner.SubCommand("tofile", "retrieves Swagger from a startup assembly, and writes to file ", c =>
            {
                c.Argument("startupassembly", "relative path to the application's startup assembly");
                c.Argument("swaggeruri", "relative uri where the application exposes it's Swagger JSON");
                c.Argument("output", "relative path where the Swagger will be output");
                c.Option("--baseaddress", "base uri to pass to the Swagger provider");
                c.OnRun((namedArgs) =>
                {
                    var depsFile = namedArgs["startupassembly"].Replace(".dll", ".deps.json");
                    var runtimeConfig = namedArgs["startupassembly"].Replace(".dll", ".runtimeconfig.json");

                    var subProcess = Process.Start("dotnet", string.Format(
                        "exec --depsfile {0} --runtimeconfig {1} {2} _{3}", // note the underscore
                        depsFile,
                        runtimeConfig,
                        typeof(Program).Assembly.Location,
                        string.Join(' ', args)
                    ));

                    subProcess.WaitForExit();
                    return subProcess.ExitCode;
                });
            });

            // > dotnet swagger _tofile (* should only be invoked via "dotnet exec")
            runner.SubCommand("_tofile", "retrieves Swagger from a startup assembly, and writes to file ", c =>
            {
                c.Argument("startupassembly", "relative path to the application's startup assembly");
                c.Argument("swaggeruri", "relative uri where the application exposes it's Swagger JSON");
                c.Argument("output", "relative path where the Swagger will be output");
                c.Option("--baseaddress", "base uri to pass to the Swagger provider");
                c.OnRun((namedArgs) =>
                {
                    // 1) Configure host with provided startupassembly
                    var startupAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
                        $"{Directory.GetCurrentDirectory()}\\{namedArgs["startupassembly"]}");
                    var hostBuilder = new WebHostBuilder()
                        .UseStartup(startupAssembly.FullName);

                    // 2) Load and instantiate an in-memory server
                    var testServerAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
                        $"{Path.GetDirectoryName(typeof(Program).Assembly.Location)}\\Microsoft.AspNetCore.TestHost.dll");
                    var testServerType = testServerAssembly.GetType("Microsoft.AspNetCore.TestHost.TestServer");
                    var testServer = Activator.CreateInstance(testServerType, hostBuilder);

                    // 3) Request Swagger from in-memory server
                    var client = (HttpClient)testServerType.GetMethod("CreateClient").Invoke(testServer, null);
                    client.BaseAddress = new Uri(namedArgs["--baseaddress"] ?? "http://localhost");
                    var swaggerResponse = client.GetAsync(namedArgs["swaggeruri"]).Result;

                    // 4) Write to specified output location
                    var outputPath = $"{Directory.GetCurrentDirectory()}\\{namedArgs["output"]}";
                    using (var outputStream = new FileStream(outputPath, FileMode.Create))
                    {
                        swaggerResponse.Content.CopyToAsync(outputStream).Wait();
                        Console.WriteLine($"Swagger JSON succesfully written to {outputPath}");
                    }

                    return 0;
                });
            });

            return runner.Run(args);
        }
    }
}
