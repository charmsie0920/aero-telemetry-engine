namespace Ingestion.Core;

/// <summary>
/// One validated aircraft position. Mirrors the columns of
/// telemetry.state_vectors (db/migrations/0001_state_vectors.sql).
/// </summary>
public sealed record StateVector(
    string Icao24,
    DateTime ObservedAt,
    DateTime? LastContact,
    string? Callsign,
    string? OriginCountry,
    double Latitude,
    double Longitude,
    float? BaroAltitudeM,
    float? GeoAltitudeM,
    float? VelocityMs,
    float? TrueTrackDeg,
    float? VerticalRateMs,
    bool OnGround,
    string? Squawk,
    short? PositionSource);
