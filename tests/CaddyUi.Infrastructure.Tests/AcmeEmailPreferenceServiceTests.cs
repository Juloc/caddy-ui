using CaddyUi.Infrastructure.Certificates;
using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AcmeEmailPreferenceServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"caddy-ui-acme-preference-tests-{Guid.NewGuid():N}");

    public AcmeEmailPreferenceServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task UpdateAsync_BlankValueUsesAvailableEnvironmentVariable()
    {
        var rootConfigPath = Path.Combine(_directory, "Caddyfile");
        await File.WriteAllTextAsync(
            rootConfigPath,
            """
            {
                email environment@example.com
                admin 0.0.0.0:2019
            }
            """);
        var markerPath = Path.Combine(_directory, ".caddy-ui-acme-email-managed");
        await File.WriteAllTextAsync(markerPath, "managed-by=caddy-ui\n");
        var runner = new RecordingCaddyCommandRunner();
        var service = CreateService(
            rootConfigPath,
            runner,
            static () => "environment@example.com");

        var result = await service.UpdateAsync(string.Empty);

        Assert.True(result.Changed);
        Assert.True(result.UsesEnvironmentVariable);
        Assert.Equal(string.Empty, result.Email);
        var content = await File.ReadAllTextAsync(rootConfigPath);
        Assert.Contains("    email {$ACME_EMAIL}", content, StringComparison.Ordinal);
        Assert.DoesNotContain("    email environment@example.com", content, StringComparison.Ordinal);
        Assert.False(File.Exists(markerPath));
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal("validate", command[0]),
            command => Assert.Equal("reload", command[0]),
            command => Assert.Equal("validate", command[0]));
    }

    [Fact]
    public async Task UpdateAsync_BlankValueRemovesEmailWhenEnvironmentVariableIsUnavailable()
    {
        var rootConfigPath = Path.Combine(_directory, "Caddyfile");
        await File.WriteAllTextAsync(
            rootConfigPath,
            """
            {
                email ui@example.com
                admin 0.0.0.0:2019
            }
            """);
        var markerPath = Path.Combine(_directory, ".caddy-ui-acme-email-managed");
        await File.WriteAllTextAsync(markerPath, "managed-by=caddy-ui\n");
        var runner = new RecordingCaddyCommandRunner();
        var service = CreateService(rootConfigPath, runner, static () => null);

        var result = await service.UpdateAsync(string.Empty);

        Assert.True(result.Changed);
        Assert.False(result.UsesEnvironmentVariable);
        Assert.Equal(string.Empty, result.Email);
        var content = await File.ReadAllTextAsync(rootConfigPath);
        Assert.DoesNotContain("email ", content, StringComparison.Ordinal);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task UpdateAsync_ExplicitUiValueOverridesEnvironmentVariable()
    {
        var rootConfigPath = Path.Combine(_directory, "Caddyfile");
        await File.WriteAllTextAsync(
            rootConfigPath,
            """
            {
                email {$ACME_EMAIL}
                admin 0.0.0.0:2019
            }
            """);
        var runner = new RecordingCaddyCommandRunner();
        var service = CreateService(
            rootConfigPath,
            runner,
            static () => "environment@example.com");

        var result = await service.UpdateAsync("ui@example.com");

        Assert.True(result.Changed);
        Assert.False(result.UsesEnvironmentVariable);
        Assert.Equal("ui@example.com", result.Email);
        var content = await File.ReadAllTextAsync(rootConfigPath);
        Assert.Contains("    email ui@example.com", content, StringComparison.Ordinal);
        Assert.DoesNotContain("{$ACME_EMAIL}", content, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_directory, ".caddy-ui-acme-email-managed")));
    }

    private static AcmeEmailPreferenceService CreateService(
        string rootConfigPath,
        ICaddyCommandRunner runner,
        Func<string?> environmentEmailProvider)
    {
        var options = new RoutingOptions
        {
            RootConfigPath = rootConfigPath,
            CommandTimeoutSeconds = 5,
        };
        var emailService = new AcmeEmailService(options, runner);
        return new AcmeEmailPreferenceService(
            emailService,
            options,
            runner,
            environmentEmailProvider);
    }

    private sealed class RecordingCaddyCommandRunner : ICaddyCommandRunner
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public Task<CaddyCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments.ToArray());
            return Task.FromResult(new CaddyCommandResult(0, string.Empty, string.Empty, TimedOut: false));
        }
    }
}
