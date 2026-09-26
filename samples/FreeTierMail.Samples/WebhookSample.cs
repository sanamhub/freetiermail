using FreeTierMail.Brevo;
using FreeTierMail.Resend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FreeTierMail.Samples;

// README: "Bounces and complaints".
internal static class WebhookSample
{
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddFreeTierMail(configuration.GetSection("FreeTierMail"))
            .AddBrevo().AddResend()
            .AddWebhook(new ResendWebhook(configuration["Webhooks:Resend"]!))
            .AddWebhook(new BrevoWebhook(configuration["Webhooks:Brevo"]!));
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        // One endpoint for every provider: /webhooks/email/resend, /webhooks/email/brevo.
        app.MapPost("/webhooks/email/{provider}", async (string provider, HttpRequest request, WebhookReceiver receiver, CancellationToken cancellationToken) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, cancellationToken);
            var headers = request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value.ToString()));
            return await receiver.ReceiveAsync(provider, new WebhookRequest(headers, body.ToArray()), cancellationToken) switch
            {
                WebhookResult.Accepted => Results.Ok(),
                WebhookResult.Unauthorized => Results.Unauthorized(),
                WebhookResult.UnknownProvider => Results.NotFound(),
                _ => Results.BadRequest(),
            };
        }).DisableAntiforgery();
    }
}
