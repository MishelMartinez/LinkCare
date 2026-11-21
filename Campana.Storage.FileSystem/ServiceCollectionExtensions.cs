using Campana.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Campana.Storage.FileSystem;

public static class ServiceCollectionExtensions
{
    /// <summary>Registra el almacenamiento sobre el filesystem (Abstract Factory: la UI y la API solo ven las interfaces).</summary>
    public static IServiceCollection AddFileSystemStorage(this IServiceCollection services, FileSystemOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(sp => new FsContext(options, sp.GetService<IClock>(), sp.GetService<IDiskSpaceProbe>(), sp.GetService<IFaultInjector>()));
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IDiskSpaceProbe>(_ => new DriveInfoProbe(Path.GetFullPath(options.DataRoot)));
        services.AddSingleton<DataRootHost>();
        services.AddSingleton<ISchemaSource>(sp => new DirectorySchemaSource(sp.GetRequiredService<FsContext>().Paths.SchemasDir));
        services.AddSingleton<ISchemaValidator>(sp => new JsonSchemaValidator(sp.GetRequiredService<ISchemaSource>()));
        services.AddSingleton<FileSystemCampaignStore>();
        services.AddSingleton<ICampaignStore>(sp => sp.GetRequiredService<FileSystemCampaignStore>());
        services.AddSingleton<FileSystemPatientStore>();
        services.AddSingleton<IPatientStore>(sp => sp.GetRequiredService<FileSystemPatientStore>());
        services.AddSingleton<FileSystemEventStore>();
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<FileSystemEventStore>());
        services.AddSingleton<IAttachmentStore, FileSystemAttachmentStore>();
        services.AddSingleton<ICampaignDocumentStore, FileSystemCampaignDocumentStore>();
        services.AddSingleton<IStatsService, FileSystemStatsService>();
        services.AddSingleton<IMinicubeBuilder, FileSystemMinicubeBuilder>();
        services.AddSingleton<IFileShareSync, FileSystemFileShareSync>();
        services.AddSingleton<FileSystemRebuildService>();
        services.AddSingleton<IRebuildService>(sp => sp.GetRequiredService<FileSystemRebuildService>());
        services.AddSingleton<IConsistencyService, FileSystemConsistencyService>();
        services.AddSingleton<IHistoryImporter, FileSystemHistoryImporter>();
        services.AddSingleton<IAuditLog, FileSystemAuditLog>();
        return services;
    }
}
