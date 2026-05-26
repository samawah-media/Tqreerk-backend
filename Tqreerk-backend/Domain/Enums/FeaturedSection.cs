namespace Taqreerk.Domain.Enums;

/// Sections of the public site that admins can curate. Stored as an int
/// in Postgres — adding new values is fine but reordering breaks the
/// existing rows, so append new sections at the end.
public enum FeaturedSection
{
    /// Single big card at the top of the homepage. Typically holds the
    /// most-prominent / time-sensitive report. Capacity is small (1–3).
    HomepageHero = 0,

    /// Carousel strip below the hero on the homepage. Holds 5–10 picks
    /// the editor wants to surface broadly.
    HomepageCarousel = 1,

    /// Pinned slot inside a sector's landing page. The sector is implied
    /// by the report's own SectorId.
    SectorTop = 2,

    /// Pinned slot inside a country's landing page. The country is
    /// implied by the report's own CountryId.
    CountryTop = 3,
}
