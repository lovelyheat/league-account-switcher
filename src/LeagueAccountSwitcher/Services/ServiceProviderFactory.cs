using LeagueAccountSwitcher.Core.Services;
using LeagueAccountSwitcher.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LeagueAccountSwitcher.Services;

public static class ServiceProviderFactory
{
    public static ServiceProvider Create() => new ServiceCollection()
        // Replace these five with the Core implementations.
        .AddSingleton<IAccountStore, PlaceholderAccountStore>()
        .AddSingleton<IRiotClientService, PlaceholderRiotClientService>()
        .AddSingleton<ILcuService, PlaceholderLcuService>()
        .AddSingleton<IAssetCacheService, PlaceholderAssetCacheService>()
        .AddSingleton<IRiotApiService, PlaceholderRiotApiService>()

        .AddSingleton<AccountCoordinator>()
        .AddSingleton<DialogService>()
        .AddSingleton<MainViewModel>()
        .AddSingleton<MainWindow>()
        .BuildServiceProvider();
}
