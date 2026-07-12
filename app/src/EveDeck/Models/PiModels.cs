using System.Text.Json.Serialization;

namespace EveDeck.Models;

// ── Raw ESI DTOs (deserialized straight from the ESI JSON; snake_case matched via JsonPropertyName) ──

// GET /characters/{id}/planets/  — one entry per colony the character owns.
public sealed class EsiPlanetSummary
{
    [JsonPropertyName("planet_id")] public int PlanetId { get; set; }
    [JsonPropertyName("solar_system_id")] public int SolarSystemId { get; set; }
    [JsonPropertyName("planet_type")] public string PlanetType { get; set; } = "";
    [JsonPropertyName("upgrade_level")] public int UpgradeLevel { get; set; }
    [JsonPropertyName("num_pins")] public int NumPins { get; set; }
    [JsonPropertyName("last_update")] public DateTimeOffset LastUpdate { get; set; }
}

// GET /characters/{id}/planets/{planet_id}/  — the colony's pins/links/routes.
public sealed class EsiPlanetDetail
{
    [JsonPropertyName("pins")] public List<EsiPin> Pins { get; set; } = new();
    [JsonPropertyName("links")] public List<EsiLink> Links { get; set; } = new();
    [JsonPropertyName("routes")] public List<EsiRoute> Routes { get; set; } = new();
}

public sealed class EsiPin
{
    [JsonPropertyName("pin_id")] public long PinId { get; set; }
    [JsonPropertyName("type_id")] public int TypeId { get; set; }
    // Present only on factory pins.
    [JsonPropertyName("schematic_id")] public int? SchematicId { get; set; }
    // Present only on extractor control units (ECUs).
    [JsonPropertyName("extractor_details")] public EsiExtractorDetails? Extractor { get; set; }
    // Present on storage-capable pins (launchpad, storage facility, command center).
    [JsonPropertyName("contents")] public List<EsiPinContent>? Contents { get; set; }
    [JsonPropertyName("expiry_time")] public DateTimeOffset? ExpiryTime { get; set; }
    [JsonPropertyName("install_time")] public DateTimeOffset? InstallTime { get; set; }
    [JsonPropertyName("last_cycle_start")] public DateTimeOffset? LastCycleStart { get; set; }
}

public sealed class EsiExtractorDetails
{
    [JsonPropertyName("product_type_id")] public int? ProductTypeId { get; set; }
    [JsonPropertyName("cycle_time")] public int? CycleTime { get; set; }        // seconds
    [JsonPropertyName("qty_per_cycle")] public int? QtyPerCycle { get; set; }
    [JsonPropertyName("head_radius")] public float? HeadRadius { get; set; }
    [JsonPropertyName("heads")] public List<EsiExtractorHead>? Heads { get; set; }
}

public sealed class EsiExtractorHead
{
    [JsonPropertyName("head_id")] public int HeadId { get; set; }
}

public sealed class EsiPinContent
{
    [JsonPropertyName("type_id")] public int TypeId { get; set; }
    [JsonPropertyName("amount")] public long Amount { get; set; }
}

public sealed class EsiLink
{
    [JsonPropertyName("source_pin_id")] public long SourcePinId { get; set; }
    [JsonPropertyName("destination_pin_id")] public long DestinationPinId { get; set; }
}

public sealed class EsiRoute
{
    [JsonPropertyName("route_id")] public long RouteId { get; set; }
    [JsonPropertyName("source_pin_id")] public long SourcePinId { get; set; }
    [JsonPropertyName("destination_pin_id")] public long DestinationPinId { get; set; }
    [JsonPropertyName("content_type_id")] public int ContentTypeId { get; set; }
    [JsonPropertyName("quantity")] public float Quantity { get; set; }
}

// GET /characters/{id}/assets/  — used only to total P1 stock for the factory-load calculator.
public sealed class EsiAssetItem
{
    [JsonPropertyName("type_id")] public int TypeId { get; set; }
    [JsonPropertyName("quantity")] public long Quantity { get; set; }
    [JsonPropertyName("location_id")] public long LocationId { get; set; }
}

// GET /characters/{id}/skills/  — used only to read the Interplanetary Consolidation level shown on
// each colony card (requires esi-skills.read_skills.v1).
public sealed class EsiCharacterSkillsResponse
{
    [JsonPropertyName("skills")] public List<EsiSkill> Skills { get; set; } = new();
}

public sealed class EsiSkill
{
    [JsonPropertyName("skill_id")] public int SkillId { get; set; }
    [JsonPropertyName("trained_skill_level")] public int TrainedSkillLevel { get; set; }
}
