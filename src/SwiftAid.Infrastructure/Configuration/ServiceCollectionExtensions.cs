using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using SwiftAid.Application.Identity;
using OpenIddict.EntityFrameworkCore;
using Minio;
using NATS.Client.Core;
using NATS.Net;
using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Attachments;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Correlation;
using SwiftAid.Application.Cases.Spatial;
using SwiftAid.Application.CommunityResearch;
using SwiftAid.Application.Demo;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Application.Jobs;
using SwiftAid.Application.Notifications;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Jobs;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Demo;
using SwiftAid.Infrastructure.Caching;
using SwiftAid.Infrastructure.Messaging;
using SwiftAid.Infrastructure.Notifications;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Notifications;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Registration;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.CommunityResearch;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;
using SwiftAid.Infrastructure.Persistence.Sessions;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Infrastructure.Storage;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Intake;
using SwiftAid.Infrastructure.Intake.Geography;
using StackExchange.Redis;
using SwiftAid.Domain.Intake;

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
        services.AddSingleton<IValidateOptions<JobPublisherOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<FcmOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<QdrantOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<MetaProviderOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<FederationOptions>>(optionsValidator);
        services.AddSingleton<IValidateOptions<IdentityServerOptions>>(optionsValidator);
        services.AddSingleton(transportSecurityOptions);
        services.AddSingleton<IOptions<TransportSecurityOptions>>(Options.Create(transportSecurityOptions));
        var minioOptions = RegisterOptions(services, ReadMinioOptions(configuration), optionsValidator);
        var redisOptions = RegisterOptions(services, ReadRedisOptions(configuration), optionsValidator);
        var natsOptions = RegisterOptions(services, ReadNatsOptions(configuration), optionsValidator);
        _ = RegisterOptions(services, ReadJobPublisherOptions(configuration), optionsValidator);
        var fcmOptions = RegisterOptions(services, ReadFcmOptions(configuration), optionsValidator);
        _ = RegisterOptions(services, ReadQdrantOptions(configuration), optionsValidator);
        _ = RegisterOptions(services, ReadMetaOptions(configuration), optionsValidator);
        var federationOptions = RegisterOptions(services, ReadFederationOptions(configuration), optionsValidator);
        if (federationOptions.FacebookWebLogin.Enabled && !redisOptions.Enabled)
        {
            throw new OptionsValidationException(
                nameof(FederationOptions),
                typeof(FederationOptions),
                ["Federation:FacebookWebLogin requires Redis:Enabled for shared rate limiting."]);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFacebookWebTokenValidator, FacebookWebTokenValidator>();
        services.TryAddSingleton<IFacebookWebLoginRateLimiter, UnavailableFacebookWebLoginRateLimiter>();
        var identityServerOptions = RegisterOptions(
            services,
            ReadIdentityServerOptions(configuration),
            optionsValidator);
        var intakeLimits = ReadIntakeLimits(configuration);
        intakeLimits.Validate();
        services.TryAdd(ServiceDescriptor.Singleton<IntakeLimits>(intakeLimits));
        services.TryAddScoped<IIntakeSubmissionValidator, IntakeSubmissionValidator>();
        services.TryAddScoped<ICoverageRouter, CoverageRouter>();
        services.TryAddScoped<IIntakeService, IntakeService>();
        services.TryAddScoped<IIntakeSessionEndpointService, IntakeSessionEndpointService>();
        services.TryAddScoped<IIntakeCountryCatalogue, IntakeCountryCatalogue>();
        services.TryAddScoped<IGeographicRegistryImporter, GeographicRegistryImporter>();
        services.TryAddScoped<IGeographicRegistry, GeographicRegistryService>();
        services.TryAddScoped<IIntakeCoverageCatalog, PostgresIntakeCoverageCatalog>();
        services.TryAddScoped<IIntakeSubmissionStore, PostgresIntakeSubmissionStore>();
        services.TryAddScoped<IIntakeSessionAuthority, PostgresIntakeSessionAuthority>();
        var intakeConsentOptions = ReadIntakeConsentOptions(configuration);
        services.TryAddSingleton(intakeConsentOptions);
        services.TryAddSingleton<IOptions<IntakeConsentOptions>>(Options.Create(intakeConsentOptions));
        services.TryAddScoped<IIntakeConsentPolicy, ConfiguredIntakeConsentPolicy>();
        services.TryAddScoped<IIntakeReceiptReader, PostgresIntakeReceiptReader>();
        services.AddScoped<IIntakeDecisionStore, PostgresIntakeDecisionStore>();
        services.AddScoped<IIntakeReviewAuthorizer, PostgresIntakeReviewAuthorizer>();
        services.AddScoped<IIntakeAttachmentAuthority, PostgresIntakeAttachmentAuthority>();
        services.AddScoped<IIntakePromotionSource, PostgresIntakePromotionSource>();
        services.AddScoped<IIntakeSubmissionOpener, PostgresIntakeSubmissionOpener>();
        services.AddScoped<IIntakePromotionWorkspaceResolver, PostgresIntakePromotionWorkspaceResolver>();
        services.AddScoped<IIntakePromotionService, IntakePromotionProcessor>();
        services.AddScoped<IManagedCaseRegistrationProcessor, CaseRegistrationProcessor>();
        services.AddScoped<SwiftAid.Application.Intake.Imports.ICaseImportStore, SwiftAid.Infrastructure.Persistence.IntakeImports.PostgresCaseImportStore>();
        services.AddScoped<SwiftAid.Application.Intake.Imports.ICaseImportWorkspaceReader, SwiftAid.Infrastructure.Persistence.IntakeImports.PostgresCaseImportStore>();
        services.AddScoped<SwiftAid.Application.Intake.Imports.CaseCsvImportService>();
        var intakeCoordinationOptions = ReadIntakeCoordinationOptions(configuration);
        intakeCoordinationOptions.Validate();
        services.TryAddSingleton(intakeCoordinationOptions);
        services.AddScoped<IIntakeCoordinationAuthorizer, PostgresIntakeCoordinationAuthorizer>();
        services.AddScoped<IIntakeCoordinationStore, PostgresIntakeCoordinationStore>();
        services.AddScoped<IIntakeCoordinationService, IntakeCoordinationProcessor>();
        var intakeMetadataOptions = ReadIntakeMetadataOptions(configuration);
        intakeMetadataOptions.Validate();
        services.TryAddSingleton(intakeMetadataOptions);
        services.TryAddSingleton<IIntakePublicMetadataProvider>(
            _ => new IntakePublicMetadataProvider(intakeMetadataOptions));
        var demoDataOptions = ReadDemoDataOptions(configuration);
        demoDataOptions.Validate();
        services.TryAddSingleton(demoDataOptions);
        services.AddScoped<IDemoCaseImporter, DemoCaseImporter>();
        if (demoDataOptions.Enabled)
        {
            services.AddHostedService<DemoDataStartupImporter>();
        }
        _ = RegisterOptions(services, new SwiftAidDatabaseOptions
        {
            ConnectionString = configuration.GetConnectionString("SwiftAid") ?? string.Empty
        }, optionsValidator);

        var identityEnabled = federationOptions.Enabled || identityServerOptions.Enabled;
        if (identityServerOptions.Enabled && !federationOptions.Enabled)
        {
            throw new OptionsValidationException(
                nameof(IdentityServerOptions),
                typeof(IdentityServerOptions),
                ["Identity:Enabled requires at least one configured Federation provider."]);
        }

        if (identityServerOptions.Enabled &&
            !(federationOptions.Google.Enabled && federationOptions.Google.CallbackVerified) &&
            !(federationOptions.Facebook.Enabled && federationOptions.Facebook.CallbackVerified) &&
            federationOptions.OrganizationProviders.All(provider => !provider.CallbackVerified))
        {
            throw new OptionsValidationException(
                nameof(IdentityServerOptions),
                typeof(IdentityServerOptions),
                ["Identity:Enabled requires a Federation provider whose registration and callback checks are complete."]);
        }

        RegisterCaseCommandProcessing(services, configuration, hostEnvironment, identityEnabled);
        if (identityEnabled)
        {
            RegisterIdentity(
                services,
                configuration,
                federationOptions,
                identityServerOptions,
                hostEnvironment,
                optionsValidator);
        }

        services.AddScoped<INotificationResourceAuthorizer, NotificationResourceAuthorizer>();
        services.AddScoped<PostgresNotificationStore>();
        services.AddScoped<INotificationItemWriter>(provider =>
            provider.GetRequiredService<PostgresNotificationStore>());
        services.AddScoped<INotificationItemQuery>(provider =>
            provider.GetRequiredService<PostgresNotificationStore>());
        services.AddScoped<IFollowUpReminderScheduleStore, PostgresFollowUpReminderScheduleStore>();
        services.AddScoped<FollowUpReminderDispatcher>();
        services.AddScoped<INotificationDeliveryOutbox, PostgresNotificationDeliveryOutbox>();
        if (identityEnabled)
        {
            services.AddScoped<IPushDeviceStore, PostgresPushDeviceStore>();
        }
        else
        {
            services.AddScoped<IPushDeviceStore, NotConfiguredPushDeviceStore>();
        }
        services.AddScoped<ITrustedNotificationRecipientAuthorizer, PostgresTrustedNotificationRecipientAuthorizer>();

        if (minioOptions.Enabled)
        {
            var endpoint = new Uri(minioOptions.Endpoint, UriKind.Absolute);
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(endpoint.Host, endpoint.Port)
                .WithCredentials(minioOptions.AccessKey, minioOptions.SecretKey)
                .WithSSL(minioOptions.UseSsl)
                .Build());
            services.AddSingleton<IObjectStorage, MinioObjectStorage>();
            services.AddSingleton<IPrivateMediaObjectStore, MinioPrivateMediaObjectStore>();
        }
        else
        {
            services.AddSingleton<IObjectStorage, NotConfiguredObjectStorage>();
        }

        if (redisOptions.Enabled)
        {
            services.AddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(redisOptions.ConnectionString));

            if (federationOptions.FacebookWebLogin.Enabled)
            {
                services.Replace(ServiceDescriptor.Singleton<
                    IFacebookWebLoginRateLimiter,
                    RedisFacebookWebLoginRateLimiter>());
            }

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
            services.AddScoped<IIngestionOutbox, PostgresIngestionOutbox>();
            services.AddScoped<IngestionDispatchService>();
            services.AddSingleton<IIngestionNoticePublisher, JetStreamIngestionNoticePublisher>();
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
            services.AddSingleton<IJobPublisher, JetStreamPublisher>();
        }
        else
        {
            services.AddSingleton<IEventPublisher, NotConfiguredEventPublisher>();
            services.AddSingleton<IInAppNotificationPublisher, NotConfiguredInAppNotificationPublisher>();
            services.AddSingleton<IJobPublisher, NotConfiguredJobPublisher>();
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

    private static void RegisterCaseCommandProcessing(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? hostEnvironment,
        bool identityEnabled)
    {
        var section = configuration.GetSection(CaseCommandProcessingServiceOptions.SectionName);
        var commandOptions = new CaseCommandProcessingServiceOptions
        {
            Enabled = ReadBool(section, nameof(CaseCommandProcessingServiceOptions.Enabled)),
            MaximumTransactionAttempts = ReadInt32(
                section,
                nameof(CaseCommandProcessingServiceOptions.MaximumTransactionAttempts),
                3)
        };
        commandOptions.Validate();

        var applicationOptions = new CaseCommandProcessingOptions
        {
            MaximumTransactionAttempts = commandOptions.MaximumTransactionAttempts
        };
        applicationOptions.Validate();

        var protectionOptions = commandOptions.Enabled
            ? ReadProtectedPayloadOptions(configuration)
            : new ProtectedPayloadOptions();

        services.AddSingleton(commandOptions);
        services.AddSingleton<IOptions<CaseCommandProcessingServiceOptions>>(Options.Create(commandOptions));
        services.AddSingleton(applicationOptions);
        services.AddSingleton<IOptions<CaseCommandProcessingOptions>>(Options.Create(applicationOptions));
        services.AddSingleton(protectionOptions);
        services.AddSingleton<IOptions<ProtectedPayloadOptions>>(Options.Create(protectionOptions));

        if (!commandOptions.Enabled)
        {
            return;
        }

        var connectionString = configuration.GetConnectionString("SwiftAid");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new OptionsValidationException(
                nameof(SwiftAidDatabaseOptions),
                typeof(SwiftAidDatabaseOptions),
                ["ConnectionStrings:SwiftAid is required when CaseCommandProcessing is enabled."]);
        }

        if (!protectionOptions.Enabled)
        {
            throw new OptionsValidationException(
                nameof(ProtectedPayloadOptions),
                typeof(ProtectedPayloadOptions),
                ["DataProtection:Enabled must be true when CaseCommandProcessing is enabled."]);
        }

        var effectiveHostEnvironment = hostEnvironment ?? ProductionHostEnvironment.Instance;
        services.TryAddSingleton<IKeyRingSecurityInspector, FileSystemKeyRingSecurityInspector>();
        services.TryAddSingleton<IKeyRingSecurityValidator, KeyRingSecurityValidator>();
        services.AddSingleton<IValidateOptions<ProtectedPayloadOptions>>(provider =>
            new ProtectedPayloadOptionsValidator(
                effectiveHostEnvironment,
                provider.GetRequiredService<IKeyRingSecurityValidator>()));

        if (!identityEnabled)
        {
            services.AddDbContext<SwiftAidDbContext>(options =>
            {
                options.UseNpgsql(connectionString);
                options.UseOpenIddict();
            });
        }
        services.AddScoped<PostgresCaseCommandUnitOfWork>();
        services.AddScoped<ICaseCommandUnitOfWork>(provider =>
            provider.GetRequiredService<PostgresCaseCommandUnitOfWork>());
        services.AddScoped<ICaseCommandLeaseAccessor>(provider =>
            provider.GetRequiredService<PostgresCaseCommandUnitOfWork>());
        services.AddScoped<PostgresCaseRegistrationUnitOfWork>();
        services.AddScoped<ICaseRegistrationUnitOfWork>(provider =>
            provider.GetRequiredService<PostgresCaseRegistrationUnitOfWork>());
        services.AddScoped<ICaseRegistrationPersistence, PostgresCaseRegistrationPersistence>();
        services.AddScoped<ICaseRegistrationAuthorization, PostgresCaseRegistrationAuthorization>();
        services.AddScoped<ICurrentAccessGrantStore, PostgresCurrentAccessGrantStore>();
        services.AddScoped<ICaseAccessDescriptorStore, PostgresCaseAccessDescriptorStore>();
        services.AddScoped<ICaseAccessPolicy, CaseAccessPolicy>();
        services.AddSingleton<CaseEventSerializer>();
        services.AddSingleton<ProtectedPayloadDataProtectionProvider>(provider =>
            ProtectedPayloadDataProtectionProviderFactory.Create(
                provider.GetRequiredService<ProtectedPayloadOptions>(),
                effectiveHostEnvironment,
                provider.GetRequiredService<IKeyRingSecurityValidator>()));
        services.Replace(ServiceDescriptor.Singleton<IDataProtectionProvider>(provider =>
            provider.GetRequiredService<ProtectedPayloadDataProtectionProvider>()));
        services.AddScoped<PostgresProtectedCaseEventPayloadStore>();
        services.AddScoped<IProtectedCaseEventPayloadStore>(provider =>
            provider.GetRequiredService<PostgresProtectedCaseEventPayloadStore>());
        services.AddScoped<ICaseCommandPersistence, PostgresCaseCommandPersistence>();
        services.AddScoped<ICaseCommandProcessor, CaseCommandProcessor>();
        services.AddScoped<ICaseTimelineStore, PostgresCaseTimelineStore>();
        services.AddScoped<ICaseCorrelationStore, PostgresCaseCorrelationStore>();
        services.AddScoped<CaseCorrelationQuery>();
        services.AddScoped<ICommunityFeedbackAggregateSubmission, PostgresCommunityFeedbackAggregateSubmission>();
        services.AddScoped<ICaseCorrelationDecisionService, PostgresCaseCorrelationDecisionService>();
        services.AddScoped<ICaseTimelineQuery, CaseTimelineQuery>();
        services.AddScoped<IFollowUpRecommendationReviewService, PostgresFollowUpRecommendationReviewService>();
        services.AddScoped<IOrganizationSuggestionDecisionService, PostgresOrganizationSuggestionDecisionService>();
        services.Configure<HealthOfficerDirectoryOptions>(configuration.GetSection("Ellai:Operational:OfficerDirectory"));
        services.AddScoped<IEligibleHealthOfficerStore, PostgresEligibleHealthOfficerStore>();
        services.AddScoped<IHealthOfficerEscalationReviewStore, PostgresHealthOfficerEscalationReviewStore>();
        services.AddScoped<IHealthOfficerEscalationReviewQuery>(provider => new HealthOfficerEscalationReviewQuery(
            provider.GetRequiredService<ICaseCommandUnitOfWork>(), provider.GetRequiredService<ICaseAccessPolicy>(),
            provider.GetRequiredService<IHealthOfficerEscalationReviewStore>(), provider.GetRequiredService<IEligibleHealthOfficerStore>(),
            provider.GetRequiredService<IOptions<HealthOfficerDirectoryOptions>>().Value));
        services.AddScoped<IHealthOfficerDesignationAdministration>(provider => new PostgresHealthOfficerDesignationAdministration(
            provider.GetRequiredService<SwiftAidDbContext>(), provider.GetRequiredService<IOptions<HealthOfficerDirectoryOptions>>().Value));
        services.AddScoped<IHealthOfficerDesignationQuery>(provider => new PostgresHealthOfficerDesignationQuery(
            provider.GetRequiredService<SwiftAidDbContext>(), provider.GetRequiredService<IOptions<HealthOfficerDirectoryOptions>>().Value));
        services.AddScoped<IHealthOfficerEscalationApprovalService>(provider => new PostgresHealthOfficerEscalationApprovalService(
            provider.GetRequiredService<SwiftAidDbContext>(), provider.GetRequiredService<ICaseCommandUnitOfWork>(),
            provider.GetRequiredService<ICaseAccessPolicy>(), provider.GetRequiredService<IEligibleHealthOfficerStore>(),
            provider.GetRequiredService<IOptions<HealthOfficerDirectoryOptions>>().Value));
        services.AddScoped<IHealthOfficerEscalationAssignmentService>(provider => new PostgresHealthOfficerEscalationAssignmentService(
            provider.GetRequiredService<SwiftAidDbContext>(), provider.GetRequiredService<ICaseCommandUnitOfWork>(),
            provider.GetRequiredService<ICaseAccessPolicy>(), provider.GetRequiredService<ICaseCommandPersistence>(),
            provider.GetRequiredService<IEligibleHealthOfficerStore>(), provider.GetRequiredService<IOptions<HealthOfficerDirectoryOptions>>().Value));
        services.AddScoped<IEligibleHealthOfficerQuery>(provider => new EligibleHealthOfficerQuery(
            provider.GetRequiredService<IEligibleHealthOfficerStore>(), provider.GetRequiredService<ICaseAccessPolicy>(),
            provider.GetRequiredService<ICaseCommandUnitOfWork>(), provider.GetRequiredService<IOptions<HealthOfficerDirectoryOptions>>().Value));
        services.AddScoped<ICaseCollaboratorCandidatesStore, PostgresCaseCollaboratorCandidatesStore>();
        services.AddScoped<ICaseCollaboratorCandidatesQuery, CaseCollaboratorCandidatesQuery>();
        services.AddScoped<ICaseQueueStore, PostgresCaseQueueStore>();
        services.AddScoped<ICaseQueueQuery, CaseQueueQuery>();
        services.AddScoped<IMapAggregateStore, PostgresMapAggregateStore>();
        services.AddScoped<IMapAggregateQuery, SurveillanceMapAggregateQuery>();
        services.AddScoped<OwnedCaseSpatialAnalysis>();
        services.AddScoped<IDashboardAggregateStore, PostgresDashboardAggregateStore>();
        services.AddScoped<IDashboardAggregateQuery, DashboardAggregateQuery>();
        services.AddScoped<ICaseSummaryStore, PostgresCaseSummaryStore>();
        services.AddScoped<ICaseSummaryQuery, CaseSummaryQuery>();
        services.AddScoped<ICaseReportReadQuery, CaseReportReadQuery>();
        services.AddScoped<ICaseMapCandidateStore, PostgresCaseQueueStore>();
        services.AddScoped<ICaseCorrectionReadQuery, CaseCorrectionReadQuery>();
        services.AddScoped<IHomeOverviewStore, PostgresHomeOverviewStore>();
        services.AddScoped<IHomeOverviewQuery, HomeOverviewQuery>();
        services.AddTransient<IEllaiCaseAuthScopeFactory, EllaiCaseAuthScopeFactory>();
        services.AddScoped<ICaseMonitoringSummaryStore, PostgresCaseMonitoringSummaryStore>();
        services.AddScoped<ICaseMonitoringSummaryQuery, CaseMonitoringSummaryQuery>();
        services.AddScoped<ICaseMonitoringQueueStore, PostgresCaseMonitoringQueueStore>();
        services.AddScoped<ICaseMonitoringQueueQuery, CaseMonitoringQueueQuery>();
        // Community sessions ride the same pipeline flag: when the pipeline is disabled the
        // service stays unregistered and the staff/public session endpoints fail closed
        // through their nullable GetService checks (pinned by CommunitySessionEndpointTests).
        services.AddCommunitySessions();
        services.AddHostedService<CaseCommandProcessingStartupValidator>();
    }

    private static void RegisterIdentity(
        IServiceCollection services,
        IConfiguration configuration,
        FederationOptions federationOptions,
        IdentityServerOptions identityServerOptions,
        IHostEnvironment? hostEnvironment,
        ProviderOptionsValidator optionsValidator)
    {
        var connectionString = configuration.GetConnectionString("SwiftAid");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new OptionsValidationException(
                nameof(SwiftAidDatabaseOptions),
                typeof(SwiftAidDatabaseOptions),
                ["ConnectionStrings:SwiftAid is required when Federation or Identity is enabled."]);
        }

        var databaseOptions = new SwiftAidDatabaseOptions { ConnectionString = connectionString };
        Validate(databaseOptions, optionsValidator);

        RegisterIdentityDataProtection(services, configuration, hostEnvironment);

        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<SwiftAidDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
        });

        services.AddIdentityCore<SwiftAidUser>(identity =>
        {
            identity.User.RequireUniqueEmail = false;
            identity.SignIn.RequireConfirmedAccount = false;
            identity.Lockout.AllowedForNewUsers = true;
            identity.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            identity.Password.RequiredLength = 8;
            identity.Password.RequireDigit = true;
            identity.Password.RequireLowercase = true;
            identity.Password.RequireUppercase = false;
            identity.Password.RequireNonAlphanumeric = true;
            identity.Password.RequiredUniqueChars = 1;
        })
            .AddSignInManager()
            .AddEntityFrameworkStores<SwiftAidDbContext>()
            .AddDefaultTokenProviders();
        services.AddScoped<IUserClaimsPrincipalFactory<SwiftAidUser>, SwiftAidUserClaimsPrincipalFactory>();
        services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

        services.AddAuthorization();
        var allowInsecureDevelopmentCookies = hostEnvironment?.IsDevelopment() == true &&
            configuration.GetValue<bool>("Authentication:AllowInsecureDevelopmentCookies");
        var authCookieSecurePolicy = allowInsecureDevelopmentCookies
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        var authCookieName = allowInsecureDevelopmentCookies
            ? "SwiftAid.Auth.Dev"
            : "__Host-SwiftAid.Auth";
        var externalCookieName = allowInsecureDevelopmentCookies
            ? "SwiftAid.External.Dev"
            : "__Host-SwiftAid.External";
        var authentication = services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, cookie =>
            {
                cookie.Cookie.Name = authCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = authCookieSecurePolicy;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
                cookie.LoginPath = "/auth/providers";
                cookie.SlidingExpiration = false;
            })
            .AddCookie(IdentityConstants.ExternalScheme, cookie =>
            {
                cookie.Cookie.Name = externalCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = authCookieSecurePolicy;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                cookie.SlidingExpiration = false;
            });

        FederationSetup.AddConfiguredProviders(authentication, federationOptions);
        services.Configure<PublicSiteOptions>(configuration.GetSection("PublicSite"));
        services.Configure<SmtpEmailOptions>(configuration.GetSection("Smtp"));
        services.AddScoped<SmtpEmailSender>();
        services.AddScoped<IEmailSender<SwiftAidUser>, SmtpEmailSender>();
        services.AddScoped<IAccountVerificationMailer, SmtpEmailSender>();
        services.AddScoped<IPasswordIdentityRegistrar, PasswordIdentityRegistrar>();
        services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        services.AddScoped<IUserProfileService, UserProfileService>();
        services.AddScoped<IProfileAvatarService, ProfileAvatarService>();
        services.AddScoped<IOrganizationMembershipService, OrganizationMembershipService>();
        services.AddScoped<IOrganizationOwnerAuthorizer, PostgresOrganizationOwnerAuthorizer>();
        services.AddScoped<IOrganizationInvitationService, OrganizationInvitationService>();
        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();
        services.AddSingleton<OrganizationSsoRegistry>();
        services.AddScoped<IExternalIdentityMappingStore, EfExternalIdentityMappingStore>();
        services.AddScoped<IExternalIdentityResolver, ExternalIdentityResolver>();

        if (identityServerOptions.Enabled)
        {
            IdentitySetup.AddOpenIddict(services, identityServerOptions);
        }
    }

    private static void RegisterIdentityDataProtection(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? hostEnvironment)
    {
        if (!services.Any(descriptor =>
                descriptor.ServiceType == typeof(ProtectedPayloadDataProtectionProvider)))
        {
            var options = ReadProtectedPayloadOptions(configuration);
            if (!options.Enabled)
            {
                throw new OptionsValidationException(
                    nameof(ProtectedPayloadOptions),
                    typeof(ProtectedPayloadOptions),
                    ["DataProtection:Enabled must be true when Federation or Identity is enabled."]);
            }

            var effectiveEnvironment = hostEnvironment ?? ProductionHostEnvironment.Instance;
            var inspector = new FileSystemKeyRingSecurityInspector();
            var keyRingValidator = new KeyRingSecurityValidator(inspector);
            services.TryAddSingleton<IKeyRingSecurityInspector>(inspector);
            services.TryAddSingleton<IKeyRingSecurityValidator>(keyRingValidator);
            var validator = new ProtectedPayloadOptionsValidator(effectiveEnvironment, keyRingValidator);
            var validation = validator.Validate(Options.DefaultName, options);
            if (validation.Failed)
            {
                throw new OptionsValidationException(
                    nameof(ProtectedPayloadOptions),
                    typeof(ProtectedPayloadOptions),
                    validation.Failures);
            }

            services.AddSingleton(options);
            services.AddSingleton<IOptions<ProtectedPayloadOptions>>(Options.Create(options));
            services.AddSingleton<IValidateOptions<ProtectedPayloadOptions>>(validator);
            services.AddSingleton(ProtectedPayloadDataProtectionProviderFactory.Create(
                options,
                effectiveEnvironment,
                keyRingValidator));
        }

        services.Replace(ServiceDescriptor.Singleton<IDataProtectionProvider>(provider =>
            provider.GetRequiredService<ProtectedPayloadDataProtectionProvider>()));
    }

    private static ProtectedPayloadOptions ReadProtectedPayloadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(ProtectedPayloadOptions.SectionName);
        return new ProtectedPayloadOptions
        {
            Enabled = ReadBool(section, nameof(ProtectedPayloadOptions.Enabled)),
            ApplicationName = ReadString(section, nameof(ProtectedPayloadOptions.ApplicationName)),
            KeysPath = ReadString(section, nameof(ProtectedPayloadOptions.KeysPath)),
            CertificatePath = ReadString(section, nameof(ProtectedPayloadOptions.CertificatePath)),
            CertificatePassword = ReadString(section, nameof(ProtectedPayloadOptions.CertificatePassword)),
            AllowUnencryptedDevelopmentKeys = ReadBool(
                section,
                nameof(ProtectedPayloadOptions.AllowUnencryptedDevelopmentKeys)),
            MaximumPayloadBytes = ReadLong(section, nameof(ProtectedPayloadOptions.MaximumPayloadBytes))
        };
    }

    private static int ReadInt32(IConfigurationSection section, string key, int defaultValue)
    {
        var raw = section[key];
        if (raw is null)
        {
            return defaultValue;
        }

        return int.TryParse(
            raw,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : throw new InvalidOperationException($"{section.Path}:{key} must be an integer.");
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
            IngestionDispatchEnabled = ReadBool(section, nameof(NatsOptions.IngestionDispatchEnabled)),
            Url = ReadString(section, nameof(NatsOptions.Url)),
            Username = ReadString(section, nameof(NatsOptions.Username)),
            Password = ReadString(section, nameof(NatsOptions.Password)),
            EventStream = ReadString(section, nameof(NatsOptions.EventStream)),
            EventSubjectPrefix = ReadString(section, nameof(NatsOptions.EventSubjectPrefix)),
            NotificationStream = ReadString(section, nameof(NatsOptions.NotificationStream)),
            NotificationSubject = ReadString(section, nameof(NatsOptions.NotificationSubject)),
            NotificationStreamMaxAgeSeconds = ReadInt32(section, nameof(NatsOptions.NotificationStreamMaxAgeSeconds), 86_400),
            NotificationStreamMaxBytes = ReadLong(section, nameof(NatsOptions.NotificationStreamMaxBytes), 67_108_864),
            NotificationStreamMaxMessages = ReadLong(section, nameof(NatsOptions.NotificationStreamMaxMessages), 100_000),
            NotificationStreamMaxMessageSizeBytes = ReadInt32(section, nameof(NatsOptions.NotificationStreamMaxMessageSizeBytes), 16_384),
            NotificationStreamReplicas = ReadInt32(section, nameof(NatsOptions.NotificationStreamReplicas), 1),
            NotificationStreamDuplicateWindowSeconds = ReadInt32(section, nameof(NatsOptions.NotificationStreamDuplicateWindowSeconds), 120),
            NotificationConsumerAckWaitSeconds = ReadInt32(
                section,
                nameof(NatsOptions.NotificationConsumerAckWaitSeconds),
                30),
            NotificationConsumerMaxDeliveries = ReadInt32(
                section,
                nameof(NatsOptions.NotificationConsumerMaxDeliveries),
                8),
            NotificationConsumerInactiveThresholdSeconds = ReadInt32(
                section,
                nameof(NatsOptions.NotificationConsumerInactiveThresholdSeconds),
                120)
        };
    }

    private static IntakeLimits ReadIntakeLimits(IConfiguration configuration)
    {
        var section = configuration.GetSection(IntakeLimits.SectionName);
        return new IntakeLimits(
            MaximumNarrativeCharacters: ReadInt32(section, nameof(IntakeLimits.MaximumNarrativeCharacters), 6_000),
            MaximumAudioAttachments: ReadInt32(section, nameof(IntakeLimits.MaximumAudioAttachments), 2),
            MaximumAudioDurationSeconds: ReadInt32(section, nameof(IntakeLimits.MaximumAudioDurationSeconds), 120),
            MaximumAudioBytes: ReadLong(section, nameof(IntakeLimits.MaximumAudioBytes), 10 * 1024 * 1024),
            MaximumSubmissionsPerCapabilityPerHour: ReadInt32(
                section,
                nameof(IntakeLimits.MaximumSubmissionsPerCapabilityPerHour),
                10),
            MaximumObservations: ReadInt32(section, nameof(IntakeLimits.MaximumObservations), 32),
            MaximumObservationCharacters: ReadInt32(section, nameof(IntakeLimits.MaximumObservationCharacters), 300));
    }

    private static IntakeMetadataOptions ReadIntakeMetadataOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(IntakeMetadataOptions.SectionName);
        return new IntakeMetadataOptions
        {
            CatalogueVersion = ReadString(section, nameof(IntakeMetadataOptions.CatalogueVersion), "2026-10-27.1"),
            CatalogueReference = ReadString(section, nameof(IntakeMetadataOptions.CatalogueReference), "/public/v1/intake/metadata/countries"),
            CatalogueSha256 = ReadString(section, nameof(IntakeMetadataOptions.CatalogueSha256), "10F08AC21834BB4ECA2BA77D38005D7191445D02E3A45921E625D6D6E0BB8570"),
            PrivacyNoticeVersion = ReadString(section, nameof(IntakeMetadataOptions.PrivacyNoticeVersion), "2026-10-27.1"),
            PrivacyNoticeReference = ReadString(section, nameof(IntakeMetadataOptions.PrivacyNoticeReference), "/privacy"),
            PrivacyNoticeSourceCommit = ReadString(section, nameof(IntakeMetadataOptions.PrivacyNoticeSourceCommit), "a9bc0e6d1703dd47e8b0f2e3f78ff6f0e6e8f461")
        };
    }

    private static IntakeCoordinationOptions ReadIntakeCoordinationOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(IntakeCoordinationOptions.SectionName);
        var enabledText = ReadString(section, nameof(IntakeCoordinationOptions.Enabled), "false");
        return new IntakeCoordinationOptions
        {
            Enabled = !string.Equals(enabledText, "false", StringComparison.OrdinalIgnoreCase),
            CoordinationOrganizationId = ReadString(section, nameof(IntakeCoordinationOptions.CoordinationOrganizationId)),
            DefaultPageLimit = ReadInt32(section, nameof(IntakeCoordinationOptions.DefaultPageLimit), 50),
            MaximumPageLimit = ReadInt32(section, nameof(IntakeCoordinationOptions.MaximumPageLimit), 200)
        };
    }

    private static IntakeConsentOptions ReadIntakeConsentOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(IntakeConsentOptions.SectionName);
        return new IntakeConsentOptions
        {
            ProcessingPolicyReference = ReadString(section, nameof(IntakeConsentOptions.ProcessingPolicyReference)),
            ProcessingPolicyVersion = ReadString(section, nameof(IntakeConsentOptions.ProcessingPolicyVersion)),
            RecordingPolicyReference = ReadString(section, nameof(IntakeConsentOptions.RecordingPolicyReference)),
            RecordingPolicyVersion = ReadString(section, nameof(IntakeConsentOptions.RecordingPolicyVersion))
        };
    }

    private static JobPublisherOptions ReadJobPublisherOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(JobPublisherOptions.SectionName);
        return new JobPublisherOptions
        {
            StreamName = ReadString(
                section,
                nameof(JobPublisherOptions.StreamName),
                JobPublisherOptions.ExpectedStreamName),
            MaximumMessageBytes = ReadInt32(
                section,
                nameof(JobPublisherOptions.MaximumMessageBytes),
                JobEnvelope.MaximumMessageBytes)
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
            ClientSecret = ReadString(section, nameof(FederationOptions.ClientSecret)),
            Google = ReadSocialProviderOptions(section.GetSection(nameof(FederationOptions.Google))),
            Facebook = ReadSocialProviderOptions(section.GetSection(nameof(FederationOptions.Facebook))),
            FacebookWebLogin = ReadFacebookWebLoginSettings(
                section.GetSection(nameof(FederationOptions.FacebookWebLogin))),
            OrganizationProviders = section.GetSection(nameof(FederationOptions.OrganizationProviders))
                .GetChildren()
                .Select(ReadOrganizationProviderOptions)
                .ToList()
        };
    }

    private static SocialProviderOptions ReadSocialProviderOptions(IConfigurationSection section) => new()
    {
        Enabled = ReadBool(section, nameof(SocialProviderOptions.Enabled)),
        CallbackVerified = ReadBool(section, nameof(SocialProviderOptions.CallbackVerified)),
        CallbackPath = ReadString(
            section,
            nameof(SocialProviderOptions.CallbackPath),
            "/google/callback"),
        ClientId = ReadString(section, nameof(SocialProviderOptions.ClientId)),
        ClientSecret = ReadString(section, nameof(SocialProviderOptions.ClientSecret))
    };

    private static FacebookWebLoginSettings ReadFacebookWebLoginSettings(IConfigurationSection section) => new()
    {
        Enabled = ReadBool(section, nameof(FacebookWebLoginSettings.Enabled)),
        RateLimitHmacKeyBase64 = ReadString(section, nameof(FacebookWebLoginSettings.RateLimitHmacKeyBase64)),
        GraphApiVersion = ReadString(section, nameof(FacebookWebLoginSettings.GraphApiVersion)),
        MaximumRequestBytes = ReadInt32(
            section, nameof(FacebookWebLoginSettings.MaximumRequestBytes), 4096),
        MaximumResponseBytes = ReadInt32(
            section, nameof(FacebookWebLoginSettings.MaximumResponseBytes), 16384),
        OutboundTimeoutSeconds = ReadInt32(
            section, nameof(FacebookWebLoginSettings.OutboundTimeoutSeconds), 5),
        MaximumConcurrentValidations = ReadInt32(
            section, nameof(FacebookWebLoginSettings.MaximumConcurrentValidations), 10),
        MaximumAttempts = ReadInt32(
            section, nameof(FacebookWebLoginSettings.MaximumAttempts), 10),
        RateLimitWindowSeconds = ReadInt32(
            section, nameof(FacebookWebLoginSettings.RateLimitWindowSeconds), 60)
    };

    private static OrganizationSsoProviderOptions ReadOrganizationProviderOptions(IConfigurationSection section) => new()
    {
        ProviderId = ReadString(section, nameof(OrganizationSsoProviderOptions.ProviderId)),
        DisplayName = ReadString(section, nameof(OrganizationSsoProviderOptions.DisplayName)),
        CallbackVerified = ReadBool(section, nameof(OrganizationSsoProviderOptions.CallbackVerified)),
        Authority = ReadString(section, nameof(OrganizationSsoProviderOptions.Authority)),
        Issuer = ReadString(section, nameof(OrganizationSsoProviderOptions.Issuer)),
        ClientId = ReadString(section, nameof(OrganizationSsoProviderOptions.ClientId)),
        ClientSecret = ReadString(section, nameof(OrganizationSsoProviderOptions.ClientSecret)),
        TenantClaimType = ReadString(section, nameof(OrganizationSsoProviderOptions.TenantClaimType)),
        TenantClaimValue = ReadString(section, nameof(OrganizationSsoProviderOptions.TenantClaimValue))
    };

    private static IdentityServerOptions ReadIdentityServerOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(IdentityServerOptions.SectionName);
        return new IdentityServerOptions
        {
            Enabled = ReadBool(section, nameof(IdentityServerOptions.Enabled)),
            Issuer = ReadString(section, nameof(IdentityServerOptions.Issuer)),
            SigningCertificatePath = ReadString(section, nameof(IdentityServerOptions.SigningCertificatePath)),
            SigningCertificatePassword = ReadString(section, nameof(IdentityServerOptions.SigningCertificatePassword)),
            EncryptionCertificatePath = ReadString(section, nameof(IdentityServerOptions.EncryptionCertificatePath)),
            EncryptionCertificatePassword = ReadString(section, nameof(IdentityServerOptions.EncryptionCertificatePassword)),
            AuthorizationCodeLifetimeSeconds = ReadInt32(section, nameof(IdentityServerOptions.AuthorizationCodeLifetimeSeconds), 120),
            AccessTokenLifetimeSeconds = ReadInt32(section, nameof(IdentityServerOptions.AccessTokenLifetimeSeconds), 600),
            RefreshTokenLifetimeSeconds = ReadInt32(section, nameof(IdentityServerOptions.RefreshTokenLifetimeSeconds), 2_592_000),
            PublicClients = section.GetSection(nameof(IdentityServerOptions.PublicClients))
                .GetChildren()
                .Select(client => new IdentityPublicClientOptions
                {
                    ClientId = ReadString(client, nameof(IdentityPublicClientOptions.ClientId)),
                    DisplayName = ReadString(client, nameof(IdentityPublicClientOptions.DisplayName)),
                    RedirectUris = ReadStringList(client.GetSection(nameof(IdentityPublicClientOptions.RedirectUris))),
                    PostLogoutRedirectUris = ReadStringList(client.GetSection(nameof(IdentityPublicClientOptions.PostLogoutRedirectUris)))
                })
                .ToList()
        };
    }

    private static List<string> ReadStringList(IConfigurationSection section) => section.GetChildren()
        .Select(child => child.Value ?? string.Empty)
        .ToList();

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

    private static DemoDataOptions ReadDemoDataOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(DemoDataOptions.SectionName);
        return new DemoDataOptions
        {
            Enabled = ReadBool(section, nameof(DemoDataOptions.Enabled)),
            MaxCases = ReadInt32(section, nameof(DemoDataOptions.MaxCases), 1000)
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
            JobPublisherOptions value => optionsValidator.Validate(Options.DefaultName, value),
            FcmOptions value => optionsValidator.Validate(Options.DefaultName, value),
            QdrantOptions value => optionsValidator.Validate(Options.DefaultName, value),
            MetaProviderOptions value => optionsValidator.Validate(Options.DefaultName, value),
            FederationOptions value => optionsValidator.Validate(Options.DefaultName, value),
            IdentityServerOptions value => optionsValidator.Validate(Options.DefaultName, value),
            SwiftAidDatabaseOptions value => optionsValidator.Validate(Options.DefaultName, value),
            _ => throw new NotSupportedException("The provider options type is not registered.")
        };

        if (result.Failed)
        {
            throw new OptionsValidationException(typeof(TOptions).Name, typeof(TOptions), result.Failures);
        }
    }

    private sealed class ProductionHostEnvironment : IHostEnvironment
    {
        public static ProductionHostEnvironment Instance { get; } = new();

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "SwiftAid";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NotConfiguredJobPublisher : IJobPublisher
    {
        public ValueTask<PublishReceipt> PublishAsync(
            JobEnvelope envelope,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            return ValueTask.FromResult(cancellationToken.IsCancellationRequested
                ? new PublishReceipt(JobPublishStatus.Cancelled)
                : new PublishReceipt(
                    JobPublishStatus.Unavailable,
                    FailureCode: "job-publisher-not-configured"));
        }
    }
}
