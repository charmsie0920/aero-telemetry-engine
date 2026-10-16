using System.Text.Json;

namespace Ingestion.Core;

/// <summary>Why a record from OpenSky was dropped (PRD 5.1).</summary>
public enum RejectReason
{
    /// <summary>Not an array, too short, or a field has the wrong JSON type.</summary>
    Malformed,

    /// <summary>icao24 is missing or not 6 hex characters.</summary>
    InvalidIcao24,

    /// <summary>time_position is null, so there is no time to file the row under.</summary>
    MissingTimestamp,

    /// <summary>Latitude or longitude is null.</summary>
    MissingPosition,

    /// <summary>Latitude or longitude is outside the valid range.</summary>
    OutOfRange,
}

/// <summary>The result of parsing one /states/all response.</summary>
public sealed record ParseResult(
    IReadOnlyList<StateVector> Valid,
    int Received,
    IReadOnlyDictionary<RejectReason, int> Rejected)
{
    public int Invalid => Received - Valid.Count;
}

/// <summary>
/// Turns OpenSky's /states/all response into typed, validated records.
/// </summary>
/// <remarks>
/// OpenSky sends each aircraft as a JSON array, not an object, so fields are
/// read by position. The index constants follow the OpenSky REST API docs.
/// Invalid records are dropped and counted rather than throwing, so one bad
/// record never loses the rest of the poll.
/// </remarks>
public static class OpenSkyStateParser
{
    private const int Icao24 = 0;
    private const int Callsign = 1;
    private const int OriginCountry = 2;
    private const int TimePosition = 3;
    private const int LastContact = 4;
    private const int Longitude = 5;
    private const int Latitude = 6;
    private const int BaroAltitude = 7;
    private const int OnGround = 8;
    private const int Velocity = 9;
    private const int TrueTrack = 10;
    private const int VerticalRate = 11;
    private const int GeoAltitude = 13;
    private const int Squawk = 14;
    private const int PositionSource = 16;

    private const int MinimumLength = PositionSource + 1;

    public static ParseResult Parse(JsonElement response)
    {
        var valid = new List<StateVector>();
        var rejected = new Dictionary<RejectReason, int>();
        var received = 0;

        // "states" is null, not an empty array, when no aircraft are in the box.
        if (response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("states", out var states)
            && states.ValueKind == JsonValueKind.Array)
        {
            foreach (var state in states.EnumerateArray())
            {
                received++;

                if (TryParse(state, out var vector, out var reason))
                {
                    valid.Add(vector);
                }
                else
                {
                    rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
                }
            }
        }

        return new ParseResult(valid, received, rejected);
    }

    public static bool TryParse(JsonElement state, out StateVector vector, out RejectReason reason)
    {
        vector = null!;

        try
        {
            if (state.ValueKind != JsonValueKind.Array || state.GetArrayLength() < MinimumLength)
            {
                reason = RejectReason.Malformed;
                return false;
            }

            var icao24 = state[Icao24].ValueKind == JsonValueKind.String
                ? state[Icao24].GetString()!.Trim().ToLowerInvariant()
                : null;
            if (icao24 is null || !IsIcao24(icao24))
            {
                reason = RejectReason.InvalidIcao24;
                return false;
            }

            var timePosition = NullableLong(state[TimePosition]);
            if (timePosition is null)
            {
                reason = RejectReason.MissingTimestamp;
                return false;
            }

            var latitude = NullableDouble(state[Latitude]);
            var longitude = NullableDouble(state[Longitude]);
            if (latitude is null || longitude is null)
            {
                reason = RejectReason.MissingPosition;
                return false;
            }

            if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            {
                reason = RejectReason.OutOfRange;
                return false;
            }

            var onGround = state[OnGround].ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException("on_ground is not a boolean."),
            };

            var lastContact = NullableLong(state[LastContact]);

            vector = new StateVector(
                Icao24: icao24,
                ObservedAt: DateTime.UnixEpoch.AddSeconds(timePosition.Value),
                LastContact: lastContact is null ? null : DateTime.UnixEpoch.AddSeconds(lastContact.Value),
                Callsign: TrimmedOrNull(state[Callsign]),
                OriginCountry: TrimmedOrNull(state[OriginCountry]),
                Latitude: latitude.Value,
                Longitude: longitude.Value,
                BaroAltitudeM: NullableFloat(state[BaroAltitude]),
                GeoAltitudeM: NullableFloat(state[GeoAltitude]),
                VelocityMs: NullableFloat(state[Velocity]),
                TrueTrackDeg: NullableFloat(state[TrueTrack]),
                VerticalRateMs: NullableFloat(state[VerticalRate]),
                OnGround: onGround,
                Squawk: TrimmedOrNull(state[Squawk]),
                PositionSource: state[PositionSource].ValueKind == JsonValueKind.Number
                    ? state[PositionSource].GetInt16()
                    : null);

            reason = default;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            // A field had an unexpected JSON type (e.g. a string where a number belongs).
            reason = RejectReason.Malformed;
            return false;
        }
    }

    private static bool IsIcao24(string value) =>
        value.Length == 6 && value.All(char.IsAsciiHexDigit);

    // Callsigns arrive space-padded to 8 characters ("KLM1234 ") or empty.
    private static string? TrimmedOrNull(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString()!.Trim();
        return value.Length == 0 ? null : value;
    }

    private static double? NullableDouble(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetDouble();

    private static float? NullableFloat(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetSingle();

    private static long? NullableLong(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt64();
}
