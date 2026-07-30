using FluentValidation;
using FSH.Modules.Webhooks.Contracts.v1.CreateWebhookSubscription;
using FSH.Modules.Webhooks.Services;

namespace FSH.Modules.Webhooks.Features.v1.CreateWebhookSubscription;

public sealed class CreateWebhookSubscriptionCommandValidator : AbstractValidator<CreateWebhookSubscriptionCommand>
{
    public CreateWebhookSubscriptionCommandValidator()
    {
        RuleFor(x => x.Url).NotEmpty()
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .WithMessage("A valid absolute URL is required.")
            .Must(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !WebhookUrlGuard.IsBlockedHost(uri.Host))
            .WithMessage("The URL must not target a private, loopback, link-local, or metadata address.");
        RuleFor(x => x.Events).NotEmpty().WithMessage("At least one event type is required.");
    }
}
