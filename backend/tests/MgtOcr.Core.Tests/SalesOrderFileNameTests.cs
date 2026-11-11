using MgtOcr.Core;

namespace MgtOcr.Core.Tests;

public class SalesOrderFileNameTests
{
    [Fact]
    public void Builds_po_and_yymmdd_delivery_date() =>
        Assert.Equal("PO12345_261115.pdf", SalesOrderFileName.Build("PO12345", "2026-11-15", "scan 001.PDF"));

    [Fact]
    public void Replaces_characters_not_allowed_in_file_names() =>
        Assert.Equal("PO_123_45_261115.pdf", SalesOrderFileName.Build(" PO/123:45 ", "2026-11-15", "a.pdf"));

    [Theory]
    [InlineData(null, "2026-11-15")]
    [InlineData("", "2026-11-15")]
    [InlineData("PO1", "")]
    [InlineData("PO1", "15/11/2026")]
    public void Returns_null_when_po_or_date_is_missing(string? po, string? date) =>
        Assert.Null(SalesOrderFileName.Build(po, date, "a.pdf"));

    [Fact]
    public void Falls_back_to_the_earliest_line_date_when_the_header_has_none() =>
        Assert.Equal("PO1_261103.pdf",
            SalesOrderFileName.Build("PO1", "", ["2026-11-20", "", null, "2026-11-03"], "a.pdf"));

    [Fact]
    public void Header_date_wins_over_line_dates() =>
        Assert.Equal("PO1_261115.pdf",
            SalesOrderFileName.Build("PO1", "2026-11-15", ["2026-11-03"], "a.pdf"));

    [Fact]
    public void Renaming_an_already_renamed_file_keeps_the_extension() =>
        Assert.Equal("PO2_261201.pdf", SalesOrderFileName.Build("PO2", "2026-12-01", "PO1_261115.pdf"));

    [Fact]
    public void Folder_year_comes_from_the_po_date() =>
        Assert.Equal(2025, SalesOrderFileName.FolderDate("2025-12-28", new DateTime(2026, 1, 5)).Year);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2569-11-01")]
    [InlineData("28/12/2025")]
    public void Folder_year_falls_back_to_the_post_date(string? poDate) =>
        Assert.Equal(2026, SalesOrderFileName.FolderDate(poDate, new DateTime(2026, 11, 11)).Year);
}
