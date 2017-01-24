using Autofac;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Serilog;
using Shacknews_Push_Notifications.Common;
using System;
using System.IO;

namespace Shacknews_Push_Notifications
{
	public class AppModuleBuilder
	{
		private static Lazy<IContainer> container = new Lazy<IContainer>(() => BuildContainer());
		public static IContainer Container { get { return container.Value; } }
		private static IContainer BuildContainer()
		{
			var builder = new ContainerBuilder();
			builder.RegisterType<NotificationService>().SingleInstance();
			builder.RegisterType<AccessTokenManager>().SingleInstance();
			builder.RegisterType<Monitor>().SingleInstance();
			builder.Register(x =>
			{
				var configBuilder = new ConfigurationBuilder()
					.AddJsonFile("appsettings.json")
					.SetBasePath(Directory.GetCurrentDirectory());

				return configBuilder.Build();
			}).SingleInstance();
			builder.Register(x =>
			{
				var config = x.Resolve<IConfigurationRoot>();
				var appConfig = new AppConfiguration();
				ConfigurationBinder.Bind(config, appConfig);
				return appConfig;
			}).SingleInstance();
			builder.Register<ILogger>(x =>
			{
				var config = x.Resolve<IConfigurationRoot>();
				return new LoggerConfiguration()
					.ReadFrom.Configuration(config)
					.CreateLogger();
			});
			builder.Register(x => new MemoryCache(new MemoryCacheOptions())).SingleInstance();
			builder.RegisterType<UserRepo>().InstancePerDependency();
			return builder.Build();
		}
	}
}
