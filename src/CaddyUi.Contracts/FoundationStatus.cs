namespace CaddyUi.Contracts;

public sealed record FoundationStatus(
    string Product,
    string Version,
    string Runtime,
    bool IsOperational);
