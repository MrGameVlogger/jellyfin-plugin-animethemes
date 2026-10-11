using System;
using System.Net.Http.Headers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AnimeThemes;

/// <inheritdoc />
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AnimeThemesDownloader>();

        var productHeader = new ProductInfoHeaderValue(
            "jf-plugin-animethemes",
            applicationHost.ApplicationVersionString);

        serviceCollection.AddTransient<PollyResilienceHandler>();

        serviceCollection
            .AddHttpClient("AnimeThemes", c =>
            {
                c.BaseAddress = new Uri("https://graphql.animethemes.moe/");
                c.DefaultRequestHeaders.UserAgent.Add(productHeader);
            })
            .AddHttpMessageHandler<PollyResilienceHandler>();

        // The media CDN sheds concurrent load with 503s, so theme downloads need the same
        // retry treatment as the API. Downloads are large files on absolute URLs.
        serviceCollection
            .AddHttpClient("AnimeThemesMedia", c =>
            {
                c.DefaultRequestHeaders.UserAgent.Add(productHeader);
                c.Timeout = TimeSpan.FromMinutes(10);
            })
            .AddHttpMessageHandler<PollyResilienceHandler>();
    }
}
