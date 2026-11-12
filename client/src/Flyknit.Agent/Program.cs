using Flyknit.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 自升级的更新助手（docs/agent-self-update.md）：不起服务，换完文件、看着新版本跑起来就退出
if (args.Contains(SelfUpdater.HelperFlag))
{
    return SelfUpdater.CompleteUpdate(args);
}

var builder = Host.CreateApplicationBuilder(args);

// 作为 Windows 服务运行（SYSTEM 账号）。直接命令行跑时也能工作，方便调试。
builder.Services.AddWindowsService(options => options.ServiceName = "FlyknitAgent");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;
