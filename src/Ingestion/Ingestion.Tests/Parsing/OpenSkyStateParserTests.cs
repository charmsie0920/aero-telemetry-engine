using System.Text.Json;
using Ingestion.Core;
using Xunit;

namespace Ingestion.Tests.Parsing;

/// <summary>
/// Unit tests for parsing and validation (PRD 5.1). No network, no database.
/// </summary>
public sealed class OpenSkyStateParserTests
{
    // 2025-10-09T08:53:20Z
    private const long TimePosition = 1760000000;

    [Fact]
    public void Maps_every_field_of_a_valid_record()
    {
        var result = OpenSkyStateParser.Parse(Response(State()));

        var vector = Assert.Single(result.Valid);
        Assert.Equal("4ca7b5", vector.Icao24);
        Assert.Equal(new DateTime(2025, 10, 9, 8, 53, 20, DateTimeKind.Utc), vector.ObservedAt);
        Assert.Equal(DateTimeKind.Utc, vector.ObservedAt.Kind);
        Assert.Equal(vector.ObservedAt.AddSeconds(1), vector.LastContact);
        Assert.Equal("KLM1234", vector.Callsign);
        Assert.Equal("Netherlands", vector.OriginCountry);
        Assert.Equal(52.3, vector.Latitude);
        Assert.Equal(4.76, vector.Longitude);
        Assert.Equal(10668f, vector.BaroAltitudeM);
        Assert.Equal(10900f, vector.GeoAltitudeM);
        Assert.Equal(230.5f, vector.VelocityMs);
        Assert.Equal(90f, vector.TrueTrackDeg);
        Assert.Equal(-1.5f, vector.VerticalRateMs);
        Assert.False(vector.OnGround);
        Assert.Equal("1000", vector.Squawk);
        Assert.Equal((short)0, vector.PositionSource);
        Assert.Equal(0, result.Invalid);
    }

    [Fact]
    public void Normalises_icao24_to_lowercase()
    {
        var result = OpenSkyStateParser.Parse(Response(State(icao24: "\" 4CA7B5\"")));

        Assert.Equal("4ca7b5", Assert.Single(result.Valid).Icao24);
    }

    [Theory]
    [InlineData("\"        \"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Blank_callsign_becomes_null(string callsign)
    {
        var result = OpenSkyStateParser.Parse(Response(State(callsign: callsign)));

        Assert.Null(Assert.Single(result.Valid).Callsign);
    }

    [Fact]
    public void Keeps_aircraft_on_the_ground()
    {
        // On-ground rows are stored; the training pipeline filters them (P3.2).
        var result = OpenSkyStateParser.Parse(Response(State(onGround: "true")));

        Assert.True(Assert.Single(result.Valid).OnGround);
    }

    [Fact]
    public void Optional_fields_may_be_null()
    {
        var state = $"[\"4ca7b5\",null,null,{TimePosition},null,4.76,52.3,null,false,null,null,null,null,null,null,false,null]";

        var vector = Assert.Single(OpenSkyStateParser.Parse(Response(state)).Valid);

        Assert.Null(vector.LastContact);
        Assert.Null(vector.BaroAltitudeM);
        Assert.Null(vector.VelocityMs);
        Assert.Null(vector.Squawk);
        Assert.Null(vector.PositionSource);
    }

    [Theory]
    [InlineData("time", "null", RejectReason.MissingTimestamp)]
    [InlineData("lat", "null", RejectReason.MissingPosition)]
    [InlineData("lon", "null", RejectReason.MissingPosition)]
    [InlineData("lat", "91.0", RejectReason.OutOfRange)]
    [InlineData("lon", "-180.5", RejectReason.OutOfRange)]
    [InlineData("icao24", "null", RejectReason.InvalidIcao24)]
    [InlineData("icao24", "\"4ca7\"", RejectReason.InvalidIcao24)]
    [InlineData("icao24", "\"zzzzzz\"", RejectReason.InvalidIcao24)]
    [InlineData("onGround", "\"no\"", RejectReason.Malformed)]
    [InlineData("lat", "\"52.3\"", RejectReason.Malformed)]
    public void Rejects_invalid_records_with_a_reason(string field, string value, RejectReason expected)
    {
        var state = field switch
        {
            "time" => State(timePosition: value),
            "lat" => State(latitude: value),
            "lon" => State(longitude: value),
            "icao24" => State(icao24: value),
            "onGround" => State(onGround: value),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var result = OpenSkyStateParser.Parse(Response(state));

        Assert.Empty(result.Valid);
        Assert.Equal(1, result.Invalid);
        Assert.Equal(1, result.Rejected[expected]);
    }

    [Theory]
    [InlineData("[\"4ca7b5\",\"KLM1234\"]")]
    [InlineData("\"not an array\"")]
    [InlineData("null")]
    public void Rejects_malformed_entries(string state)
    {
        var result = OpenSkyStateParser.Parse(Response(state));

        Assert.Equal(1, result.Rejected[RejectReason.Malformed]);
    }

    [Fact]
    public void Bad_records_do_not_lose_the_good_ones()
    {
        var result = OpenSkyStateParser.Parse(Response(
            State(),
            State(icao24: "\"3c6444\"", latitude: "null"),
            State(icao24: "\"3c6445\"", timePosition: "null"),
            State(icao24: "\"3c6446\"")));

        Assert.Equal(4, result.Received);
        Assert.Equal(2, result.Valid.Count);
        Assert.Equal(2, result.Invalid);
    }

    [Theory]
    [InlineData("{\"time\":1760000002,\"states\":null}")]
    [InlineData("{\"time\":1760000002}")]
    [InlineData("{\"time\":1760000002,\"states\":[]}")]
    public void No_aircraft_in_the_box_is_an_empty_result(string json)
    {
        var result = OpenSkyStateParser.Parse(JsonDocument.Parse(json).RootElement);

        Assert.Equal(0, result.Received);
        Assert.Empty(result.Valid);
    }

    /// <summary>One aircraft in OpenSky's positional array format.</summary>
    internal static string State(
        string icao24 = "\"4ca7b5\"",
        string callsign = "\"KLM1234 \"",
        string timePosition = "1760000000",
        string longitude = "4.76",
        string latitude = "52.3",
        string onGround = "false") =>
        $"[{icao24},{callsign},\"Netherlands\",{timePosition},1760000001,{longitude},{latitude},"
        + $"10668.0,{onGround},230.5,90.0,-1.5,null,10900.0,\"1000\",false,0]";

    internal static string ResponseJson(params string[] states) =>
        $"{{\"time\":1760000002,\"states\":[{string.Join(',', states)}]}}";

    private static JsonElement Response(params string[] states) =>
        JsonDocument.Parse(ResponseJson(states)).RootElement;
}
