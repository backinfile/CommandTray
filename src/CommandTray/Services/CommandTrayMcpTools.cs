using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CommandTray.Services;

public sealed class CommandTrayMcpTools(CommandTrayApi api)
{
    [McpServerTool(Name = "get_manager", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Get the CommandTray MCP endpoint and whether MCP is enabled in the saved configuration.")]
    public ManagerView GetManager() => api.GetManager();

    [McpServerTool(Name = "list_services", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("List every configured service with its complete non-secret configuration and current runtime state.")]
    public IReadOnlyList<ServiceView> ListServices() => api.ListServices();

    [McpServerTool(Name = "get_service", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Get one configured service and its current runtime state. Secret values are never returned.")]
    public ServiceView GetService([Description("Service GUID returned by list_services.")] string serviceId) =>
        api.GetService(serviceId);

    [McpServerTool(Name = "create_service", UseStructuredContent = true)]
    [Description("Create and persist a CommandTray service. The command is not started automatically by this operation.")]
    public Task<ServiceView> CreateService(
        [Description("Complete service configuration to create.")] ServiceInput service,
        CancellationToken cancellationToken) => api.CreateServiceAsync(service, cancellationToken);

    [McpServerTool(Name = "update_service", Destructive = true, UseStructuredContent = true)]
    [Description("Replace and persist a service configuration. For an existing secret, omit its value to preserve it.")]
    public Task<ServiceView> UpdateService(
        [Description("Service GUID returned by list_services.")] string serviceId,
        [Description("Complete replacement service configuration.")] ServiceInput service,
        CancellationToken cancellationToken) => api.UpdateServiceAsync(serviceId, service, cancellationToken);

    [McpServerTool(Name = "delete_service", Destructive = true, UseStructuredContent = true)]
    [Description("Delete a stopped or faulted service from CommandTray. Running services must be stopped first.")]
    public Task<OperationResult> DeleteService(
        [Description("Service GUID returned by list_services.")] string serviceId,
        CancellationToken cancellationToken) => api.DeleteServiceAsync(serviceId, cancellationToken);

    [McpServerTool(Name = "start_service", Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Start a configured service and return its resulting runtime state.")]
    public Task<ServiceView> StartService(
        [Description("Service GUID returned by list_services.")] string serviceId,
        CancellationToken cancellationToken) => api.StartServiceAsync(serviceId, cancellationToken);

    [McpServerTool(Name = "stop_service", Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Stop a configured service and its CommandTray-owned process tree.")]
    public Task<ServiceView> StopService(
        [Description("Service GUID returned by list_services.")] string serviceId,
        CancellationToken cancellationToken) => api.StopServiceAsync(serviceId, cancellationToken);

    [McpServerTool(Name = "restart_service", OpenWorld = true, UseStructuredContent = true)]
    [Description("Stop and start a configured service, then return its resulting runtime state.")]
    public Task<ServiceView> RestartService(
        [Description("Service GUID returned by list_services.")] string serviceId,
        CancellationToken cancellationToken) => api.RestartServiceAsync(serviceId, cancellationToken);

    [McpServerTool(Name = "get_service_log", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Read the newest lines from a service's rolling log. Log text may contain data written by the managed command.")]
    public LogView GetServiceLog(
        [Description("Service GUID returned by list_services.")] string serviceId,
        [Description("Number of newest lines to return, from 1 to 1000.")] int lines = 200) =>
        api.GetServiceLog(serviceId, lines);
}
