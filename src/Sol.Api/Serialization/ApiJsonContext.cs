using System.Text.Json.Serialization;
using Sol.Application.Contracts.Agent;
using Sol.Application.Contracts.Ai;
using Sol.Application.Contracts.Device;
using Sol.Application.Contracts.Realtime;
using Sol.Application.Contracts.Skill;
using Sol.Api.Endpoints;

namespace Sol.Api.Serialization;

/// <summary>
/// Source-generated JSON metadata for everything that crosses the HTTP or SignalR boundary.
/// </summary>
/// <remarks>
/// Under Native AOT the reflection-based serializer is unavailable, so any type sent or received
/// without an entry here throws at runtime. Registered on both the HTTP pipeline and the SignalR
/// JSON hub protocol via <c>TypeInfoResolverChain.Insert(0, …)</c>.
/// <para>
/// Infrastructure keeps its own context (<c>IntegrationJsonContext</c>) for broker and cache
/// payloads, because it cannot reference this assembly without inverting the layering.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceSignalsPayload))]
[JsonSerializable(typeof(DeviceHandshakeResponse))]
[JsonSerializable(typeof(StableSignals))]
[JsonSerializable(typeof(VolatileSignals))]
[JsonSerializable(typeof(ScreenSignals))]
[JsonSerializable(typeof(GpuSignals))]
[JsonSerializable(typeof(RealtimeEnvelope))]
[JsonSerializable(typeof(HealthReport))]
[JsonSerializable(typeof(HealthEntry))]
[JsonSerializable(typeof(SystemInfoResponse))]
[JsonSerializable(typeof(ErrorResponse))]
// AI configuration. Every type crossing the HTTP boundary needs an entry: under Native AOT the
// reflection-based serializer is gone, so an unregistered type throws at request time rather
// than failing the build.
[JsonSerializable(typeof(ProviderListResponse))]
[JsonSerializable(typeof(ProviderResponse))]
[JsonSerializable(typeof(ModelResponse))]
[JsonSerializable(typeof(CreateProviderRequest))]
[JsonSerializable(typeof(UpdateProviderRequest))]
[JsonSerializable(typeof(CreateModelRequest))]
[JsonSerializable(typeof(UpdateModelRequest))]
[JsonSerializable(typeof(ProviderCheckResponse))]
[JsonSerializable(typeof(DiscoveredModel))]
[JsonSerializable(typeof(DiscoveredModelsResponse))]
[JsonSerializable(typeof(ImportModelsRequest))]
[JsonSerializable(typeof(ImportModelsResponse))]
[JsonSerializable(typeof(ImportConfigRequest))]
[JsonSerializable(typeof(ImportProviderEntry))]
[JsonSerializable(typeof(ImportConfigResponse))]
[JsonSerializable(typeof(McpServerListResponse))]
[JsonSerializable(typeof(McpServerResponse))]
[JsonSerializable(typeof(McpEnvEntry))]
[JsonSerializable(typeof(McpSecretEntry))]
[JsonSerializable(typeof(CreateMcpServerRequest))]
[JsonSerializable(typeof(UpdateMcpServerRequest))]
[JsonSerializable(typeof(ImportMcpServersRequest))]
[JsonSerializable(typeof(ImportMcpServersResponse))]
[JsonSerializable(typeof(McpServerCheckResponse))]
[JsonSerializable(typeof(McpToolResponse))]
[JsonSerializable(typeof(McpToolListResponse))]
// Generation.
[JsonSerializable(typeof(GenerateImageRequest))]
[JsonSerializable(typeof(GenerateImageResponse))]
[JsonSerializable(typeof(GeneratedAsset))]
[JsonSerializable(typeof(StartVideoRequest))]
[JsonSerializable(typeof(StartVideoResponse))]
[JsonSerializable(typeof(VideoJobStatusResponse))]
[JsonSerializable(typeof(GenerateTextRequest))]
[JsonSerializable(typeof(GenerateTextResponse))]
[JsonSerializable(typeof(CanvasAssetSummary))]
[JsonSerializable(typeof(CanvasAssetListResponse))]
[JsonSerializable(typeof(CanvasAssetGroupSummary))]
[JsonSerializable(typeof(CanvasAssetGroupListResponse))]
[JsonSerializable(typeof(CreateCanvasAssetGroupRequest))]
[JsonSerializable(typeof(UpdateCanvasAssetGroupRequest))]
[JsonSerializable(typeof(AssignCanvasAssetsRequest))]
[JsonSerializable(typeof(AssignCanvasAssetsResponse))]
// Canvas documents. The graph itself is a JsonNode passed through verbatim.
[JsonSerializable(typeof(CanvasResponse))]
[JsonSerializable(typeof(CanvasSummaryResponse))]
[JsonSerializable(typeof(CanvasListResponse))]
[JsonSerializable(typeof(CreateCanvasRequest))]
[JsonSerializable(typeof(UpdateCanvasRequest))]
// Durable canvas Agent.
[JsonSerializable(typeof(CreateAgentSessionRequest))]
[JsonSerializable(typeof(AgentSessionResponse))]
[JsonSerializable(typeof(AgentSessionListResponse))]
[JsonSerializable(typeof(CreateAgentRunRequest))]
[JsonSerializable(typeof(AgentRunResponse))]
[JsonSerializable(typeof(AgentMessageResponse))]
[JsonSerializable(typeof(AgentMessageListResponse))]
[JsonSerializable(typeof(AgentEventResponse))]
[JsonSerializable(typeof(AgentEventListResponse))]
[JsonSerializable(typeof(AgentEventEnvelope))]
[JsonSerializable(typeof(AgentToolCallEnvelope))]
[JsonSerializable(typeof(AgentApprovalEnvelope))]
[JsonSerializable(typeof(AgentToolCatalogEntryResponse))]
[JsonSerializable(typeof(AgentToolCatalogResponse))]
// Uploaded Agent Skills.
[JsonSerializable(typeof(SkillRiskFindingResponse))]
[JsonSerializable(typeof(SkillScanResponse))]
[JsonSerializable(typeof(SkillResponse))]
[JsonSerializable(typeof(SkillListResponse))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonNode))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
