namespace CaddyUi.Domain.Certificates;

public enum CertificateMode
{
    Inherit = 0,
    Wildcard = 1,
    Individual = 2,
}

public static class CertificateModeExtensions
{
    public static string ToStorageValue(this CertificateMode mode)
    {
        return mode switch
        {
            CertificateMode.Inherit => "inherit",
            CertificateMode.Wildcard => "wildcard",
            CertificateMode.Individual => "individual",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported certificate mode."),
        };
    }

    public static CertificateMode ParseStorageValue(string? value, CertificateMode fallback = CertificateMode.Inherit)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "inherit" => CertificateMode.Inherit,
            "wildcard" => CertificateMode.Wildcard,
            "individual" => CertificateMode.Individual,
            _ => fallback,
        };
    }
}
