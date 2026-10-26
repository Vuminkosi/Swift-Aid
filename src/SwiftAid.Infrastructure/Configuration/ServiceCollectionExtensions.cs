using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Minio;
using NATS.Client.Core;
using NATS.Net;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Caching;
using SwiftAid.Infrastructure.Messaging;
using SwiftAid.Infrastructure.Notifications;
using SwiftAid.Infrastructure.Storage;

namespace SwiftAid.Infrastructure.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSwiftAidInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration) =>
        AddSwiftAidInfrastructureCore(services, configuration, hostEnvironment: null);

    public static IServiceCollection AddSwiftAidInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        return AddSwiftAidInfrastructureCore(services, configuration, hostEnvironment);
    }

    private static IServiceCollection AddSwiftAidInfrastructureCore(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var transportSecurityOptions = ReadTransportSecurityOptions(configuration);
        var optionsValidator = new ProviderOptionsValidator(hostEnvironment, transportSecurityOptions);
        services.AddSingleton(optionsValidator);
        services.AddSingleton<IValidateOptions<MinioOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<RedisOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<NatsOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<FcmOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<QdrantOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<MetaProviderOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<FederationOptions>>(optionsValidator);
        services.AddSingleton(transportSecurityOptions);
        services.AddSingleton<IOptions<TransportSecurityOptions>>(Options.Create(transportSecurityOptions));
        var minioOptions = RegisterOptions(services, ReadMinioOptions(configuration), optionsValidator);
        var redisOptions = RegisterOptions(services, ReadRedisOptions(configuration), optionsValidator);
        var natsOptions = RegisterOptions(services, ReadNatsOptions(configuration), optionsValidator);
        var fcmOptions = RegisterOptions(services, ReadFcmOptions(configuration), optionsValidator);
        _ = RegisterOptions(services, ReadQdrantOptions(configuration), optionsValidator);
        _ = RegisterOptions(services, ReadMetaOptions(configuration), optionsValidator);
        _ = RegisterOptions(services, ReadFederationOptions(configuration), optionsValidator);
        _ = RegisterOptions(services, new SwiftAidDatabaseOptions
        {
            ConnectionString = configuration.GetConnectionString("SwiftAid") ?? string.Empty
        }, optionsValidator);

        if (minioOptions.Enabled)
        {
            var endpoint = new Uri(minioOptions.Endpoint, UriKind.Absolute);
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(endpoint.Host, endpoint.Port)
                .WithCredentials(minioOptions.AccessKey, minioOptions.SecretKey)
                .WithSSL(minioOptions.UseSsl)
                .Build());
            services.AddSingleton<IObjectStorage, MinioObjectStorage>();
        }
        else
        {
            services.AddSingleton<IObjectStorage, NotConfiguredObjectStorage>();
        }

        if (redisOptions.Enabled)
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisOptions.ConnectionString;
                options.InstanceName = redisOptions.InstanceName;
            });
            services.AddSingleton<ICacheStore, RedisCacheStore>();
        }
        else
        {
            services.AddSingleton<ICacheStore, NotConfiguredCacheStore>();
        }

        if (natsOptions.Enabled)
        {
            services.AddSingleton<INatsClient>(_ => new NatsClient(new NatsOpts
            {
                Url = natsOptions.Url,
                AuthOpts = new NatsAuthOpts
                {
                    Username = natsOptions.Username,
                    Password = natsOptions.Password
                },
                ConnectTimeout = TimeSpan.FromSeconds(5),
                RequestTimeout = TimeSpan.FromSeconds(5),
                PublishTimeoutOnDisconnected = true
            }));
            services.AddSingleton<IEventPublisher, JetStreamEventPublisher>();
            services.AddSingleton<IInAppNotificationPublisher, JetStreamInAppNotificationPublisher>();
        }
        else
        {
            services.AddSingleton<IEventPublisher, NotConfiguredEventPublisher>();
            services.AddSingleton<IInAppNotificationPublisher, NotConfiguredInAppNotificationPublisher>();
        }

        if (fcmOptions.Enabled)
        {
            services.AddSingleton(_ => FirebaseApp.Create(new AppOptions
            {
                Credential = CredentialFactory.FromFile<GoogleCredential>(fcmOptions.CredentialsFilePath),
                ProjectId = fcmOptions.ProjectId
            }, $"swiftaid-{Guid.NewGuid():N}"));
            services.AddSingleton(serviceProvider =>
                FirebaseMessaging.GetMessaging(serviceProvider.GetRequiredService<FirebaseApp>()));
            services.AddSingleton<IPushNotificationSender, FcmPushNotificationSender>();
        }
        else
        {
            services.AddSingleton<IPushNotificationSender, NotConfiguredPushNotificationSender>();
        }

        return services;
    }

    private static TOptions RegisterOptions<TOptions>(
        IServiceCollection services,
        TOptions options,
        ProviderOptionsValidator optionsValidator)
        where TOptions : class
    {
        Validate(options, optionsValidator);
        services.AddSingleton(options);
        services.AddSingleton<IOptions<TOptions>>(Options.Create(options));
        return options;
    }

    private static MinioOptions ReadMinioOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(MinioOptions.SectionName);
        return new MinioOptions
        {
            Enabled = ReadBool(section, nameof(MinioOptions.Enabled)),
            Endpoint = ReadString(section, nameof(MinioOptions.Endpoint)),
            Bucket = ReadString(section, nameof(MinioOptions.Bucket)),
            MaxObjectSizeBytes = ReadLong(section, nameof(MinioOptions.MaxObjectSizeBytes)),
            AccessKey = ReadString(section, nameof(MinioOptions.AccessKey)),
            SecretKey = ReadString(section, nameof(MinioOptions.SecretKey)),
            UseSsl = ReadBool(section, nameof(MinioOptions.UseSsl))
        };
    }

    private static RedisOptions ReadRedisOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(RedisOptions.SectionName);
        return new RedisOptions
        {
            Enabled = ReadBool(section, nameof(RedisOptions.Enabled)),
            ConnectionString = ReadString(section, nameof(RedisOptions.ConnectionString)),
            InstanceName = ReadString(section, nameof(RedisOptions.InstanceName), "swiftaid:"),
            MaximumTtlSeconds = ReadLong(section, nameof(RedisOptions.MaximumTtlSeconds), 3600)
        };
    }

    private static NatsOptions ReadNatsOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(NatsOptions.SectionName);
        return new NatsOptions
        {
            Enabled = ReadBool(section, nameof(NatsOptions.Enabled)),
            Url = ReadString(section, nameof(NatsOptions.Url)),
            Username = ReadString(section, nameof(NatsOptions.Username)),
            Password = ReadString(section, nameof(NatsOptions.Password)),
            EventStream = ReadString(section, nameof(NatsOptions.EventStream)),
            EventSubjectPrefix = ReadString(section, nameof(NatsOptions.EventSubjectPrefix)),
            NotificationStream = ReadString(section, nameof(NatsOptions.NotificationStream)),
            NotificationSubject = ReadString(section, nameof(NatsOptions.NotificationSubject))
        };
    }

    private static FcmOptions ReadFcmOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(FcmOptions.SectionName);
        return new FcmOptions
        {
            Enabled = ReadBool(section, nameof(FcmOptions.Enabled)),
            ProjectId = ReadString(section, nameof(FcmOptions.ProjectId)),
            CredentialsFilePath = ReadString(section, nameof(FcmOptions.CredentialsFilePath))
        };
    }

    private static QdrantOptions ReadQdrantOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(QdrantOptions.SectionName);
        return new QdrantOptions
        {
            Enabled = ReadBool(section, nameof(QdrantOptions.Enabled)),
            Endpoint = ReadString(section, nameof(QdrantOptions.Endpoint)),
            ApiKey = ReadString(section, nameof(QdrantOptions.ApiKey))
        };
    }

    private static MetaProviderOptions ReadMetaOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(MetaProviderOptions.SectionName);
        return new MetaProviderOptions
        {
            Enabled = ReadBool(section, nameof(MetaProviderOptions.Enabled)),
            Provider = ReadString(section, nameof(MetaProviderOptions.Provider)),
            RouteAlias = ReadString(section, nameof(MetaProviderOptions.RouteAlias)),
            ModelId = ReadString(section, nameof(MetaProviderOptions.ModelId)),
            ApiKey = ReadString(section, nameof(MetaProviderOptions.ApiKey))
        };
    }

    private static FederationOptions ReadFederationOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(FederationOptions.SectionName);
        return new FederationOptions
        {
            Enabled = ReadBool(section, nameof(FederationOptions.Enabled)),
            Authority = ReadString(section, nameof(FederationOptions.Authority)),
            ClientId = ReadString(section, nameof(FederationOptions.ClientId)),
            ClientSecret = ReadString(section, nameof(FederationOptions.ClientSecret))
        };
    }

    private static TransportSecurityOptions ReadTransportSecurityOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(TransportSecurityOptions.SectionName);
        var configuredHosts = section
            .GetSection(nameof(TransportSecurityOptions.AllowedDevelopmentHosts))
            .GetChildren()
            .Select(host => host.Value)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host!)
            .ToArray();

        return new TransportSecurityOptions
        {
            AllowPlaintextDevelopment = ReadBool(
                section,
                nameof(TransportSecurityOptions.AllowPlaintextDevelopment)),
            AllowedDevelopmentHosts = configuredHosts.Length == 0
                ? ["localhost", "127.0.0.1", "::1"]
                : configuredHosts
        };
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool defaultValue = false)
    {
        var raw = section[key];
        if (raw is null)
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException($"{section.Path}:{key} must be a boolean.");
    }

    private static long ReadLong(IConfigurationSection section, string key, long defaultValue = 0)
    {
        var raw = section[key];
        if (raw is null)
        {
            return defaultValue;
        }

        return long.TryParse(
            raw,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
                ? value
                : throw new InvalidOperationException($"{section.Path}:{key} must be an integer.");
    }

    private static string ReadString(IConfigurationSection section, string key, string defaultValue = "") =>
        section[key] ?? defaultValue;

    private static void Validate<TOptions>(TOptions options, ProviderOptionsValidator optionsValidator)
        where TOptions : class
    {
        var result = options switch
        {
            MinioOptions value => optionsValidator.Validate(Options.DefaultName, value),
            RedisOptions value => optionsValidator.Validate(Options.DefaultName, value),
            NatsOptions value => optionsValidator.Validate(Options.DefaultName, value),
            FcmOptions value => optionsValidator.Validate(Options.DefaultName, value),
            QdrantOptions value => optionsValidator.Validate(Options.DefaultName, value),
            MetaProviderOptions value => optionsValidator.Validate(Options.DefaultName, value),
            FederationOptions value => optionsValidator.Validate(Options.DefaultName, value),
            SwiftAidDatabaseOptions value => optionsValidator.Validate(Options.DefaultName, value),
            _ => throw new NotSupportedException("The provider options type is not registered.")
        };

        if (result.Failed)
        {
            throw new OptionsValidationException(typeof(TOptions).Name, typeof(TOptions), result.Failures);
        }
    }
}
