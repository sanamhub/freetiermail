using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FreeTierMail.Samples;

// README: "With dependency injection".
internal static class DependencyInjectionSample
{
    public static IServiceCollection Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddFreeTierMail(configuration.GetSection("FreeTierMail"))
            .AddBrevo()
            .AddResend()
            .AddMailjet();
        return services;
    }
}
