using CaddyUi.Infrastructure.Certificates;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Localization;

/// <summary>
/// Converts certificate state codes into localized UI text. Infrastructure keeps
/// provider and Caddy diagnostics language-neutral at the boundary; only raw
/// external error messages are rendered unchanged.
/// </summary>
public static class CertificateUiText
{
    public static string CertificateLabel(
        IStringLocalizer<SharedResource> localizer,
        CertificateStatusItem certificate)
    {
        return localizer[certificate.State switch
        {
            "not-requested" => "Not requested",
            "blocked" when certificate.ExpiresAt <= DateTimeOffset.UtcNow => "Renewal blocked",
            "blocked" => "Acquisition blocked",
            "renewing" => "Renewal running",
            "retry-scheduled" => "Retry scheduled",
            "renewal-failed" => "Renewal failed",
            "renewal-pending" => "Renewal pending",
            "expired" => "Expired",
            "renewal-due" => "In renewal window",
            "active" => "Available",
            "obtaining" => "Acquisition running",
            "acquisition-failed" => "Acquisition failed",
            "verifying" => "Checking storage",
            "requested" => "Requested",
            "draft" => "Ready for apply",
            _ => certificate.State,
        }];
    }

    public static string CertificateDetail(
        IStringLocalizer<SharedResource> localizer,
        CertificateStatusItem certificate)
    {
        var expires = certificate.ExpiresAt;
        var renewal = certificate.RenewalWindowStartsAt;
        return certificate.State switch
        {
            "not-requested" => localizer["Can be enabled later in the domain settings."],
            "blocked" => localizer["The DNS provider or active configuration blocks this certificate operation."],
            "renewing" when expires is not null => localizer["Caddy is renewing the certificate. Previous expiry: {0:u}.", expires.Value],
            "retry-scheduled" => localizer["The previous attempt failed and Caddy scheduled another attempt."],
            "renewal-failed" => localizer["The latest renewal attempt failed. Review the lifecycle error below."],
            "renewal-pending" => localizer["The certificate requires renewal, but no active attempt was detected in the available logs."],
            "expired" when expires is not null => localizer["The stored certificate expired at {0:u} and is not present in the active configuration.", expires.Value],
            "renewal-due" when expires is not null && renewal is not null => localizer["Valid until {0:u}. The estimated renewal window started at {1:u}.", expires.Value, renewal.Value],
            "active" when expires is not null && renewal is not null => localizer["Valid until {0:u}. Estimated renewal window starts at {1:u}.", expires.Value, renewal.Value],
            "active" when expires is not null => localizer["Valid until {0:u}.", expires.Value],
            "obtaining" => localizer["Caddy is currently processing a certificate attempt."],
            "acquisition-failed" => localizer["The latest acquisition attempt failed. Review the lifecycle error below."],
            "verifying" => localizer["Caddy reported success, but the certificate file is not visible in the mounted storage yet."],
            "requested" => localizer["The name is active in Caddy, but no certificate is visible in storage yet."],
            "draft" => localizer["Not active in Caddy yet. Review the preview and apply the configuration."],
            _ => localizer["Certificate status is based on active configuration, storage, and available Caddy logs."],
        };
    }

    public static string LifecycleLabel(
        IStringLocalizer<SharedResource> localizer,
        CertificateLifecycleStatus lifecycle)
    {
        return localizer[lifecycle.State switch
        {
            "in-progress" => lifecycle.RecentAttempts.FirstOrDefault()?.State switch
            {
                "challenging" => "DNS-01 challenge running",
                "propagating" => "Checking DNS propagation",
                _ => "Certificate attempt running",
            },
            "retry-scheduled" => "Retry scheduled",
            "failed" => "Latest attempt failed",
            "succeeded" => "Latest operation succeeded",
            "blocked" => "Blocked by configuration",
            "managed" => "Managed automatically by Caddy",
            "draft" => "Waiting for apply",
            _ => "No attempt detected",
        }];
    }

    public static string CurrentAction(
        IStringLocalizer<SharedResource> localizer,
        CertificateLifecycleStatus lifecycle)
    {
        var state = lifecycle.RecentAttempts.FirstOrDefault()?.State ?? lifecycle.State;
        return localizer[state switch
        {
            "started" => "Initializing ACME operation",
            "challenging" => "Creating DNS-01 TXT record",
            "propagating" => "Waiting for and checking DNS propagation",
            "retry-scheduled" => "Waiting for the next attempt",
            "failed" => "Latest attempt finished with an error",
            "succeeded" => "Certificate processed successfully",
            "draft" => "Configuration waiting for apply",
            "managed" => "Certificate managed automatically by Caddy",
            _ => "Certificate state being evaluated",
        }];
    }

