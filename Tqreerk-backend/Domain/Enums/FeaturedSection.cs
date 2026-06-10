namespace Taqreerk.Domain.Enums;

/// Sections of the public site that admins can curate. Stored as an int
/// in Postgres — adding new values is fine but reordering breaks the
/// existing rows, so append new sections at the end.
public enum FeaturedSection
{
    /// Cognitive-flow hero on the homepage (التدفق المعرفي). Up to 4 picks.
    HomepageHero = 0,

    /// Most-popular carousel on the homepage (التقارير الأكثر رواجاً). Up to 4 picks.
    HomepageCarousel = 1,

    /// Pinned slot inside a sector's landing page. The sector is implied
    /// by the report's own SectorId.
    SectorTop = 2,

    /// Pinned slot inside a country's landing page. The country is
    /// implied by the report's own CountryId.
    CountryTop = 3,
}
