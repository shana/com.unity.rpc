﻿using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unity.Ipc.Extensions;

namespace Unity.Ipc
{
    public class IpcHostClient : HostBuilder
    {
        private const string AppSettingsFile = "appsettings.json";
        public Configuration Configuration { get; }
        public IServiceProvider ServiceProvider => host.Services;
        public IApplicationLifetime ApplicationLifetime => ServiceProvider.GetRequiredService<IApplicationLifetime>();

        private IHost host;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private CancellationToken? cancellationToken;
        private bool started = false;

        public IpcHostClient(int port = 0, IpcVersion protocolVersion = default)
        {
            var defaultConfiguration = new Dictionary<string, string>();
            if (port > 0)
                defaultConfiguration.Add("port", port.ToString());
            if (protocolVersion != IpcVersion.Default)
                defaultConfiguration.Add("version", protocolVersion.ToString());

            Configuration = new ClientConfiguration();

            this
                .UseContentRoot(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location))
                .ConfigureHostConfiguration(c => { c.AddEnvironmentVariables("UNITYIPC_"); })
                .ConfigureAppConfiguration((context, app) =>
                {
                    var env = context.HostingEnvironment;
                    app.AddJsonFile(AppSettingsFile, optional: true, reloadOnChange: true);
                    app.AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: true);
                    app.AddEnvironmentVariables();
                    app.AddInMemoryCollection(defaultConfiguration);
                })
                ;
        }

        public new IHost Build()
        {
            ConfigureServices((context, s) =>
            {
                s.AddSingleton(((ClientConfiguration)Configuration).Configure(context, s));
                s.AddSingleton<IHostedService, HostedClient>();
            });
            return base.Build();
        }

        public async Task<IpcClient> Start()
        {
            return await InternalStart();
        }

        public async Task<IpcClient> Start(CancellationToken token)
        {
            return await InternalStart(token);
        }

        private async Task<IpcClient> InternalStart(CancellationToken? token = null)
        {
            cancellationToken = token;
            host = Build();
            if (cancellationToken.HasValue)
                await host.StartAsync(cancellationToken.Value);
            else
                await host.StartAsync();
            started = true;
            return ServiceProvider.GetHostedService<HostedClient>().Client;
        }

        public Task Run()
        {
            return InternalRun();
        }

        public Task Run(CancellationToken token)
        {
            return InternalRun(token);
        }

        private async Task InternalRun(CancellationToken? token = null)
        {
            if (!started)
            {
                cancellationToken = token;
                host = Build();
                if (cancellationToken.HasValue)
                    await host.RunAsync(cancellationToken.Value);
                else
                    await host.RunAsync();
            }
            else
            {
                using (host)
                    await host.WaitForShutdownAsync();
            }
        }

        public Task Stop(int timeout = -1)
        {
            if (!started)
                return Task.CompletedTask;

            if (timeout >= 0)
                return host.StopAsync(TimeSpan.FromMilliseconds(timeout));
            return host.StopAsync();
        }
    }
}
