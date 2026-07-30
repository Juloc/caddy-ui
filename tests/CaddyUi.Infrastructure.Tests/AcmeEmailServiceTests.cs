using CaddyUi.Infrastructure.Certificates;
using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AcmeEmailServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"caddy-ui-acme-tests-{Guid.NewGuid():N}");

    public AcmeEmailServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task UpdateAsync_InsertsEmailAndReloadsValidatedConfiguration()
    {
        var rootConfigPath = Path.Combine(_directory, "Caddyfile");
        await File.WriteAllTextAsync(
            rootConfigPath,
            """
            {
                admin 0.0.0.0:2019
                log default {
                    output stdout
                }
            }

            import /etc/caddy/routes/*.caddy
            """);
        var runner = new RecordingCaddyCommandRunner();
        var service = CreateService(rootConfigPath, runner);

        var result = await service.UpdateAsync("admin@example.com");

        Assert.True(result.Changed);
        Assert.Equal("admin@example.com", result.Email);
        var content = await File.ReadAllTextAsync(rootConfigPath);
        Assert.Contains("    email admin@example.com", content, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_directory, ".caddy-ui-acme-email-managed")));
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal("validate", command[0]),
            command => Assert.Equal("reload", command[0]),
            command => Assert.Equal("validate", command[0]));
    }

    [Fact]
    public async Task UpdateAsync_ReplacesEnvironmentReferenceAndCanRemoveEmail()
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
        var service = CreateService(rootConfigPath, runner);

        var initialState = await service.ReadAsync();
        Assert.True(initialState.UsesEnvironmentVariable);

        await service.UpdateAsync("certificates@example.com");
        var configuredState = await service.ReadAsync();
        Assert.Equal("certificates@example.com", configuredState.Email);
        Assert.False(configuredState.UsesEnvironmentVariable);

        await service.UpdateAsync(string.Empty);
        var removedState = await service.ReadAsync();
        Assert.False(removedState.IsConfigured);
        var content = await File.ReadAllTextAsync(rootConfigPath);
        Assert.DoesNotContain("email ", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_BlankValueCreatesOwnershipMarkerWithoutReload()
    {
        var rootConfigPath = Path.Combine(_directory, "Caddyfile");
        await File.WriteAllTextAsync(rootConfigPath, "{\n}\n");
        var runner = new RecordingCaddyCommandRunner();
        var service = CreateService(rootConfigPath, runner);

        var result = await service.UpdateAsync(string.Empty);

        Assert.True(result.Changed);
        Assert.True(File.Exists(Path.Combine(_directory, ".caddy-ui-acme-email-managed")));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task UpdateAsync_RejectsDisplayNameInsteadOfPlainAddress()
    {
        var rootConfigPath = Path.Combine(_directory, "Caddyfile");
        await File.WriteAllTextAsync(rootConfigPath, "{\n}\n");
        var runner = new RecordingCaddyCommandRunner();
        var service = CreateService(rootConfigPath, runner);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateAsync("Admin <admin@example.com>"));

        Assert.Contains("ungültig", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    private static AcmeEmailService CreateService(
        string rootConfigPath,
        ICaddyCommandRunner runner)
    {
        return new AcmeEmailService(
            new RoutingOptions
            {
                RootConfigPath = rootConfigPath,
                CommandTimeoutSeconds = 5,
            },
            runner);
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
