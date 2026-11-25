using System.ComponentModel.DataAnnotations;

namespace DocuWareAdobeSignIntegration.Models;

public class AdobeSignAnchorField
{
    /// <summary>
    /// Text/reference label configured by the signing form.
    /// The Financial_Document_Request template uses fixed measured
    /// coordinates, so this value is retained as field metadata and
    /// is not used to search the PDF text.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string AnchorText { get; set; } = string.Empty;

    /// <summary>
    /// The recipient who should complete this field.
    /// Signer numbering starts at 1 and follows the recipient list
    /// submitted when the agreement is created.
    /// </summary>
    [Range(1, 10)]
    public int SignerNumber { get; set; } = 1;

    /// <summary>
    /// Supported values for the Financial_Document_Request layout:
    /// SIGNATURE, DATE, and TEXT.
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string InputType { get; set; } = "SIGNATURE";

    /// <summary>
    /// Nominal field width in source-PDF points for the fixed template
    /// placement. AdobeSignService converts this to Adobe's rendered
    /// FormFieldLocation coordinate units.
    /// </summary>
    [Range(1, 1000)]
    public int Width { get; set; } = 140;

    /// <summary>
    /// Nominal field height in source-PDF points for the fixed template
    /// placement. AdobeSignService converts this to Adobe's rendered
    /// FormFieldLocation coordinate units.
    /// </summary>
    [Range(1, 1000)]
    public int Height { get; set; } = 24;

    /// <summary>
    /// Optional horizontal adjustment in source-PDF points.
    /// The Financial_Document_Request fixed layout currently uses its
    /// measured column coordinates and does not apply this value.
    /// </summary>
    public int OffsetX { get; set; }

    /// <summary>
    /// Optional vertical adjustment in source-PDF points.
    /// The Financial_Document_Request fixed layout currently uses its
    /// measured line coordinates and does not apply this value.
    /// </summary>
    public int OffsetY { get; set; }

    /// <summary>
    /// Indicates whether the field is mandatory.
    /// </summary>
    public bool Required { get; set; } = true;
}
