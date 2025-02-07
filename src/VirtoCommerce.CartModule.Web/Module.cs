using System;
using System.Linq;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.CartModule.Core;
using VirtoCommerce.CartModule.Core.Events;
using VirtoCommerce.CartModule.Core.Model;
using VirtoCommerce.CartModule.Core.Model.Search;
using VirtoCommerce.CartModule.Core.Services;
using VirtoCommerce.CartModule.Data.BackgroundJobs;
using VirtoCommerce.CartModule.Data.Handlers;
using VirtoCommerce.CartModule.Data.MySql;
using VirtoCommerce.CartModule.Data.PostgreSql;
using VirtoCommerce.CartModule.Data.Repositories;
using VirtoCommerce.CartModule.Data.Services;
using VirtoCommerce.CartModule.Data.SqlServer;
using VirtoCommerce.Platform.Core.Bus;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.DynamicProperties;
using VirtoCommerce.Platform.Core.GenericCrud;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Platform.Data.Extensions;
using VirtoCommerce.Platform.Hangfire;
using VirtoCommerce.Platform.Hangfire.Extensions;

namespace VirtoCommerce.CartModule.Web
{
    public class Module : IModule, IHasConfiguration
    {
        public ManifestModuleInfo ModuleInfo { get; set; }
        public IConfiguration Configuration { get; set; }

        public void Initialize(IServiceCollection serviceCollection)
        {
            var databaseProvider = Configuration.GetValue("DatabaseProvider", "SqlServer");
            serviceCollection.AddDbContext<CartDbContext>((provider, options) =>
            {
                var connectionString = Configuration.GetConnectionString(ModuleInfo.Id) ?? Configuration.GetConnectionString("VirtoCommerce");

                switch (databaseProvider)
                {
                    case "MySql":
                        options.UseMySqlDatabase(connectionString);
                        break;
                    case "PostgreSql":
                        options.UsePostgreSqlDatabase(connectionString);
                        break;
                    default:
                        options.UseSqlServerDatabase(connectionString);
                        break;
                }
            });

            switch (databaseProvider)
            {
                case "MySql":
                    serviceCollection.AddTransient<ICartRawDatabaseCommand, MySqlCartRawDatabaseCommand>();
                    break;
                case "PostgreSql":
                    serviceCollection.AddTransient<ICartRawDatabaseCommand, PostgreSqlCartRawDatabaseCommand>();
                    break;
                default:
                    serviceCollection.AddTransient<ICartRawDatabaseCommand, SqlServerCartRawDatabaseCommand>();
                    break;
            }


            serviceCollection.AddTransient<ICartRepository, CartRepository>();
            serviceCollection.AddTransient<Func<ICartRepository>>(provider => () => provider.CreateScope().ServiceProvider.GetRequiredService<ICartRepository>());
            serviceCollection.AddTransient<ICrudService<ShoppingCart>, ShoppingCartService>();
            serviceCollection.AddTransient(x => (IShoppingCartService)x.GetRequiredService<ICrudService<ShoppingCart>>());
            serviceCollection.AddTransient<ISearchService<ShoppingCartSearchCriteria, ShoppingCartSearchResult, ShoppingCart>, ShoppingCartSearchService>();
            serviceCollection.AddTransient(x => (IShoppingCartSearchService)x.GetRequiredService<ISearchService<ShoppingCartSearchCriteria, ShoppingCartSearchResult, ShoppingCart>>());
            serviceCollection.AddTransient<IShoppingCartTotalsCalculator, DefaultShoppingCartTotalsCalculator>();
            serviceCollection.AddTransient<IShoppingCartBuilder, ShoppingCartBuilder>();
            serviceCollection.AddTransient<IWishlistService, WishlistService>();

            serviceCollection.AddTransient<CartChangedEventHandler>();

        }

        public void PostInitialize(IApplicationBuilder appBuilder)
        {
            var permissionsProvider = appBuilder.ApplicationServices.GetRequiredService<IPermissionsRegistrar>();
            permissionsProvider.RegisterPermissions(ModuleConstants.Security.Permissions.AllPermissions.Select(x =>
                new Permission()
                {
                    GroupName = "Cart",
                    ModuleId = ModuleInfo.Id,
                    Name = x
                }).ToArray());

            var dynamicPropertyRegistrar = appBuilder.ApplicationServices.GetRequiredService<IDynamicPropertyRegistrar>();
            dynamicPropertyRegistrar.RegisterType<LineItem>();
            dynamicPropertyRegistrar.RegisterType<Payment>();
            dynamicPropertyRegistrar.RegisterType<Shipment>();
            dynamicPropertyRegistrar.RegisterType<ShoppingCart>();

            var settingsRegistrar = appBuilder.ApplicationServices.GetRequiredService<ISettingsRegistrar>();
            settingsRegistrar.RegisterSettings(ModuleConstants.Settings.General.AllSettings, ModuleInfo.Id);


            var recurringJobManager = appBuilder.ApplicationServices.GetService<IRecurringJobManager>();
            var settingsManager = appBuilder.ApplicationServices.GetService<ISettingsManager>();

            recurringJobManager.WatchJobSetting(
                settingsManager,
                new SettingCronJobBuilder()
                    .SetEnablerSetting(ModuleConstants.Settings.General.EnableDeleteObsoleteCarts)
                    .SetCronSetting(ModuleConstants.Settings.General.CronDeleteObsoleteCarts)
                    .ToJob<DeleteObsoleteCartsJob>(x => x.Process())
                    .Build());

            var inProcessBus = appBuilder.ApplicationServices.GetService<IHandlerRegistrar>();
            inProcessBus.RegisterHandler<CartChangedEvent>(async (message, token) => await appBuilder.ApplicationServices.GetService<CartChangedEventHandler>().Handle(message));
            inProcessBus.RegisterHandler<CartChangeEvent>(async (message, token) => await appBuilder.ApplicationServices.GetService<CartChangedEventHandler>().Handle(message));

            using (var serviceScope = appBuilder.ApplicationServices.CreateScope())
            {
                var databaseProvider = Configuration.GetValue("DatabaseProvider", "SqlServer");

                using (var dbContext = serviceScope.ServiceProvider.GetRequiredService<CartDbContext>())
                {
                    if (databaseProvider == "SqlServer")
                    {
                        dbContext.Database.MigrateIfNotApplied(MigrationName.GetUpdateV2MigrationName(ModuleInfo.Id));
                    }
                    dbContext.Database.Migrate();
                }
            }
        }

        public void Uninstall()
        {
            // Method intentionally left empty.
        }
    }
}
