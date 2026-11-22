using LeagueAccountSwitcher.Core.Services;
using LeagueAccountSwitcher.Core.Services.Assets;
using LeagueAccountSwitcher.Core.Services.Lcu;
using LeagueAccountSwitcher.Core.Services.RiotApi;
using LeagueAccountSwitcher.Core.Services.RiotClient;
using LeagueAccountSwitcher.Core.Services.Storage;
using LeagueAccountSwitcher.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LeagueAccountSwitcher.Services;

public static class ServiceProviderFactory
{
    public static ServiceProvider Create() => new ServiceCollection()
        .AddSingleton<IAccountStore, AccountStore>()
        .AddSingleton<IRiotClientService, RiotClientService>()
        .AddSingleton<ILcuService, LcuService>()
        .AddSingleton<IAssetCacheService, AssetCacheService>()
        .AddSingleton<IRiotApiService, RiotApiService>()
        .AddSingleton<AccountCoordinator>()
        .AddSingleton<DialogService>()
        .AddSingleton<MainViewModel>()
        .AddSingleton<MainWindow>()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
}
