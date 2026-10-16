using System.ComponentModel.DataAnnotations;

namespace Ingestion.Worker.Configuration;

/// <summary>
/// Bound from the OPENSKY_* environment variables (see .env.example).
/// Validated at startup so a missing credential fails the host immediately
/// instead of surfacing as an opaque 401 from the token endpoint.
/// </summary>
public sealed class OpenSkyOptions : IValidatableObject
{
    public const string SectionName = "OpenSky";

    /// <summary>Maximum bounding-box area that still costs 1 OpenSky credit per call.</summary>
    public const double MaxFreeBoundingBoxAreaSquareDegrees = 25.0;

    [Required(AllowEmptyStrings = false)]
    public string ClientId { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ClientSecret { get; init; } = string.Empty;

    [Range(-90, 90)]
    public double BboxLamin { get; init; }

    [Range(-180, 180)]
    public double BboxLomin { get; init; }

    [Range(-90, 90)]
    public double BboxLamax { get; init; }

    [Range(-180, 180)]
    public double BboxLomax { get; init; }

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 30;

    // Not in .env: only the tests (and later the chaos demo) point these at a
    // fake OpenSky server.
    public Uri ApiBaseUrl { get; init; } = new("https://opensky-network.org/api/");

    public Uri AuthBaseUrl { get; init; } = new("https://auth.opensky-network.org/");

    public double BoundingBoxAreaSquareDegrees =>
        Math.Abs(BboxLamax - BboxLamin) * Math.Abs(BboxLomax - BboxLomin);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (BboxLamax <= BboxLamin)
        {
            yield return new ValidationResult(
                "OPENSKY_BBOX_LAMAX must be greater than OPENSKY_BBOX_LAMIN.",
                [nameof(BboxLamax)]);
        }

        if (BboxLomax <= BboxLomin)
        {
            yield return new ValidationResult(
                "OPENSKY_BBOX_LOMAX must be greater than OPENSKY_BBOX_LOMIN.",
                [nameof(BboxLomax)]);
        }

        // Guard the credit budget (PRD D3). Above 25 sq deg OpenSky charges 2-4
        // credits per call, which exhausts the 4000/day allowance before the day ends.
        if (BoundingBoxAreaSquareDegrees > MaxFreeBoundingBoxAreaSquareDegrees)
        {
            yield return new ValidationResult(
                $"Bounding box is {BoundingBoxAreaSquareDegrees:F1} sq deg, above the "
                + $"{MaxFreeBoundingBoxAreaSquareDegrees:F0} sq deg that costs 1 credit per call. "
                + "At 30s polling this exceeds the 4000 credits/day budget. Shrink the box.",
                [nameof(BboxLamax), nameof(BboxLomax)]);
        }
    }
}
