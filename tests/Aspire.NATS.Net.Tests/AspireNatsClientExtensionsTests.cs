﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.Common.Tests;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.NATS.Net.Tests;

public class AspireNatsClientExtensionsTests : IClassFixture<NatsContainerFixture>
{
    private const string DefaultConnectionName = "nats";

    private readonly NatsContainerFixture _containerFixture;
    private readonly string _connectionString;

    public AspireNatsClientExtensionsTests(NatsContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
        _connectionString = RequiresDockerTheoryAttribute.IsSupported
            ? _containerFixture.GetConnectionString()
            : "nats://apire-host:4222";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:nats", _connectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient("nats");
        }
        else
        {
            builder.AddNatsClient("nats");
        }

        var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsConnection>("nats") :
            host.Services.GetRequiredService<INatsConnection>();

        Assert.Equal(_connectionString, connection.Opts.Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:nats", "unused")
        ]);

        void SetConnectionString(NatsClientSettings settings) => settings.ConnectionString = _connectionString;

        if (useKeyed)
        {
            builder.AddKeyedNatsClient("nats", SetConnectionString);
        }
        else
        {
            builder.AddNatsClient("nats", SetConnectionString);
        }

        var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsConnection>("nats") :
            host.Services.GetRequiredService<INatsConnection>();

        Assert.Equal(_connectionString, connection.Opts.Url);
        // the connection string from config should not be used since code set it explicitly
        Assert.DoesNotContain("unused", connection.Opts.Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "nats" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Nats:Client", key, "ConnectionString"), "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:nats", _connectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient("nats");
        }
        else
        {
            builder.AddNatsClient("nats");
        }

        var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsConnection>("nats") :
            host.Services.GetRequiredService<INatsConnection>();

        Assert.Equal(_connectionString, connection.Opts.Url);
        // the connection string from config should not be used since it was found in ConnectionStrings
        Assert.DoesNotContain("unused", connection.Opts.Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddNatsClient_HealthCheckShouldBeRegisteredByDefault(bool useKeyed)
    {
        var key = DefaultConnectionName;
        var builder = CreateBuilder(_connectionString);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient(key);
        }
        else
        {
            builder.AddNatsClient(DefaultConnectionName);
        }

        var host = builder.Build();

        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        var healthCheckReport = await healthCheckService.CheckHealthAsync();

        var healthCheckName = useKeyed ? $"NATS_{key}" : "NATS";

        Assert.Contains(healthCheckReport.Entries, x => x.Key == healthCheckName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddNatsClient_HealthCheckShouldNotBeRegisteredWhenDisabled(bool useKeyed)
    {
        var builder = CreateBuilder(_connectionString);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient(DefaultConnectionName, settings =>
            {
                settings.HealthChecks = false;
            });
        }
        else
        {
            builder.AddNatsClient(DefaultConnectionName, settings =>
            {
                settings.HealthChecks = false;
            });
        }

        var host = builder.Build();

        var healthCheckService = host.Services.GetService<HealthCheckService>();

        Assert.Null(healthCheckService);
    }

    [RequiresDockerFact]
    public void NatsInstrumentationEndToEnd()
    {
        RemoteExecutor.Invoke(async (connectionString) =>
        {
            var builder = CreateBuilder(connectionString);

            builder.AddNatsClient(DefaultConnectionName);

            var notifier = new ActivityNotifier();
            builder.Services.AddOpenTelemetry().WithTracing(builder => builder.AddProcessor(notifier));

            using var host = builder.Build();
            host.Start();

            var nats = host.Services.GetRequiredService<INatsConnection>();
            await nats.PublishAsync("test");

            await notifier.ActivityReceived.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(notifier.ExportedActivities);

            var activity = notifier.ExportedActivities[0];
            Assert.Equal("test publish", activity.OperationName);
            Assert.Contains(activity.Tags, kvp => kvp.Key == "messaging.system" && kvp.Value == "nats");
        }, _connectionString).Dispose();
    }

    private static HostApplicationBuilder CreateBuilder(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>($"ConnectionStrings:{DefaultConnectionName}", connectionString)
        ]);
        return builder;
    }
}
