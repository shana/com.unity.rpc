﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamRpc;

namespace Unity.Rpc.Hosted
{
    public class RpcHostedServer : RpcHost<RpcServer, RpcHostedServer>, IHostedService
    {
        private struct ClientData : IDisposable
        {
            public string Id;
            public IRegistration Registration { get; }
            public IRequestContext Context { get; }
            public IServiceScope ServiceScope { get; }

            public ClientData(string id, IRegistration registration, IRequestContext context, IServiceScope serviceScope)
            {
                this.Id = id;
                this.Registration = registration;
                this.Context = context;
                this.ServiceScope = serviceScope;
            }

            public void Dispose()
            {
                Context?.Dispose();
                ServiceScope?.Dispose();
            }
        }

        public event Action<IServiceProvider> OnClientConnect;
        public event Action<IServiceProvider> OnClientReady;
        public event Action<IServiceProvider, JsonRpcDisconnectedEventArgs> OnClientDisconnect;

        private Dictionary<string, ClientData> clients = new Dictionary<string,ClientData>();
        private ClientData currentClientData;
        private static object lck = new object();

        public RpcHostedServer(Configuration configuration)
        {
            Host = this;

            RegisterRpc(new RpcServer(configuration))
                .ClientConnecting((registration, context) => {
                    var scope = ServiceProvider.CreateScope();

                    // new client, force the service provider to cache the registration and context
                    // instances for this scope
                    // every client gets its own service provider
                    lock(lck)
                    {
                        currentClientData = new ClientData(context.Id, registration, context, scope);
                        scope.ServiceProvider.GetService<IRequestContext>();
                        scope.ServiceProvider.GetService<IRegistration>();
                    }
                    clients.Add(context.Id, currentClientData);
                    RaiseOnClientConnect(scope.ServiceProvider);
                })
                .ClientDisconnecting((context, args) => {
                    if (clients.TryGetValue(context.Id, out var client))
                    {
                        clients.Remove(context.Id);
                        RaiseOnClientDisconnect(client.ServiceScope.ServiceProvider, args);
                        client.Dispose();
                    }
                })
                .ClientReady(context => {
                    if (clients.TryGetValue(context.Id, out var client))
                    {
                        RaiseOnClientReady(client.ServiceScope.ServiceProvider);
                    }
                });

            // Register scoped services that will be retrieved on a per-client basis
            ConfigureServices((context, collection) => {
                collection.AddScoped(provider => currentClientData.Context);
                collection.AddScoped(provider => currentClientData.Registration);
            });
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => Rpc.Stop());
            await Rpc.Start();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Rpc.Stop();
            var stopTask = Rpc.Run();
            await Task.WhenAny(stopTask, Task.Delay(-1, cancellationToken));
        }

        /// <summary>
        /// Register a class as a local rpc target that can receive rpc calls. This instance will be created
        /// when a client connects, so it will be scoped to that specific client.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public RpcHostedServer AddLocalScoped<T>()
            where T : class
        {
            ConfigureServices((context, collection) => collection.AddScoped<T>());
            ClientConnecting(scopedProvider => scopedProvider.GetService<IRegistration>().RegisterLocalTarget(scopedProvider.GetService<T>()));
            return this;
        }

        public RpcHostedServer AddLocalScoped<T>(Func<IServiceProvider, T> factory)
            where T : class
        {
            ConfigureServices((context, collection) => collection.AddScoped(factory));
            ClientConnecting(scopedProvider => scopedProvider.GetService<IRegistration>().RegisterLocalTarget(scopedProvider.GetService<T>()));
            return this;
        }

        public RpcHostedServer ClientConnecting(Action<IServiceProvider> onConnect)
        {
            OnClientConnect += onConnect;
            return this;
        }

        public RpcHostedServer ClientDisconnecting(Action<IServiceProvider, JsonRpcDisconnectedEventArgs> onDisconnect)
        {
            OnClientDisconnect += onDisconnect;
            return this;
        }

        public RpcHostedServer ClientReady(Action<IServiceProvider> onClientReady)
        {
            OnClientReady += onClientReady;
            return this;
        }

        protected void RaiseOnClientConnect(IServiceProvider serviceProvider)
        {
            OnClientConnect?.Invoke(serviceProvider);
        }

        protected void RaiseOnClientDisconnect(IServiceProvider serviceProvider, JsonRpcDisconnectedEventArgs args)
        {
            OnClientDisconnect?.Invoke(serviceProvider, args);
        }

        protected void RaiseOnClientReady(IServiceProvider serviceProvider)
        {
            OnClientReady?.Invoke(serviceProvider);
        }
    }
}
