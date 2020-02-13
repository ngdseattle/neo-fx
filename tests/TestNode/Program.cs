﻿using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeoFx.P2P;

namespace NeoFx.TestNode
{
    class Program
    {
        public static Task Main(string[] args)
        {
            return CreateHostBuilder(args).Build().RunAsync();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .UseSystemd()
                .ConfigureServices((context, services) =>
                {
                    // services.AddTransient<IHeaderStorage, MemoryHeaderStorage>();
                    // services.AddSingleton<IHeaderStorage>(_ => new RocksDbHeaderStorage(@"C:\Users\harry\.neofx-testnode"));
                    services.AddTransient<PipelineSocket>();
                    services.AddSingleton<INodeConnectionFactory, NodeConnectionFactory>();
                    services.AddSingleton<IRemoteNodeFactory, RemoteNodeFactory>();
                    services.Configure<NodeOptions>(context.Configuration.GetSection("NodeOptions"));
                    services.Configure<NetworkOptions>(context.Configuration.GetSection("NetworkOptions"));
                    services.AddHostedService<Worker>();
                });
    }
}