    public static string CurrentActionDetail(
        IStringLocalizer<SharedResource> localizer,
        CertificateLifecycleStatus lifecycle,
        int? propagationDelaySeconds,
        int? propagationTimeoutSeconds)
    {
        var state = lifecycle.RecentAttempts.FirstOrDefault()?.State ?? lifecycle.State;
        return state switch
        {
            "started" => localizer["Caddy loads the ACME account, certificate names, and configured DNS provider."],
            "challenging" => localizer["Caddy creates the challenge value and writes it as TXT at {0}.", lifecycle.DnsChallengeName],
            "propagating" => localizer[
                "Caddy waits {0} before the first check, then checks authoritative DNS for up to {1}.",
                FormatDuration(localizer, propagationDelaySeconds),
                FormatDuration(localizer, propagationTimeoutSeconds ?? lifecycle.PropagationTimeoutSeconds)],
            "retry-scheduled" => localizer["The previous attempt failed. Caddy applies its backoff and retries at the displayed time."],
            "failed" => localizer["The complete error is shown below. Force retry validates and reloads the same active configuration with --force."],
            "succeeded" => localizer["Caddy verifies or stores the issued certificate in the shared certificate storage."],
            _ => localizer["This view combines active Caddy configuration, certificate storage, and available Caddy logs."],
        };
    }

    public static string ProviderTestLabel(
        IStringLocalizer<SharedResource> localizer,
        string status)
    {
        return localizer[status switch
        {
            "passed" or "success" or "succeeded" or "ok" => "Successful",
            "failed" => "Failed",
            "untested" => "Not tested yet",
            _ when string.IsNullOrWhiteSpace(status) => "Unknown",
            _ => status,
        }];
    }

    public static string AttemptLabel(
        IStringLocalizer<SharedResource> localizer,
        string state)
    {
        return localizer[state switch
        {
            "succeeded" => "Successful",
            "failed" => "Failed",
            "retry-scheduled" => "Failed, retry scheduled",
            "propagating" => "DNS check running",
            "challenging" => "Creating TXT challenge",
            "started" => "Started",
            _ => "Running",
        }];
    }

    public static string EventLabel(
        IStringLocalizer<SharedResource> localizer,
        string state)
    {
        return AttemptLabel(localizer, state);
    }

    public static string EventDetail(
        IStringLocalizer<SharedResource> localizer,
        CertificateAttemptItem item)
    {
        return item.State switch
        {
            "started" => localizer["Caddy started a certificate operation."],
            "challenging" => localizer["Caddy prepared or wrote the DNS-01 TXT challenge."],
            "propagating" => localizer["Caddy is waiting for the TXT value to become visible through authoritative DNS."],
            "retry-scheduled" => localizer["The attempt failed and another attempt was scheduled."],
            "succeeded" => localizer["The certificate operation completed successfully."],
            "failed" => string.IsNullOrWhiteSpace(item.Detail) ? localizer["The certificate operation failed."] : item.Detail,
            _ => localizer["Certificate operation event."],
        };
    }

    public static IReadOnlyList<string> Tips(
        IStringLocalizer<SharedResource> localizer,
        CertificateLifecycleStatus lifecycle)
    {
        var tips = new List<string>();
        if (!lifecycle.Applied)
        {
            tips.Add(localizer["Create a preview and apply the configuration before expecting certificate acquisition."]);
        }

        if (!string.IsNullOrWhiteSpace(lifecycle.ProviderType) &&
            lifecycle.ProviderTestStatus is not ("passed" or "success" or "succeeded" or "ok"))
        {
            tips.Add(localizer["Run the DNS provider connection test for the assigned domain."]);
        }

        if (!string.IsNullOrWhiteSpace(lifecycle.DnsChallengeName))
        {
            tips.Add(localizer["Verify that the current TXT value is visible on every authoritative nameserver."]);
        }

        if (lifecycle.ConsecutiveFailures > 0)
        {
            tips.Add(localizer["Review the latest error and retry time before forcing another attempt."]);
        }

        return tips;
    }

    public static string FormatDuration(
        IStringLocalizer<SharedResource> localizer,
        int? seconds)
    {
        if (seconds is null)
        {
            return localizer["Not configured"];
        }

        if (seconds > 0 && seconds % 86_400 == 0)
        {
            return localizer["{0} days", seconds / 86_400];
        }

        if (seconds > 0 && seconds % 3_600 == 0)
        {
            return localizer["{0} hours", seconds / 3_600];
        }

        if (seconds > 0 && seconds % 60 == 0)
        {
            return localizer["{0} minutes", seconds / 60];
        }

        return localizer["{0} seconds", seconds];
    }
}
