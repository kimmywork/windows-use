using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WinUse.Platform.Windows.Coordinates;
using WinUse.Transport.Cli;
using WinUse.Transport.Http;
using WinUse.Transport.Http.Security;

namespace WinUse.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // 1) DPI 感知必须在任何窗口/图形调用之前（PoC C2）。
        DpiAwareness.DeclarePerMonitorV2();

        // 2) 一次性 CLI 子命令：invoke / tools / health / help。
        if (args.Length > 0 && args[0] is "invoke" or "tools" or "health" or "help" or "--help" or "-h")
        {
            return await RunCliOnceAsync(args);
        }

        // 2.5) 脚本编辑器 UI：editor / scripts。
        if (args.Length > 0 && args[0] is "editor" or "scripts")
        {
            return RunScriptEditor();
        }

        // 3) 默认：启动回环 HTTP 服务。
        return await RunServerAsync(args);
    }

    private static async Task<int> RunCliOnceAsync(string[] args)
    {
        var services = new ServiceCollection().AddWinUse().BuildServiceProvider();
        var runner = new CliRunner(
            services.GetRequiredService<Core.Dispatch.CapabilityDispatcher>(),
            services.GetRequiredService<Core.Capabilities.CapabilityRegistry>());
        return await runner.RunAsync(args, Console.Out, Console.Error);
    }

    private static void RegisterEditorExceptionLogging()
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUse");
        var logPath = System.IO.Path.Combine(logDir, "editor-errors.log");

        void Log(string source, Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(logPath,
                    $"[{DateTime.Now:O}] {source}: {ex}\n\n");
            }
            catch
            {
                // 日志写入失败时保持静默，避免吞掉原始异常。
            }
        }

        Application.ThreadException += (_, e) => Log("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain", e.ExceptionObject as Exception ?? new Exception("unknown"));
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task", e.Exception);
            e.SetObserved();
        };
    }

    private static int RunScriptEditor()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        RegisterEditorExceptionLogging();

        var services = new ServiceCollection().AddWinUse().BuildServiceProvider();
        var form = new Ui.ScriptEditorForm(
            services.GetRequiredService<Core.Scripts.ScriptStore>(),
            services.GetRequiredService<Core.Scripts.ScriptRunner>(),
            services.GetRequiredService<Core.Capabilities.CapabilityRegistry>());

        // 单实例：已有编辑器驻留托盘时，唤起其窗口后本进程退出。
        using var tray = Ui.EditorTrayHost.CreateOrSignal(form);
        if (tray is null)
        {
            return 0;
        }

        // 强制创建窗口句柄，使托盘线程在窗口首次显示前也能 BeginInvoke。
        _ = form.Handle;

        tray.Start();
        Application.Run();
        return 0;
    }

    private static async Task<int> RunServerAsync(string[] args)
    {
        var options = ParseServerOptions(args);
        var token = options.Token ?? TokenStore.LoadOrCreate();

        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddWinUse())
            .ConfigureWebHostDefaults(web =>
            {
                web.ConfigureKestrel(k =>
                {
                    k.AddServerHeader = false;
                    k.Listen(options.BindAddress, options.Port);
                });
                web.ConfigureServices(services => services.AddWinUseHttp(security =>
                {
                    security.Token = token;
                    security.AllowedHosts.Add($"{options.BindAddress}:{options.Port}");
                    security.AllowedHosts.Add($"127.0.0.1:{options.Port}");
                    security.AllowedHosts.Add($"localhost:{options.Port}");
                }));
                web.Configure(app =>
                {
                    app.UseWinUseSecurity();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapWinUseEndpoints());
                });
            })
            .Build();

        await host.StartAsync();

        var actualPort = options.Port != 0
            ? options.Port
            : host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
                .Addresses.FirstOrDefault() is { } address
                ? ExtractPort(address)
                : 0;

        Console.WriteLine("WinUse 已启动");
        Console.WriteLine($"  端点:   http://127.0.0.1:{actualPort}/v1/invoke");
        Console.WriteLine($"  Token:  {token}");
        Console.WriteLine("  按 Ctrl+C 退出");

        await host.WaitForShutdownAsync();
        return 0;
    }

    private sealed record ServerOptions(System.Net.IPAddress BindAddress, int Port, string? Token);

    private static ServerOptions ParseServerOptions(string[] args)
    {
        var bind = System.Net.IPAddress.Loopback;
        var port = 0; // 0：操作系统分配动态端口
        string? token = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i]);
                    break;
                case "--bind" when i + 1 < args.Length:
                {
                    var raw = args[++i];
                    if (raw != "127.0.0.1" && raw != "localhost" && raw != "::1")
                    {
                        throw new ArgumentException(
                            "M1 仅允许绑定回环地址；0.0.0.0 需显式确认（设计 06 §13），当前版本未开放");
                    }
                    bind = raw == "::1" ? System.Net.IPAddress.IPv6Loopback : System.Net.IPAddress.Loopback;
                    break;
                }
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--help" or "-h":
                    Console.WriteLine("用法: winuse [--port <n>] [--bind 127.0.0.1] [--token <t>]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"未知参数: {args[i]}（winuse help 查看用法）");
            }
        }

        return new ServerOptions(bind, port, token);
    }

    private static int ExtractPort(string address)
    {
        var lastColon = address.LastIndexOf(':');
        return lastColon >= 0 && int.TryParse(address[(lastColon + 1)..], out var p) ? p : 0;
    }
}
