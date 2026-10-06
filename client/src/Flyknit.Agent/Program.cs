using Flyknit.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// 作为 Windows 服务运行（SYSTEM 账号）。直接命令行跑时也能工作，方便调试。
builder.Services.AddWindowsService(options => options.ServiceName = "FlyknitAgent");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
