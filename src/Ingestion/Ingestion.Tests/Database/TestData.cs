using Ingestion.Core;
using Ingestion.Worker.Pipeline;

namespace Ingestion.Tests.Database;

/// <summary>Builders for batches and records used by the database tests.</summary>
internal static class TestData
{
    // Whole seconds, like OpenSky's time_position, and "now" so the row lands
    // in one of pg_partman's daily partitions rather than the default one.
    public static readonly DateTime Now = DateTime.UnixEpoch.AddSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public static StateVector Vector(string icao24, DateTime? observedAt = null) => new(
        Icao24: icao24,
        ObservedAt: observedAt ?? Now,
        LastContact: null,
        Callsign: "KLM1234",
        OriginCountry: "Netherlands",
        Latitude: 52.3,
        Longitude: 4.76,
        BaroAltitudeM: 10668f,
        GeoAltitudeM: null,
        VelocityMs: 230.5f,
        TrueTrackDeg: 90f,
        VerticalRateMs: 0f,
        OnGround: false,
        Squawk: null,
        PositionSource: 0);

    public static PollBatch Batch(params StateVector[] records) => new(
        RunId: Guid.CreateVersion7(),
        StartedAt: DateTimeOffset.UtcNow,
        DurationMs: 400,
        HttpStatus: 200,
        RecordsReceived: records.Length,
        Records: records,
        Retries: 0,
        CreditsRemaining: 3999,
        Error: null);

    public static PollBatch FailedBatch(string error) => new(
        RunId: Guid.CreateVersion7(),
        StartedAt: DateTimeOffset.UtcNow,
        DurationMs: 10000,
        HttpStatus: null,
        RecordsReceived: 0,
        Records: [],
        Retries: 3,
        CreditsRemaining: null,
        Error: error);
}
