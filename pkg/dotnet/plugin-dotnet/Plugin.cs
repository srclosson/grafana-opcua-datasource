
using System;
using System.IO;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration.Json;
using Pluginv2;

namespace plugin_dotnet
{
    class Plugin
    {
        public const int AppProtoVersion = 1;

        static Opc.Ua.ApplicationConfiguration CreateApplicationConfiguration()
        {
            var certificateValidator = new Opc.Ua.CertificateValidator();
            certificateValidator.CertificateValidation += (sender, eventArgs) =>
            {
                if (Opc.Ua.ServiceResult.IsGood(eventArgs.Error))
                    eventArgs.Accept = true;
                else if (eventArgs.Error.StatusCode.Code == Opc.Ua.StatusCodes.BadCertificateUntrusted)
                    eventArgs.Accept = true;
                else
                    throw new Exception(string.Format("Failed to validate certificate with error code {0}: {1}", eventArgs.Error.Code, eventArgs.Error.AdditionalInfo));
            };

            Opc.Ua.SecurityConfiguration securityConfigurationcv = new Opc.Ua.SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = true,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 1024,
            };
            certificateValidator.Update(securityConfigurationcv);

            return new Opc.Ua.ApplicationConfiguration
            {
                ApplicationName = "Grafana",
                ApplicationType = Opc.Ua.ApplicationType.Client,
                CertificateValidator = certificateValidator,
                ServerConfiguration = new Opc.Ua.ServerConfiguration
                {
                    MaxSubscriptionCount = 100000,
                    MaxMessageQueueSize = 1000000,
                    MaxNotificationQueueSize = 1000000,
                    MaxPublishRequestCount = 10000000,
                },

                SecurityConfiguration = new Opc.Ua.SecurityConfiguration
                {
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false,
                    MinimumCertificateKeySize = 1024,
                },

                TransportQuotas = new Opc.Ua.TransportQuotas
                {
                    OperationTimeout = 6000000,
                    MaxStringLength = int.MaxValue,
                    MaxByteStringLength = int.MaxValue,
                    MaxArrayLength = 65535,
                    MaxMessageSize = 419430400,
                    MaxBufferSize = 65535,
                    ChannelLifetime = -1,
                    SecurityTokenLifetime = -1
                },
                ClientConfiguration = new Opc.Ua.ClientConfiguration
                {
                    DefaultSessionTimeout = -1,
                    MinSubscriptionLifetime = -1,
                },
                DisableHiResClock = true
            };
         }


        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(configure => configure.AddLog4Net())
                .AddTransient<Plugin>();
            services.Configure<LoggerFilterOptions>(options => options.MinLevel = LogLevel.Trace);

            // Build configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetParent(AppContext.BaseDirectory).FullName)
                .AddJsonFile("appsettings.json", false)
                .Build();
            services.AddSingleton<IConfiguration>(configuration);
        }

        static async Task Main(string[] args)
        {
            
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            var serviceProvider = serviceCollection.BuildServiceProvider();

            ILogger logger = serviceProvider.GetService<ILogger<Plugin>>();
            logger.LogDebug("Ua plugin starting");
            var configuration = serviceProvider.GetService<IConfiguration>();
            var servicePort = configuration.GetValue("ServicePort", 6061);
            var serviceHost = configuration.GetValue("ServiceHost", "localhost");
            logger.LogDebug("Port: " + servicePort);
            try
            {
                var traceLogConverter = new TraceLogConverter(logger);
                Prediktor.Log.LogManager.TraceLogFactory = (name => traceLogConverter);
                var nodeCacheFactory = new NodeCacheFactory(logger);
                var connections = new Connections(logger, new Prediktor.UA.Client.SessionFactory(c => true), nodeCacheFactory, CreateApplicationConfiguration);
                
                IDashboardDb dashboardDb = new DashboardDb(logger, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dbs", "dashboardmapping.db"));
                IDashboardResolver dashboardResolver = new DashboardResolver(dashboardDb);

                // Build a server to host the plugin over gRPC
                Server server = new Server
                {
                    Ports = { { serviceHost, servicePort, ServerCredentials.Insecure } },
                    Services = {
                    { Diagnostics.BindService(new DiagnosticsService(logger, connections)) },
                    { Resource.BindService(new ResourceService(logger, connections, dashboardResolver)) },
                    { Data.BindService(new DataService(logger, connections, nodeCacheFactory)) }
                }
                };

                server.Start();

                // Part of the go-plugin handshake:
                //  https://github.com/hashicorp/go-plugin/blob/master/docs/guide-plugin-write-non-go.md#4-output-handshake-information
                await Console.Out.WriteAsync($"1|2|tcp|{serviceHost}:{servicePort}|grpc\n");
                await Console.Out.FlushAsync();

                while (Console.Read() == -1)
                    await Task.Delay(1000);

                await server.ShutdownAsync();
            }
            catch(Exception e)
			{
                logger.LogError(e, "Ua Plugin stopped unexpectedly");
            }
        }
    }
}
